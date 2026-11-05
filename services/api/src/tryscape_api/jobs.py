"""Creation job routes (technical-spec S13.1-S13.4): create, poll and cancel transcription and scene-proposal jobs.

Work never runs in the request handler: jobs are queued in PostgreSQL and executed by `tryscape_api.worker`, which is
a separate process. Every job operation needs the anonymous-session bearer token, and lookups are scoped to that
session, so an unknown job and another session's job both return 404.
"""

from __future__ import annotations

import hashlib
import json
import uuid
from collections.abc import Callable
from datetime import UTC, datetime, timedelta
from typing import Annotated, Any

from fastapi import APIRouter, Depends, Header, Request, Response
from fastapi.responses import JSONResponse
from pydantic import ValidationError
from sqlalchemy import func, select, update
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker

from tryscape_api import strict_json
from tryscape_api.audio_store import AudioStore
from tryscape_api.config import Settings
from tryscape_api.contracts.models import SceneProposalJobRequest
from tryscape_api.creation.transcription import WavError, validate_wav
from tryscape_api.db import AnonymousSession, Consent, IdempotencyRecord, Job
from tryscape_api.errors import ApiError, ErrorCode
from tryscape_api.sessions import hash_token

NO_STORE = {"Cache-Control": "no-store"}
ACTIVE = ("queued", "running", "cancel_requested")
TERMINAL = ("succeeded", "failed", "cancelled", "expired")
SUPPORTED_CATALOG_VERSIONS = frozenset({"1.0.0"})
PROPOSAL_PATH = "/v1/scene-proposal-jobs"
TRANSCRIPTION_PATH = "/v1/transcription-jobs"
SUPPORTED_LOCALES = frozenset({"en"})
POLL_AFTER_MS = 1000


async def _db(request: Request) -> Any:
    maker: async_sessionmaker[AsyncSession] = request.app.state.sessionmaker
    async with maker() as session:
        yield session


Db = Annotated[AsyncSession, Depends(_db)]


def _now() -> datetime:
    return datetime.now(UTC)


async def authenticate(db: AsyncSession, authorization: str | None, *, lock: bool = False) -> AnonymousSession:
    if not authorization or not authorization.startswith("Bearer "):
        raise ApiError(ErrorCode.UNAUTHENTICATED)
    token = authorization[len("Bearer ") :].strip()
    if not token or len(token) > 128 or not token.isascii():
        raise ApiError(ErrorCode.UNAUTHENTICATED)
    query = select(AnonymousSession).where(
        AnonymousSession.token_hash == hash_token(token), AnonymousSession.expires_at > _now()
    )
    if lock:
        query = query.with_for_update()  # serialises creation per session (rate limit / one active job)
    session = (await db.execute(query)).scalar_one_or_none()
    if session is None:
        raise ApiError(ErrorCode.UNAUTHENTICATED)
    return session


def _parse_uuid(raw: str | None, field: str) -> uuid.UUID:
    try:
        parsed = uuid.UUID(raw or "")
    except ValueError as exc:
        raise ApiError(ErrorCode.INVALID_REQUEST, details=[{"field": field, "code": "invalid_uuid"}]) from exc
    if str(parsed) != (raw or "").lower():
        raise ApiError(ErrorCode.INVALID_REQUEST, details=[{"field": field, "code": "invalid_uuid"}])
    return parsed


def job_view(job: Job) -> dict[str, Any]:
    """Wire shape `api.schema.json#/$defs/job`."""
    succeeded = job.status == "succeeded"
    error = None
    if job.status in ("failed", "expired"):
        code = job.error_code or ("deadline_exceeded" if job.status == "expired" else "internal_error")
        error = {"code": code, "retryable": code in ("provider_unavailable", "deadline_exceeded")}
    return {
        "job_id": str(job.id),
        "status": job.status,
        "kind": job.kind,
        "result_schema_version": "1.0.0" if succeeded else None,
        "result": job.result if succeeded else None,
        "error": error,
    }


def _require_consent(settings: Settings, header: str | None) -> str:
    consent = (header or "").strip()
    if not consent or len(consent) > 64 or consent not in settings.accepted_consent_versions:
        raise ApiError(
            ErrorCode.VALIDATION_FAILED,
            message="Processing consent for the current notice is required.",
            details=[{"field": "x-processing-consent-version", "code": "consent_required"}],
        )
    return consent


