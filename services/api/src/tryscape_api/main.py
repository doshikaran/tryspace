"""FastAPI application factory and ASGI entry point (`tryscape_api.main:app`).

Implemented R0 routes: health (live/ready), anonymous sessions, and scene-proposal jobs (create/poll/cancel; executed
by the separate `tryscape_api.worker` process). The worker's intent extractor is currently the deterministic
development parser (`deterministic_rules`), not a model; transcription jobs are not exposed yet (technical-spec S13.2).
"""

from __future__ import annotations

import logging
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from typing import Annotated

from fastapi import Depends, FastAPI, Request
from fastapi.responses import JSONResponse
from sqlalchemy import text
from sqlalchemy.ext.asyncio import AsyncEngine, AsyncSession, async_sessionmaker, create_async_engine
from starlette.types import ASGIApp

from tryscape_api import strict_json
from tryscape_api.config import Settings, get_settings
from tryscape_api.errors import ApiError, ErrorCode, install_error_handlers
from tryscape_api.jobs import build_router
from tryscape_api.request_context import RequestContextMiddleware
from tryscape_api.sessions import issue_session

logger = logging.getLogger("tryscape_api")
SERVICE_VERSION = "0.1.0"
NO_STORE = {"Cache-Control": "no-store"}


async def db_session(request: Request) -> AsyncIterator[AsyncSession]:
    """Request-scoped database session. Module level so FastAPI can resolve the annotation."""
    maker: async_sessionmaker[AsyncSession] = request.app.state.sessionmaker
    async with maker() as session:
        yield session


DbSession = Annotated[AsyncSession, Depends(db_session)]


def create_fastapi(settings: Settings, engine: AsyncEngine | None = None) -> FastAPI:
    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        owned = engine is None
        db_engine = engine or create_async_engine(
            settings.database_url.get_secret_value(), pool_size=5, max_overflow=5, pool_pre_ping=True, pool_timeout=3
        )
        app.state.engine = db_engine
        app.state.sessionmaker = async_sessionmaker(db_engine, expire_on_commit=False)
        try:
            yield
        finally:
            if owned:
                await db_engine.dispose()

    app = FastAPI(
        title="Tryscape creation API",
        version=SERVICE_VERSION,
        lifespan=lifespan,
        docs_url="/docs" if settings.env.value in ("development", "test") else None,
        redoc_url=None,
        openapi_url="/openapi.json" if settings.env.value in ("development", "test") else None,
    )
    install_error_handlers(app)
    app.include_router(build_router(settings))

    @app.get("/health/live")
    async def health_live() -> dict[str, str]:
        return {"status": "live"}

    @app.get("/health/ready")
    async def health_ready(request: Request) -> JSONResponse:
        db_engine: AsyncEngine = request.app.state.engine
        try:
            async with db_engine.connect() as conn:
                await conn.execute(text("SELECT 1"))
        except Exception as exc:  # readiness reports unavailability rather than raising
            logger.warning("readiness_db_unavailable type=%s", type(exc).__name__)
            return JSONResponse({"status": "unavailable", "checks": {"database": "unavailable"}}, status_code=503)
        return JSONResponse({"status": "ready", "checks": {"database": "ok"}})

    @app.post("/v1/anonymous-sessions", status_code=201)
    async def create_anonymous_session(request: Request, db: DbSession) -> JSONResponse:
        content_type = request.headers.get("content-type", "").split(";")[0].strip().lower()
        if content_type != "application/json":
            raise ApiError(ErrorCode.UNSUPPORTED_MEDIA)
        try:
            body = strict_json.loads(await request.body())
        except strict_json.StrictJsonError as exc:
            raise ApiError(ErrorCode.INVALID_REQUEST, details=[{"field": "body", "code": str(exc)}]) from exc
        if body != {}:
            raise ApiError(ErrorCode.VALIDATION_FAILED, details=[{"field": "body", "code": "must_be_empty_object"}])
        issued = await issue_session(db, settings.session_ttl_seconds)
        return JSONResponse(
            {"session_token": issued.token, "expires_at": issued.expires_at.strftime("%Y-%m-%dT%H:%M:%SZ")},
            status_code=201,
            headers=NO_STORE,
        )

    return app


def create_app(settings: Settings | None = None, engine: AsyncEngine | None = None) -> ASGIApp:
    resolved = settings or get_settings()
    return RequestContextMiddleware(
        create_fastapi(resolved, engine), json_body_limit_bytes=resolved.json_body_limit_bytes
    )


def __getattr__(name: str) -> ASGIApp:
    # Lazy module attribute so importing this module (tests, tooling) does not require environment configuration,
    # while `uvicorn tryscape_api.main:app` still fails closed on missing settings.
    if name == "app":
        return create_app()
    raise AttributeError(name)
