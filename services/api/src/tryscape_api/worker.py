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
from pathlib import Path
from typing import Any

from sqlalchemy import delete, or_, select, update
from sqlalchemy.ext.asyncio import AsyncEngine, AsyncSession, async_sessionmaker, create_async_engine

from tryscape_api.audio_store import AudioStore
from tryscape_api.config import Settings, get_settings
from tryscape_api.creation.assembler import SceneCompilerError
from tryscape_api.creation.intent import DeterministicIntentExtractor, IntentExtractor
from tryscape_api.creation.proposals import ProposalService
from tryscape_api.creation.transcription import Transcriber, TranscriptionFailed, WavError, WhisperCppTranscriber
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
    input_ref: str | None = None


def configured_transcriber(settings: Settings) -> Transcriber | None:
    """whisper.cpp from operator configuration, or None (transcription jobs then fail `provider_unavailable`)."""
    if not settings.asr_binary or not settings.asr_model:
        return None
    try:
        return WhisperCppTranscriber(
            Path(settings.asr_binary), Path(settings.asr_model), settings.asr_threads, settings.asr_provider_id
        )
    except TranscriptionFailed as exc:
        logger.error("asr_not_available code=%s", exc.code)
        return None


class Worker:
    def __init__(
        self,
        sessionmaker: async_sessionmaker[AsyncSession],
        settings: Settings,
        extractor: IntentExtractor | None = None,
        worker_id: str | None = None,
        clock: Callable[[], datetime] = _now,
        transcriber: Transcriber | None = None,
    ) -> None:
        self._sessions = sessionmaker
        self._settings = settings
        self._proposals = ProposalService(extractor or DeterministicIntentExtractor())
        self._audio = AudioStore(settings.audio_dir)
        self._transcriber = transcriber if transcriber is not None else configured_transcriber(settings)
        self.worker_id = worker_id or f"{socket.gethostname()[:40]}-{uuid.uuid4().hex[:8]}"
        self._clock = clock

    @property
    def has_transcriber(self) -> bool:
        return self._transcriber is not None

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
            if job.deadline_at <= now or job.attempts >= MAX_ATTEMPTS or job.input_payload is None:
                expired = job.deadline_at <= now
                job.status = "expired" if expired else "failed"
                job.error_code = "deadline_exceeded" if expired else "internal_error"
                stored, job.input_payload, job.input_ref, job.lease_owner = job.input_ref, None, None, None
                await db.commit()
                self._audio.delete(stored)
                return None
            job.status = "running"
            job.lease_owner = self.worker_id
            job.lease_expires_at = now + timedelta(seconds=self._settings.worker_lease_seconds)
            job.attempts += 1
            claimed = Claimed(job.id, job.kind, dict(job.input_payload), self.worker_id, job.input_ref)
            await db.commit()
            return claimed

    def execute(self, claimed: Claimed) -> tuple[str, dict[str, Any] | None, str | None]:
        """Returns (status, result, error_code). Never raises for an expected failure."""
        if claimed.kind == "transcription":
            return self._transcribe(claimed)
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

    def _transcribe(self, claimed: Claimed) -> tuple[str, dict[str, Any] | None, str | None]:
        if self._transcriber is None:
            return "failed", None, "provider_unavailable"  # no ASR configured on this host
        data = self._audio.read(claimed.input_ref or "")
        if data is None:
            logger.error("transcription_audio_missing job_id=%s", claimed.job_id)
            return "failed", None, "internal_error"
        try:
            transcript = self._transcriber.transcribe(data, timeout_seconds=self._settings.asr_timeout_seconds)
        except WavError:
            return "failed", None, "invalid_audio"
        except TranscriptionFailed as exc:
            logger.error("transcription_failed job_id=%s code=%s", claimed.job_id, exc.code)
            return "failed", None, "provider_unavailable"
        except Exception as exc:  # provider boundary: safe type only
            logger.error("transcription_failed job_id=%s type=%s", claimed.job_id, type(exc).__name__)
            return "failed", None, "provider_unavailable"
        # Empty text (silence) is a successful transcription; the client asks the user to try again.
        locale = str(claimed.payload.get("locale", "en"))
        return "succeeded", {"text": transcript.text, "locale": locale, "provider_id": transcript.provider_id}, None

    async def complete(self, claimed: Claimed, status: str, result: dict[str, Any] | None, error: str | None) -> str:
        """Conditional completion. Returns the job's resulting status as seen by this worker."""
        now = self._clock()
        async with self._sessions() as db:
            owned = (Job.id == claimed.job_id) & (Job.lease_owner == claimed.lease_owner)
            done = await db.execute(
                update(Job)
                .where(owned, Job.status == "running", Job.deadline_at > now)
                .values(
                    status=status, result=result, error_code=error, input_payload=None, input_ref=None, lease_owner=None
                )
            )
            if done.rowcount == 1:  # type: ignore[attr-defined]
                await db.commit()
                return status
            cancelled = await db.execute(
                update(Job)
                .where(owned, Job.status == "cancel_requested")
                .values(status="cancelled", result=None, input_payload=None, input_ref=None, lease_owner=None)
            )
            if cancelled.rowcount == 1:  # type: ignore[attr-defined]
                await db.commit()
                logger.info("late_result_discarded job_id=%s reason=cancelled", claimed.job_id)
                return "cancelled"
            expired = await db.execute(
                update(Job)
                .where(owned, Job.status == "running", Job.deadline_at <= now)
                .values(
                    status="expired",
                    error_code="deadline_exceeded",
                    result=None,
                    input_payload=None,
                    input_ref=None,
                    lease_owner=None,
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
        # Audio cleanup deadline (S13.3): stored audio older than this is removed whatever its job state.
        self._audio.sweep(self._settings.audio_max_age_seconds)
        return int(gone.rowcount or 0)  # type: ignore[attr-defined]

    async def run_once(self) -> str | None:
        claimed = await self.claim()
        if claimed is None:
            return None
        # Blocking work (ASR subprocess) runs off the event loop.
        status, result, error = await asyncio.to_thread(self.execute, claimed)
        outcome = await self.complete(claimed, status, result, error)
        if outcome != "discarded":  # this worker wrote the terminal state; another owner keeps its input
            self._audio.delete(claimed.input_ref)
        return outcome

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
    logger.info(
        "worker_started id=%s extractor=deterministic_rules asr=%s",
        worker.worker_id,
        settings.asr_provider_id if worker.has_transcriber else "not_configured",
    )
    try:
        await worker.run_forever(stop)
    finally:
        await engine.dispose()
        logger.info("worker_stopped id=%s", worker.worker_id)


if __name__ == "__main__":
    asyncio.run(_main())