async def _enqueue(
    db: AsyncSession,
    settings: Settings,
    session: AnonymousSession,
    *,
    path: str,
    kind: str,
    key: uuid.UUID,
    request_hash: bytes,
    consent: str,
    consent_purpose: str,
    input_payload: dict[str, Any],
    store_input: Callable[[], str] | None = None,
) -> JSONResponse:
    """Shared creation path: idempotency, per-session limits, consent record, job and idempotency rows.

    The caller holds the session row lock (authenticate(lock=True)), so the limit checks are serialised per session.
    `store_input` (audio) runs only once a new job is certain, so a replay or rejection never stores anything.
    """
    now = _now()
    existing = (
        await db.execute(
            select(IdempotencyRecord, Job)
            .join(Job, Job.id == IdempotencyRecord.job_id)
            .where(
                IdempotencyRecord.session_id == session.id,
                IdempotencyRecord.method == "POST",
                IdempotencyRecord.path == path,
                IdempotencyRecord.idempotency_key == key,
                IdempotencyRecord.expires_at > now,
            )
        )
    ).first()
    if existing is not None:
        record, job = existing
        if record.request_hash != request_hash:
            raise ApiError(ErrorCode.IDEMPOTENCY_CONFLICT, message="This idempotency key was used for another request.")
        return JSONResponse(
            {"job_id": str(job.id), "status": job.status, "poll_after_ms": POLL_AFTER_MS},
            status_code=202,
            headers=NO_STORE,
        )

    recent = (
        await db.execute(
            select(func.count())
            .select_from(Job)
            .where(Job.session_id == session.id, Job.created_at > now - timedelta(seconds=60))
        )
    ).scalar_one()
    if recent >= settings.jobs_per_minute_per_session:
        raise ApiError(ErrorCode.RATE_LIMITED, headers={"Retry-After": "60"})
    active = (
        await db.execute(
            select(func.count())
            .select_from(Job)
            .where(Job.session_id == session.id, Job.kind == kind, Job.status.in_(ACTIVE))
        )
    ).scalar_one()
    if active:
        raise ApiError(
            ErrorCode.RATE_LIMITED,
            message="A job of this kind is already in progress for this session.",
            headers={"Retry-After": "2"},
        )

    has_consent = (
        await db.execute(
            select(func.count())
            .select_from(Consent)
            .where(
                Consent.session_id == session.id,
                Consent.purpose == consent_purpose,
                Consent.policy_version == consent,
                Consent.revoked_at.is_(None),
            )
        )
    ).scalar_one()
    if not has_consent:
        db.add(Consent(session_id=session.id, purpose=consent_purpose, policy_version=consent))

    input_ref = store_input() if store_input is not None else None
    try:
        job = Job(
            id=uuid.uuid4(),
            session_id=session.id,
            kind=kind,
            status="queued",
            request_hash=request_hash,
            input_ref=input_ref,
            input_payload=input_payload,
            deadline_at=now + timedelta(seconds=settings.job_deadline_seconds),
            expires_at=now + timedelta(seconds=settings.job_result_ttl_seconds),
            attempts=0,
        )
        db.add(job)
        await db.flush()
        db.add(
            IdempotencyRecord(
                session_id=session.id,
                method="POST",
                path=path,
                idempotency_key=key,
                request_hash=request_hash,
                job_id=job.id,
                expires_at=now + timedelta(hours=24),
            )
        )
        await db.commit()
    except BaseException:
        if input_ref is not None:
            AudioStore(settings.audio_dir).delete(input_ref)
        raise
    return JSONResponse(
        {"job_id": str(job.id), "status": "queued", "poll_after_ms": POLL_AFTER_MS},
        status_code=202,
        headers=NO_STORE,
    )


