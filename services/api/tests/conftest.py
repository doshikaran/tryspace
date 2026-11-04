"""Test fixtures. Database tests run against real PostgreSQL (technical-spec S18.2), not SQLite.

TRYSCAPE_TEST_DATABASE_URL selects the server (default: local socket, database `tryscape_test`). The database is
dropped and recreated per test session and migrated with Alembic, so migrations are exercised on every run.
If PostgreSQL is unreachable the tests fail rather than skip.
"""

from __future__ import annotations

import os
from collections.abc import AsyncIterator
from pathlib import Path

import pytest
from alembic import command
from alembic.config import Config
from httpx import ASGITransport, AsyncClient
from sqlalchemy import create_engine, make_url, text
from sqlalchemy.ext.asyncio import AsyncEngine, create_async_engine

from tryscape_api.config import Environment, Settings
from tryscape_api.main import create_app

API_ROOT = Path(__file__).resolve().parent.parent
REPO_ROOT = API_ROOT.parent.parent
DEFAULT_URL = "postgresql+psycopg://localhost/tryscape_test"


def database_url() -> str:
    return os.environ.get("TRYSCAPE_TEST_DATABASE_URL", DEFAULT_URL)


def alembic_config(url: str) -> Config:
    cfg = Config(str(API_ROOT / "alembic.ini"))
    cfg.set_main_option("script_location", str(API_ROOT / "migrations"))
    os.environ["TRYSCAPE_DATABASE_URL"] = url
    return cfg


def _recreate_database(url: str) -> None:
    parsed = make_url(url)
    name = parsed.database
    if not name or not name.endswith("_test"):
        raise RuntimeError("refusing to recreate a database whose name does not end in _test")
    admin = create_engine(parsed.set(database="postgres"), isolation_level="AUTOCOMMIT")
    with admin.connect() as conn:
        conn.execute(text(f'DROP DATABASE IF EXISTS "{name}" WITH (FORCE)'))
        conn.execute(text(f'CREATE DATABASE "{name}"'))
    admin.dispose()


@pytest.fixture(scope="session")
def migrated_database() -> str:
    url = database_url()
    _recreate_database(url)
    command.upgrade(alembic_config(url), "head")
    return url


@pytest.fixture
def settings(migrated_database: str) -> Settings:
    return Settings(env=Environment.TEST, database_url=migrated_database)  # type: ignore[arg-type]


@pytest.fixture
async def engine(migrated_database: str) -> AsyncIterator[AsyncEngine]:
    eng = create_async_engine(migrated_database)
    async with eng.begin() as conn:
        await conn.execute(
            text("TRUNCATE idempotency_records, jobs, consents, anonymous_sessions RESTART IDENTITY CASCADE")
        )
    yield eng
    await eng.dispose()


@pytest.fixture
async def client(settings: Settings, engine: AsyncEngine) -> AsyncIterator[AsyncClient]:
    app = create_app(settings, engine)
    transport = ASGITransport(app=app)
    # ASGITransport does not run lifespan; drive it explicitly so app state matches production startup.
    async with AsyncClient(transport=transport, base_url="http://testserver") as http, _lifespan(app):
        yield http


class _lifespan:
    """Minimal ASGI lifespan driver for tests."""

    def __init__(self, app: object) -> None:
        import asyncio

        self._app = app
        self._queue: asyncio.Queue[dict[str, str]] = asyncio.Queue()
        self._sent: asyncio.Queue[dict[str, str]] = asyncio.Queue()
        self._task: asyncio.Task[None] | None = None

    async def __aenter__(self) -> None:
        import asyncio

        async def receive() -> dict[str, str]:
            return await self._queue.get()

        async def send(message: dict[str, str]) -> None:
            await self._sent.put(message)

        self._task = asyncio.create_task(self._app({"type": "lifespan"}, receive, send))  # type: ignore[operator]
        await self._queue.put({"type": "lifespan.startup"})
        started = await self._sent.get()
        assert started["type"] == "lifespan.startup.complete", started

    async def __aexit__(self, *_: object) -> None:
        await self._queue.put({"type": "lifespan.shutdown"})
        await self._sent.get()
        assert self._task is not None
        await self._task
