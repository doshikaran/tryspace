"""R0 persistence schema (technical-spec S14.2): anonymous sessions, jobs, idempotency records, consents.

R1 account/project tables and the owner-or-session CHECK constraint are intentionally absent; they arrive in an
R1 expand migration. Schema changes go through Alembic (migrations/), never `create_all` at runtime.
"""

from __future__ import annotations

import uuid
from datetime import datetime
from typing import Any

from sqlalchemy import (
    CheckConstraint,
    DateTime,
    ForeignKey,
    Index,
    Integer,
    LargeBinary,
    MetaData,
    String,
    UniqueConstraint,
    func,
)
from sqlalchemy.dialects.postgresql import JSONB, UUID
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column

NAMING = {
    "ix": "ix_%(table_name)s_%(column_0_N_name)s",
    "uq": "uq_%(table_name)s_%(column_0_N_name)s",
    "ck": "ck_%(table_name)s_%(constraint_name)s",
    "fk": "fk_%(table_name)s_%(column_0_name)s_%(referred_table_name)s",
    "pk": "pk_%(table_name)s",
}

JOB_STATUSES = ("queued", "running", "succeeded", "failed", "cancel_requested", "cancelled", "expired")
JOB_KINDS = ("scene_proposal", "transcription")


class Base(DeclarativeBase):
    metadata = MetaData(naming_convention=NAMING)


class AnonymousSession(Base):
    __tablename__ = "anonymous_sessions"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)
    token_hash: Mapped[bytes] = mapped_column(LargeBinary(32), nullable=False, unique=True)
    issued_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, index=True)

    __table_args__ = (CheckConstraint("octet_length(token_hash) = 32", name="token_hash_sha256"),)


class Job(Base):
    __tablename__ = "jobs"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)
    session_id: Mapped[uuid.UUID] = mapped_column(
        UUID(as_uuid=True), ForeignKey("anonymous_sessions.id", ondelete="CASCADE"), nullable=False
    )
    kind: Mapped[str] = mapped_column(String(32), nullable=False)
    status: Mapped[str] = mapped_column(String(32), nullable=False, default="queued")
    request_hash: Mapped[bytes] = mapped_column(LargeBinary(32), nullable=False)
    input_ref: Mapped[str | None] = mapped_column(String(128))
    result: Mapped[dict[str, Any] | None] = mapped_column(JSONB)
    error_code: Mapped[str | None] = mapped_column(String(64))
    deadline_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    lease_owner: Mapped[str | None] = mapped_column(String(64))
    lease_expires_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    attempts: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, server_default=func.now(), onupdate=func.now()
    )
    expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)

    __table_args__ = (
        CheckConstraint("status IN (" + ", ".join(f"'{s}'" for s in JOB_STATUSES) + ")", name="status_valid"),
        CheckConstraint("kind IN (" + ", ".join(f"'{k}'" for k in JOB_KINDS) + ")", name="kind_valid"),
        CheckConstraint("attempts >= 0 AND attempts <= 5", name="attempts_bounded"),
        CheckConstraint("octet_length(request_hash) = 32", name="request_hash_sha256"),
        CheckConstraint("result IS NULL OR pg_column_size(result) <= 1048576", name="result_bounded"),
        Index("ix_jobs_worker_scan", "status", "lease_expires_at", "created_at"),
        Index("ix_jobs_session_lookup", "session_id", "id"),
    )


class IdempotencyRecord(Base):
    __tablename__ = "idempotency_records"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)
    session_id: Mapped[uuid.UUID] = mapped_column(
        UUID(as_uuid=True), ForeignKey("anonymous_sessions.id", ondelete="CASCADE"), nullable=False
    )
    method: Mapped[str] = mapped_column(String(8), nullable=False)
    path: Mapped[str] = mapped_column(String(128), nullable=False)
    idempotency_key: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), nullable=False)
    request_hash: Mapped[bytes] = mapped_column(LargeBinary(32), nullable=False)
    job_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("jobs.id", ondelete="CASCADE"))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, index=True)

    __table_args__ = (UniqueConstraint("session_id", "method", "path", "idempotency_key"),)


class Consent(Base):
    """Append-only minimal consent record: no audio, transcript or identity content."""

    __tablename__ = "consents"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)
    session_id: Mapped[uuid.UUID] = mapped_column(
        UUID(as_uuid=True), ForeignKey("anonymous_sessions.id", ondelete="CASCADE"), nullable=False, index=True
    )
    purpose: Mapped[str] = mapped_column(String(32), nullable=False)
    policy_version: Mapped[str] = mapped_column(String(64), nullable=False)
    granted_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    revoked_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))

    __table_args__ = (
        CheckConstraint("purpose IN ('remote_audio_processing', 'remote_transcript_processing')", name="purpose_valid"),
    )