def build_router(settings: Settings) -> APIRouter:
    router = APIRouter()
    audio = AudioStore(settings.audio_dir)

    @router.post(PROPOSAL_PATH, status_code=202)
    async def create_scene_proposal_job(
        request: Request,
        db: Db,
        authorization: Annotated[str | None, Header()] = None,
        idempotency_key: Annotated[str | None, Header()] = None,
        x_processing_consent_version: Annotated[str | None, Header()] = None,
    ) -> JSONResponse:
        session = await authenticate(db, authorization, lock=True)
        content_type = request.headers.get("content-type", "").split(";")[0].strip().lower()
        if content_type != "application/json":
            raise ApiError(ErrorCode.UNSUPPORTED_MEDIA)
        key = _parse_uuid(idempotency_key, "idempotency_key")
        consent = _require_consent(settings, x_processing_consent_version)
        raw = await request.body()
        try:
            body = strict_json.loads(raw)
        except strict_json.StrictJsonError as exc:
            raise ApiError(ErrorCode.INVALID_REQUEST, details=[{"field": "body", "code": str(exc)}]) from exc
        try:
            job_request = SceneProposalJobRequest.model_validate(body)
        except ValidationError as exc:
            details = [
                {"field": ".".join(str(p) for p in e["loc"])[:128] or "body", "code": "invalid_value"}
                for e in exc.errors()
            ]
            raise ApiError(ErrorCode.VALIDATION_FAILED, details=details) from exc
        if job_request.catalog_version not in SUPPORTED_CATALOG_VERSIONS:
            raise ApiError(
                ErrorCode.VALIDATION_FAILED,
                details=[{"field": "catalog_version", "code": "unsupported_catalog_version"}],
            )
        request_hash = hashlib.sha256(
            json.dumps(job_request.model_dump(), sort_keys=True, separators=(",", ":")).encode()
        ).digest()
        return await _enqueue(
            db,
            settings,
            session,
            path=PROPOSAL_PATH,
            kind="scene_proposal",
            key=key,
            request_hash=request_hash,
            consent=consent,
            consent_purpose="remote_transcript_processing",
            input_payload=job_request.model_dump(),
        )

    @router.post(TRANSCRIPTION_PATH, status_code=202)
    async def create_transcription_job(
        request: Request,
        db: Db,
        authorization: Annotated[str | None, Header()] = None,
        idempotency_key: Annotated[str | None, Header()] = None,
        x_processing_consent_version: Annotated[str | None, Header()] = None,
        content_language: Annotated[str | None, Header()] = None,
    ) -> JSONResponse:
        """Raw PCM WAV body (S13.2/S13.3). The bytes are validated before anything is stored or queued."""
        session = await authenticate(db, authorization, lock=True)
        content_type = request.headers.get("content-type", "").split(";")[0].strip().lower()
        if content_type not in ("audio/wav", "audio/x-wav", "audio/wave"):
            raise ApiError(ErrorCode.UNSUPPORTED_MEDIA)
        key = _parse_uuid(idempotency_key, "idempotency_key")
        locale = (content_language or "").strip().lower()
        if locale not in SUPPORTED_LOCALES:
            raise ApiError(
                ErrorCode.VALIDATION_FAILED, details=[{"field": "content-language", "code": "unsupported_locale"}]
            )
        consent = _require_consent(settings, x_processing_consent_version)
        data = await request.body()
        try:
            validate_wav(data)
        except WavError as exc:
            code = ErrorCode.PAYLOAD_TOO_LARGE if exc.code == "audio_too_large" else ErrorCode.VALIDATION_FAILED
            raise ApiError(code, details=[{"field": "body", "code": exc.code}]) from exc
        request_hash = hashlib.sha256(locale.encode() + b"\x00" + data).digest()
        return await _enqueue(
            db,
            settings,
            session,
            path=TRANSCRIPTION_PATH,
            kind="transcription",
            key=key,
            request_hash=request_hash,
            consent=consent,
            consent_purpose="remote_audio_processing",
            input_payload={"locale": locale},
            store_input=lambda: audio.put(data),
        )

    async def _owned_job(db: AsyncSession, session: AnonymousSession, job_id: str, *, lock: bool) -> Job:
        try:
            parsed = uuid.UUID(job_id)
        except ValueError as exc:
            raise ApiError(ErrorCode.NOT_FOUND) from exc
        query = select(Job).where(Job.id == parsed, Job.session_id == session.id)
        if lock:
            query = query.with_for_update()
        job = (await db.execute(query)).scalar_one_or_none()
        if job is None:
            raise ApiError(ErrorCode.NOT_FOUND)
        return job

    @router.get("/v1/jobs/{job_id}")
    async def get_job(job_id: str, db: Db, authorization: Annotated[str | None, Header()] = None) -> JSONResponse:
        session = await authenticate(db, authorization)
        job = await _owned_job(db, session, job_id, lock=False)
        if job.status in ACTIVE and job.deadline_at <= _now():
            # Lazy expiry; conditional so a concurrent worker completion or cancellation wins consistently.
            # Capture the audio reference first: the ORM update synchronises the loaded row to NULL.
            stored = job.input_ref
            expired = await db.execute(
                update(Job)
                .where(Job.id == job.id, Job.status.in_(ACTIVE))
                .values(
                    status="expired",
                    error_code="deadline_exceeded",
                    input_payload=None,
                    input_ref=None,
                    lease_owner=None,
                )
            )
            await db.commit()
            if expired.rowcount == 1:  # type: ignore[attr-defined]
                audio.delete(stored)
            await db.refresh(job)
        return JSONResponse(job_view(job), headers=NO_STORE)

    @router.delete("/v1/jobs/{job_id}")
    async def cancel_job(job_id: str, db: Db, authorization: Annotated[str | None, Header()] = None) -> Response:
        session = await authenticate(db, authorization)
        job = await _owned_job(db, session, job_id, lock=True)
        if job.status in TERMINAL:
            return Response(status_code=204, headers=NO_STORE)
        stored = None
        if job.status == "queued":
            stored = job.input_ref
            job.status = "cancelled"
            job.input_payload = None
            job.input_ref = None
        elif job.status == "running":
            job.status = "cancel_requested"  # the worker deletes the audio when it observes this
        await db.commit()
        audio.delete(stored)
        return Response(status_code=202, headers=NO_STORE)

    return router
