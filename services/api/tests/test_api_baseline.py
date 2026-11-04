"""Behavioral tests for the R0 baseline routes: health, anonymous sessions, error envelope, transport limits."""

from __future__ import annotations

import hashlib
import re
import uuid
from datetime import UTC, datetime, timedelta

import pytest
from httpx import ASGITransport, AsyncClient
from pydantic import ValidationError
from sqlalchemy import text
from sqlalchemy.ext.asyncio import AsyncEngine, create_async_engine

from tryscape_api.config import Environment, Settings
from tryscape_api.main import create_app

TOKEN_RE = re.compile(r"^[A-Za-z0-9_-]{43}$")
JSON = {"content-type": "application/json"}


def assert_error(body: dict[str, object], code: str) -> None:
    assert set(body) == {"error"}
    err = body["error"]
    assert isinstance(err, dict)
    assert set(err) == {"code", "message", "request_id", "retryable", "details"}
    assert err["code"] == code
    uuid.UUID(str(err["request_id"]))


async def test_live_does_not_touch_database() -> None:
    # Unroutable DB: live must still be 200, ready must report 503 rather than crash.
    settings = Settings(env=Environment.TEST, database_url="postgresql+psycopg://nobody@127.0.0.1:1/none")  # type: ignore[arg-type]
    engine = create_async_engine(settings.database_url.get_secret_value(), pool_timeout=1)
    app = create_app(settings, engine)
    async with AsyncClient(transport=ASGITransport(app=app), base_url="http://t") as http:
        from tests.conftest import _lifespan

        async with _lifespan(app):
            live = await http.get("/health/live")
            ready = await http.get("/health/ready")
    await engine.dispose()
    assert live.status_code == 200
    assert live.json() == {"status": "live"}
    assert ready.status_code == 503
    assert ready.json()["checks"]["database"] == "unavailable"
    assert "nobody" not in ready.text
    assert "127.0.0.1" not in ready.text


async def test_ready_with_database(client: AsyncClient) -> None:
    resp = await client.get("/health/ready")
    assert resp.status_code == 200
    assert resp.json() == {"status": "ready", "checks": {"database": "ok"}}


async def test_session_issued_and_only_hash_stored(client: AsyncClient, engine: AsyncEngine) -> None:
    before = datetime.now(UTC)
    resp = await client.post("/v1/anonymous-sessions", content=b"{}", headers=JSON)
    assert resp.status_code == 201
    assert resp.headers["cache-control"] == "no-store"
    body = resp.json()
    assert set(body) == {"session_token", "expires_at"}
    token = body["session_token"]
    assert TOKEN_RE.match(token)
    expires = datetime.strptime(body["expires_at"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=UTC)
    assert before + timedelta(minutes=59) <= expires <= before + timedelta(hours=1, seconds=5)

    async with engine.connect() as conn:
        rows = (await conn.execute(text("SELECT token_hash, expires_at FROM anonymous_sessions"))).all()
        dump = (await conn.execute(text("SELECT row_to_json(s)::text FROM anonymous_sessions s"))).scalar_one()
    assert len(rows) == 1
    assert bytes(rows[0].token_hash) == hashlib.sha256(token.encode()).digest()
    assert token not in dump


async def test_sessions_are_distinct(client: AsyncClient) -> None:
    tokens = {
        (await client.post("/v1/anonymous-sessions", content=b"{}", headers=JSON)).json()["session_token"]
        for _ in range(5)
    }
    assert len(tokens) == 5


@pytest.mark.parametrize(
    ("body", "headers", "status", "code"),
    [
        (b'{"debug": true}', JSON, 422, "validation_failed"),
        (b"[]", JSON, 422, "validation_failed"),
        (b"{", JSON, 400, "invalid_request"),
        (b'{"a": 1, "a": 2}', JSON, 400, "invalid_request"),
        (b'{"a": NaN}', JSON, 400, "invalid_request"),
        (b"{}", {"content-type": "text/plain"}, 415, "unsupported_media"),
        (b"{}", {}, 415, "unsupported_media"),
    ],
)
async def test_session_rejects_bad_bodies_without_creating_rows(
    client: AsyncClient, engine: AsyncEngine, body: bytes, headers: dict[str, str], status: int, code: str
) -> None:
    resp = await client.post("/v1/anonymous-sessions", content=body, headers=headers)
    assert resp.status_code == status
    assert_error(resp.json(), code)
    async with engine.connect() as conn:
        assert (await conn.execute(text("SELECT count(*) FROM anonymous_sessions"))).scalar_one() == 0


async def test_body_over_limit_rejected_before_parsing(client: AsyncClient, engine: AsyncEngine) -> None:
    big = b'{"x": "' + b"a" * (16 * 1024) + b'"}'
    resp = await client.post("/v1/anonymous-sessions", content=big, headers=JSON)
    assert resp.status_code == 413
    assert_error(resp.json(), "payload_too_large")
    async with engine.connect() as conn:
        assert (await conn.execute(text("SELECT count(*) FROM anonymous_sessions"))).scalar_one() == 0


async def test_request_id_echoed_when_valid_and_replaced_when_not(client: AsyncClient) -> None:
    rid = str(uuid.uuid4())
    echoed = await client.get("/health/live", headers={"x-request-id": rid})
    assert echoed.headers["x-request-id"] == rid
    replaced = await client.get("/health/live", headers={"x-request-id": "<script>"})
    assert replaced.headers["x-request-id"] != "<script>"
    uuid.UUID(replaced.headers["x-request-id"])


async def test_error_request_id_matches_header(client: AsyncClient) -> None:
    rid = str(uuid.uuid4())
    resp = await client.post("/v1/anonymous-sessions", content=b"{", headers={**JSON, "x-request-id": rid})
    assert resp.json()["error"]["request_id"] == rid == resp.headers["x-request-id"]


async def test_unknown_and_deferred_routes_are_absent(client: AsyncClient) -> None:
    # R1 routes and not-yet-implemented creation jobs must not exist as success stubs.
    for method, path in [
        ("GET", "/v1/nope"),
        ("POST", "/v1/projects"),
        ("POST", "/v1/scene-proposal-jobs"),
        ("POST", "/v1/transcription-jobs"),
        ("GET", f"/v1/jobs/{uuid.uuid4()}"),
    ]:
        resp = await client.request(method, path, content=b"{}", headers=JSON)
        assert resp.status_code == 404, (method, path)
        assert_error(resp.json(), "not_found")


def test_settings_fail_closed(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("TRYSCAPE_DATABASE_URL", raising=False)
    monkeypatch.delenv("TRYSCAPE_ENV", raising=False)
    with pytest.raises(ValidationError):
        Settings()  # type: ignore[call-arg]
    with pytest.raises(ValidationError):
        Settings(env=Environment.TEST, database_url="sqlite:///x.db")  # type: ignore[arg-type]
    with pytest.raises(ValidationError):
        Settings(env="staging", database_url="postgresql+psycopg://localhost/x")  # type: ignore[arg-type]


def test_docs_disabled_outside_development(migrated_database: str) -> None:
    from tryscape_api.main import create_fastapi

    judge = create_fastapi(Settings(env=Environment.JUDGE, database_url=migrated_database))  # type: ignore[arg-type]
    assert judge.openapi_url is None
    assert judge.docs_url is None
