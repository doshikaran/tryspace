"""In-process sliding-window limiter for anonymous-session issuance (technical-spec S13.2).

Single-host R0 only: the counts live in one API process and reset on restart. A multi-instance deployment needs edge or
shared-store enforcement. Neither an IP address nor a session is a trustworthy identity; these limits slow abuse but do
not prevent it (S13.3).
"""

from __future__ import annotations

import time
from collections import deque
from collections.abc import Callable

from starlette.requests import Request


class SlidingWindowLimiter:
    def __init__(
        self, limit: int, window_seconds: float, clock: Callable[[], float] = time.monotonic, max_keys: int = 10000
    ):
        self._limit = limit
        self._window = window_seconds
        self._clock = clock
        self._max_keys = max_keys
        self._hits: dict[str, deque[float]] = {}

    def allow(self, key: str) -> tuple[bool, int]:
        """Records a hit if allowed. Returns (allowed, retry_after_seconds)."""
        now = self._clock()
        hits = self._hits.get(key)
        if hits is None:
            if len(self._hits) >= self._max_keys:  # bounded memory: drop the oldest-touched key
                oldest = min(self._hits, key=lambda k: self._hits[k][-1] if self._hits[k] else 0.0)
                del self._hits[oldest]
            hits = self._hits[key] = deque()
        while hits and hits[0] <= now - self._window:
            hits.popleft()
        if len(hits) >= self._limit:
            return False, max(1, int(hits[0] + self._window - now) + 1)
        hits.append(now)
        return True, 0


def client_ip(request: Request, trust_forwarded_for: bool) -> str:
    if trust_forwarded_for:
        forwarded = request.headers.get("x-forwarded-for", "")
        first = forwarded.split(",")[0].strip()
        if first:
            return first[:64]
    return (request.client.host if request.client else "unknown")[:64]
