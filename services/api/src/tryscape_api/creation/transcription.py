"""Speech-to-text behind a provider-neutral interface (technical-spec S10.2, S13.3), kept separate from intent.

- `validate_wav` enforces the R0 audio contract on the actual bytes: PCM WAV, mono, 16 kHz, 16-bit, at most 15 s
  and 512 KiB. A file extension or Content-Length is never trusted.
- `WhisperCppTranscriber` runs a pinned whisper.cpp `whisper-cli` binary as a subprocess:
  - fixed argv, no shell, so transcript or audio content can never reach a command line;
  - bounded runtime;
  - audio written to a private temporary file that is always deleted afterwards.

Phase 3 status: this is the feasibility-spike integration, used by scripts/asr_spike.py on the developer Mac. No
`/v1/transcription-jobs` route uses it yet, and no deployed host runs it.
"""

from __future__ import annotations

import os
import re
import struct
import subprocess
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

SAMPLE_RATE_HZ = 16000
MAX_SECONDS = 15
MAX_BYTES = 512 * 1024
MAX_TRANSCRIPT_CHARS = 2000


class WavError(ValueError):
    """The audio does not meet the R0 WAV contract. `code` is a safe slug."""

    def __init__(self, code: str) -> None:
        super().__init__(code)
        self.code = code


@dataclass(frozen=True)
class WavInfo:
    frames: int

    @property
    def seconds(self) -> float:
        return self.frames / SAMPLE_RATE_HZ


def validate_wav(data: bytes) -> WavInfo:
    if len(data) > MAX_BYTES:
        raise WavError("audio_too_large")
    if len(data) < 44 or data[0:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise WavError("not_wav")
    offset = 12
    fmt: tuple[int, int, int, int] | None = None
    frames: int | None = None
    for _ in range(16):  # bounded chunk walk
        if offset + 8 > len(data):
            break
        chunk_id = data[offset : offset + 4]
        (size,) = struct.unpack_from("<I", data, offset + 4)
        body = offset + 8
        if body + size > len(data):
            raise WavError("truncated_chunk")
        if chunk_id == b"fmt ":
            if size < 16:
                raise WavError("bad_format_chunk")
            audio_format, channels, rate, _, _, bits = struct.unpack_from("<HHIIHH", data, body)
            fmt = (audio_format, channels, rate, bits)
        elif chunk_id == b"data":
            if fmt is None:
                raise WavError("data_before_format")
            frames = size // 2
            if size % 2:
                raise WavError("partial_frame")
            break
        offset = body + size + (size % 2)
    if fmt is None or frames is None:
        raise WavError("missing_chunks")
    if fmt != (1, 1, SAMPLE_RATE_HZ, 16):
        raise WavError("unsupported_wav_format")
    if frames == 0:
        raise WavError("empty_audio")
    if frames > SAMPLE_RATE_HZ * MAX_SECONDS:
        raise WavError("audio_too_long")
    return WavInfo(frames)


@dataclass(frozen=True)
class Transcript:
    text: str
    locale: str
    provider_id: str
    elapsed_seconds: float


class Transcriber(Protocol):
    @property
    def provider_id(self) -> str: ...

    def transcribe(self, wav: bytes, timeout_seconds: float) -> Transcript: ...


class TranscriptionFailed(Exception):
    def __init__(self, code: str) -> None:
        super().__init__(code)
        self.code = code


_NOISE = re.compile(r"\[(?:BLANK_AUDIO|MUSIC|NOISE|SILENCE)\]|\((?:[a-z ]+)\)", re.IGNORECASE)


def clean_transcript(raw: str) -> str:
    text = _NOISE.sub(" ", raw)
    text = re.sub(r"\s+", " ", text).strip()
    return text[:MAX_TRANSCRIPT_CHARS]


class WhisperCppTranscriber:
    """whisper.cpp CLI adapter. Paths are operator configuration, never derived from requests."""

    def __init__(self, binary: Path, model: Path, threads: int = 4, provider_id: str = "whisper_cpp") -> None:
        if not binary.is_file() or not os.access(binary, os.X_OK):
            raise TranscriptionFailed("asr_binary_missing")
        if not model.is_file():
            raise TranscriptionFailed("asr_model_missing")
        self._binary = binary
        self._model = model
        self._threads = max(1, min(threads, 8))
        self._provider_id = provider_id

    @property
    def provider_id(self) -> str:
        return self._provider_id

    def argv(self, audio_path: Path) -> list[str]:
        return [
            str(self._binary),
            "-m", str(self._model),
            "-f", str(audio_path),
            "-l", "en",
            "-t", str(self._threads),
            "-nt",  # no timestamps
            "-np",  # no progress/system prints on stdout
        ]  # fmt: skip

    def transcribe(self, wav: bytes, timeout_seconds: float) -> Transcript:
        validate_wav(wav)
        started = time.perf_counter()
        with tempfile.TemporaryDirectory(prefix="tryscape-asr-") as private_dir:
            os.chmod(private_dir, 0o700)
            audio_path = Path(private_dir) / "input.wav"  # application-owned name; never user-supplied
            audio_path.write_bytes(wav)
            try:
                completed = subprocess.run(  # noqa: S603 - fixed argv, no shell
                    self.argv(audio_path),
                    capture_output=True,
                    timeout=timeout_seconds,
                    check=False,
                )
            except subprocess.TimeoutExpired as exc:
                raise TranscriptionFailed("asr_timeout") from exc
        if completed.returncode != 0:
            raise TranscriptionFailed("asr_failed")
        text = clean_transcript(completed.stdout.decode("utf-8", "replace"))
        return Transcript(text, "en", self._provider_id, time.perf_counter() - started)
