"""Error envelope (technical-spec S13.3) and exception handlers. Never exposes stack traces or raw provider errors."""

from __future__ import annotations

import logging
from enum import StrEnum

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from starlette.exceptions import HTTPException as StarletteHTTPException

from tryscape_api.request_context import current_request_id

logger = logging.getLogger("tryscape_api")


class ErrorCode(StrEnum):
    INVALID_REQUEST = "invalid_request"
    UNAUTHENTICATED = "unauthenticated"
    NOT_FOUND = "not_found"
    IDEMPOTENCY_CONFLICT = "idempotency_conflict"
    REVISION_CONFLICT = "revision_conflict"
    PAYLOAD_TOO_LARGE = "payload_too_large"
    UNSUPPORTED_MEDIA = "unsupported_media"
    VALIDATION_FAILED = "validation_failed"
    RATE_LIMITED = "rate_limited"
    PROVIDER_UNAVAILABLE = "provider_unavailable"
    DEADLINE_EXCEEDED = "deadline_exceeded"
    INTERNAL_ERROR = "internal_error"


STATUS_BY_CODE: dict[ErrorCode, int] = {
    ErrorCode.INVALID_REQUEST: 400,
    ErrorCode.UNAUTHENTICATED: 401,
    ErrorCode.NOT_FOUND: 404,
    ErrorCode.IDEMPOTENCY_CONFLICT: 409,
    ErrorCode.REVISION_CONFLICT: 412,
    ErrorCode.PAYLOAD_TOO_LARGE: 413,
    ErrorCode.UNSUPPORTED_MEDIA: 415,
    ErrorCode.VALIDATION_FAILED: 422,
    ErrorCode.RATE_LIMITED: 429,
    ErrorCode.PROVIDER_UNAVAILABLE: 503,
    ErrorCode.DEADLINE_EXCEEDED: 504,
    ErrorCode.INTERNAL_ERROR: 500,
}

RETRYABLE = {ErrorCode.RATE_LIMITED, ErrorCode.PROVIDER_UNAVAILABLE, ErrorCode.DEADLINE_EXCEEDED}

DEFAULT_MESSAGES: dict[ErrorCode, str] = {
    ErrorCode.INVALID_REQUEST: "The request could not be read.",
    ErrorCode.UNAUTHENTICATED: "A valid session is required.",
    ErrorCode.NOT_FOUND: "Not found.",
    ErrorCode.PAYLOAD_TOO_LARGE: "The request body is too large.",
    ErrorCode.UNSUPPORTED_MEDIA: "Unsupported content type.",
    ErrorCode.VALIDATION_FAILED: "The request did not match the contract.",
    ErrorCode.INTERNAL_ERROR: "An internal error occurred.",
}


class ApiError(Exception):
    def __init__(
        self,
        code: ErrorCode,
        message: str | None = None,
        details: list[dict[str, str]] | None = None,
        headers: dict[str, str] | None = None,
    ) -> None:
        super().__init__(code.value)
        self.code = code
        self.message = message or DEFAULT_MESSAGES.get(code, code.value.replace("_", " ").capitalize())
        self.details = details or []
        self.headers = headers or {}


def error_response(error: ApiError) -> JSONResponse:
    body = {
        "error": {
            "code": error.code.value,
            "message": error.message[:240],
            "request_id": current_request_id(),
            "retryable": error.code in RETRYABLE,
            "details": error.details[:32],
        }
    }
    return JSONResponse(body, status_code=STATUS_BY_CODE[error.code], headers=error.headers)


def _safe_field(loc: tuple[int | str, ...]) -> str:
    return ".".join(str(part) for part in loc if part != "body")[:128] or "body"


def install_error_handlers(app: FastAPI) -> None:
    @app.exception_handler(ApiError)
    async def _api_error(_: Request, exc: ApiError) -> JSONResponse:
        return error_response(exc)

    @app.exception_handler(RequestValidationError)
    async def _validation(_: Request, exc: RequestValidationError) -> JSONResponse:
        details = [{"field": _safe_field(tuple(e.get("loc", ()))), "code": "invalid_value"} for e in exc.errors()]
        return error_response(ApiError(ErrorCode.VALIDATION_FAILED, details=details))

    @app.exception_handler(StarletteHTTPException)
    async def _http(_: Request, exc: StarletteHTTPException) -> JSONResponse:
        code = {404: ErrorCode.NOT_FOUND, 405: ErrorCode.INVALID_REQUEST}.get(
            exc.status_code, ErrorCode.INVALID_REQUEST
        )
        return error_response(ApiError(code))

    @app.exception_handler(Exception)
    async def _unhandled(_: Request, exc: Exception) -> JSONResponse:
        # Log a safe code and type only; never the request body.
        logger.error("unhandled_error type=%s request_id=%s", type(exc).__name__, current_request_id())
        return error_response(ApiError(ErrorCode.INTERNAL_ERROR))
