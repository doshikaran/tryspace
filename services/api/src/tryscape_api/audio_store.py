"""Temporary job audio (technical-spec S13.3).

On the single R0 host, the API and the worker share one access-restricted directory. Files get random
application-owned names, and only that opaque name enters the job store (`jobs.input_ref`). Audio is deleted when its
job finishes, is cancelled or expires; anything older than the cleanup deadline (at most 15 minutes) is swept
regardless. Audio never enters logs, the database or backups.
"""

from __future__ import annotations

import contextlib
import os
import re
import secrets
import tempfile
import time
from pathlib import Path

_REF = re.compile(r"^aud_[0-9a-f]{32}\.wav$")


class AudioStore:
    def __init__(self, directory: str = "") -> None:
        root = Path(directory) if directory else Path(tempfile.gettempdir()) / f"tryscape-audio-{os.getuid()}"
        root.mkdir(mode=0o700, parents=True, exist_ok=True)
        os.chmod(root, 0o700)
        self.root = root

    def put(self, data: bytes) -> str:
        ref = f"aud_{secrets.token_hex(16)}.wav"
        fd = os.open(self.root / ref, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, "wb") as handle:
            handle.write(data)
        return ref

    def path(self, ref: str) -> Path:
        if not _REF.match(ref):
            raise ValueError("invalid audio reference")
        return self.root / ref

    def read(self, ref: str) -> bytes | None:
        try:
            return self.path(ref).read_bytes()
        except (FileNotFoundError, ValueError):
            return None

    def delete(self, ref: str | None) -> None:
        if not ref:
            return
        with contextlib.suppress(ValueError):
            self.path(ref).unlink(missing_ok=True)

    def sweep(self, max_age_seconds: float, now: float | None = None) -> int:
        """Delete stored audio older than the cleanup deadline. Returns the number of files removed."""
        cutoff = (now if now is not None else time.time()) - max_age_seconds
        removed = 0
        for entry in self.root.iterdir():
            if _REF.match(entry.name) and entry.stat().st_mtime < cutoff:
                entry.unlink(missing_ok=True)
                removed += 1
        return removed
