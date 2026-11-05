"""Durable job worker (technical-spec S13.4). Run as its own process: `uv run python -m tryscape_api.worker`.

- Claim: one queued job, or a running job whose lease expired, via `FOR UPDATE SKIP LOCKED`. The claim (status,
  lease, attempt count) is committed before any work starts.
- Execute: scene-proposal jobs go through `ProposalService`, using the configured extractor.
- Complete: a conditional update keyed on job ID, status `running` and lease owner. If the job was cancelled,
  expired or re-leased meanwhile, the late output is discarded. A `cancel_requested` job becomes `cancelled`
  without its result.
- Housekeeping: job inputs are purged at terminal states. Jobs, with their results and transcripts, are deleted
  after `expires_at`; idempotency records cascade with them.

Execution is at least once; completion writes are idempotent.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import signal
import socket
import uuid
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from typing import Any

from sqlalchemy import delete, or_, select, update
from sqlalchemy.ext.asyncio import AsyncEngine, AsyncSession, async_sessionmaker, create_async_engine

from tryscape_api.config import Settings, get_settings
from tryscape_api.creation.assembler import SceneCompilerError
from tryscape_api.creation.intent import DeterministicIntentExtractor, IntentExtractor
from tryscape_api.creation.proposals import ProposalService
from tryscape_api.db import IdempotencyRecord, Job

logger = logging.getLogger("tryscape_api.worker")
MAX_ATTEMPTS = 2  # first attempt plus at most one retry after a lost lease (S13.4)


def _now() -> datetime:
    return datetime.now(UTC)


@dataclass(frozen=True)
class Claimed:
    job_id: uuid.UUID
    kind: str
    payload: dict[str, Any]
    lease_owner: str


class Worker:
    def __init__(
        self,
        sessionmaker: async_sessionmaker[AsyncSession],
        settings: Settings,
        extractor: IntentExtractor | None = None,
        worker_id: str | None = None,
        clock: Callable[[], datetime] = _now,
    ) -> None:
        self._sessions = sessionmaker
        self._settings = settings
        self._proposals = ProposalService(extractor or DeterministicIntentExtractor())
        self.worker_id = worker_id or f"{socket.gethostname()[:40]}-{uuid.uuid4().hex[:8]}"
        self._clock = clock

    async def claim(self) -> Claimed | None:
        now = self._clock()
        async with self._sessions() as db:
            job = (
                await db.execute(
                    select(Job)
                    .where(
                        or_(
                            Job.status == "queued",
                            (Job.status == "running") & (Job.lease_expires_at < now),
                        )
                    )
                    .order_by(Job.created_at)
                    .with_for_update(skip_locked=True)
                    .limit(1)
                )
            ).scalar_one_or_none()
            if job is None:
                return None
            if job.deadline_at <= now:
                job.status, job.error_code, job.input_payload, job.lease_owner = (
                    "expired",
                    "deadline_exceeded",
                    None,
                    None,
                )
                await db.commit()
                return None
            if job.attempts >= MAX_ATTEMPTS or job.input_payload is None:
                job.status, job.error_code, job.input_payload, job.lease_owner = "failed", "internal_error", None, None
                await db.commit()
                return None
            job.status = "running"
            job.lease_owner = self.worker_id
            job.lease_expires_at = now + timedelta(seconds=self._settings.worker_lease_seconds)
            job.attempts += 1
            claimed = Claimed(job.id, job.kind, dict(job.input_payload), self.worker_id)
            await db.commit()
            return claimed

    def execute(self, claimed: Claimed) -> tuple[str, dict[str, Any] | None, str | None]:
        """Returns (status, result, error_code). Never raises for an expected failure."""
        if claimed.kind != "scene_proposal":
            return "failed", None, "internal_error"
        try:
            proposal = self._proposals.propose(str(claimed.payload["transcript"]))
        except SceneCompilerError as exc:
            logger.error("scene_compiler_invariant job_id=%s code=%s", claimed.job_id, exc.code)
            return "failed", None, "internal_error"
        except Exception as exc:  # provider-boundary failure: safe code only, never raw provider text
            logger.error("proposal_failed job_id=%s type=%s", claimed.job_id, type(exc).__name__)
            return "failed", None, "provider_unavailable"
        return "succeeded", proposal.model_dump(), None

    async def complete(self, claimed: Claimed, status: str, result: dict[str, Any] | None, error: str | None) -> str:
        """Conditional completion. Returns the job's resulting status as seen by this worker."""
        now = self._clock()
        async with self._sessions() as db:
            owned = (Job.id == claimed.job_id) & (Job.lease_owner == claimed.lease_owner)
            done = await db.execute(
                update(Job)
                .where(owned, Job.status == "running", Job.deadline_at > now)
                .values(status=status, result=result, error_code=error, input_payload=None, lease_owner=None)
            )
            if done.rowcount == 1:  # type: ignore[attr-defined]
                await db.commit()
                return status
            cancelled = await db.execute(
                update(Job)
                .where(owned, Job.status == "cancel_requested")
                .values(status="cancelled", result=None, input_payload=None, lease_owner=None)
            )
            if cancelled.rowcount == 1:  # type: ignore[attr-defined]
                await db.commit()
                logger.info("late_result_discarded job_id=%s reason=cancelled", claimed.job_id)
                return "cancelled"
            expired = await db.execute(
                update(Job)
                .where(owned, Job.status == "running", Job.deadline_at <= now)
                .values(
                    status="expired", error_code="deadline_exceeded", result=None, input_payload=None, lease_owner=None
                )
            )
            await db.commit()
            if expired.rowcount == 1:  # type: ignore[attr-defined]
                logger.info("late_result_discarded job_id=%s reason=deadline", claimed.job_id)
                return "expired"
            logger.info("late_result_discarded job_id=%s reason=not_owner", claimed.job_id)
            return "discarded"

    async def purge_expired(self) -> int:
        now = self._clock()
        async with self._sessions() as db:
            await db.execute(delete(IdempotencyRecord).where(IdempotencyRecord.expires_at <= now))
            gone = await db.execute(delete(Job).where(Job.expires_at <= now))
            await db.commit()
            return int(gone.rowcount or 0)  # type: ignore[attr-defined]

    async def run_once(self) -> str | None:
        claimed = await self.claim()
        if claimed is None:
            return None
        status, result, error = self.execute(claimed)
        return await self.complete(claimed, status, result, error)

    async def run_forever(self, stop: asyncio.Event) -> None:
        last_purge = 0.0
        loop = asyncio.get_running_loop()
        while not stop.is_set():
            try:
                outcome = await self.run_once()
                if loop.time() - last_purge > 60:
                    await self.purge_expired()
                    last_purge = loop.time()
            except Exception as exc:  # keep the worker alive across transient database errors
                logger.error("worker_iteration_failed type=%s", type(exc).__name__)
                outcome = None
            if outcome is None:
                with contextlib.suppress(TimeoutError):
                    await asyncio.wait_for(stop.wait(), timeout=self._settings.worker_poll_seconds)


async def _main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    settings = get_settings()
    engine: AsyncEngine = create_async_engine(settings.database_url.get_secret_value(), pool_size=2, pool_pre_ping=True)
    worker = Worker(async_sessionmaker(engine, expire_on_commit=False), settings)
    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, stop.set)
    logger.info("worker_started id=%s extractor=deterministic_rules", worker.worker_id)
    try:
        await worker.run_forever(stop)
    finally:
        await engine.dispose()
        logger.info("worker_stopped id=%s", worker.worker_id)


if __name__ == "__main__":
    asyncio.run(_main())
