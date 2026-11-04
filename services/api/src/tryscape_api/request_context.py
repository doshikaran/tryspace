"""Per-request context (request ID) and transport middleware: X-Request-ID, body limits, access log."""

from __future__ import annotations

import json
import logging
import time
import uuid
from contextvars import ContextVar

from starlette.types import ASGIApp, Message, Receive, Scope, Send

_request_id: ContextVar[str] = ContextVar("tryscape_request_id", default="")
access_logger = logging.getLogger("tryscape_api.access")


def current_request_id() -> str:
    return _request_id.get() or str(uuid.uuid4())


def _validated_request_id(raw: bytes | None) -> str:
    if raw:
        try:
            parsed = uuid.UUID(raw.decode("ascii"))
        except (UnicodeDecodeError, ValueError):
            pass
        else:
            return str(parsed)
    return str(uuid.uuid4())


class RequestContextMiddleware:
    """Assigns a validated request ID, enforces a JSON body limit before parsing, and logs metadata only."""

    def __init__(self, app: ASGIApp, json_body_limit_bytes: int) -> None:
        self._app = app
        self._limit = json_body_limit_bytes

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http":
            await self._app(scope, receive, send)
            return

        headers = dict(scope.get("headers", []))
        request_id = _validated_request_id(headers.get(b"x-request-id"))
        token = _request_id.set(request_id)
        started = time.perf_counter()
        status_holder = {"status": 500}

        async def send_with_id(message: Message) -> None:
            if message["type"] == "http.response.start":
                status_holder["status"] = message["status"]
                message.setdefault("headers", [])
                message["headers"] = [
                    *message["headers"],
                    (b"x-request-id", request_id.encode("ascii")),
                ]
            await send(message)

        try:
            declared = headers.get(b"content-length")
            if declared is not None and (not declared.isdigit() or int(declared) > self._limit):
                await self._reject_too_large(send_with_id, request_id)
                return

            received = 0

            async def bounded_receive() -> Message:
                nonlocal received
                message = await receive()
                if message["type"] == "http.request":
                    received += len(message.get("body", b""))
                    if received > self._limit:
                        raise _BodyTooLarge
                return message

            try:
                await self._app(scope, bounded_receive, send_with_id)
            except _BodyTooLarge:
                await self._reject_too_large(send_with_id, request_id)
        finally:
            access_logger.info(
                "request method=%s path=%s status=%s duration_ms=%.1f request_id=%s",
                scope.get("method"),
                scope.get("path"),
                status_holder["status"],
                (time.perf_counter() - started) * 1000,
                request_id,
            )
            _request_id.reset(token)

    @staticmethod
    async def _reject_too_large(send: Send, request_id: str) -> None:
        body = json.dumps(
            {
                "error": {
                    "code": "payload_too_large",
                    "message": "The request body is too large.",
                    "request_id": request_id,
                    "retryable": False,
                    "details": [],
                }
            }
        ).encode()
        await send(
            {
                "type": "http.response.start",
                "status": 413,
                "headers": [(b"content-type", b"application/json"), (b"content-length", str(len(body)).encode())],
            }
        )
        await send({"type": "http.response.body", "body": body})


class _BodyTooLarge(Exception):
    pass
