"""Accountless session issuance (technical-spec S13.2). Only a SHA-256 hash of the token is stored; the raw token is
returned once and never logged. Edge/IP issuance limits are a deployment requirement (see docs/progress.md)."""

from __future__ import annotations

import hashlib
import secrets
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta

from sqlalchemy.ext.asyncio import AsyncSession

from tryscape_api.db import AnonymousSession

TOKEN_BYTES = 32  # 256-bit


@dataclass(frozen=True)
class IssuedSession:
    token: str
    expires_at: datetime


def hash_token(token: str) -> bytes:
    return hashlib.sha256(token.encode("ascii")).digest()


async def issue_session(db: AsyncSession, ttl_seconds: int, now: datetime | None = None) -> IssuedSession:
    issued_at = now or datetime.now(UTC)
    token = secrets.token_urlsafe(TOKEN_BYTES)
    expires_at = (issued_at + timedelta(seconds=ttl_seconds)).replace(microsecond=0)
    db.add(AnonymousSession(token_hash=hash_token(token), issued_at=issued_at, expires_at=expires_at))
    await db.commit()
    return IssuedSession(token=token, expires_at=expires_at)
