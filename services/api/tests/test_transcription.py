"""Audio contract (S13.3) and whisper.cpp adapter plumbing. Real speech recognition quality is measured separately
by scripts/asr_spike.py with the real model; these tests use a stand-in executable and never claim ASR accuracy."""

from __future__ import annotations

import io
import os
import stat
import struct
import wave
from pathlib import Path

import pytest

from tryscape_api.creation.transcription import (
    MAX_BYTES,
    TranscriptionFailed,
    WavError,
    WhisperCppTranscriber,
    clean_transcript,
    validate_wav,
)


def wav(seconds: float = 1.0, rate: int = 16000, channels: int = 1, width: int = 2) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(width)
        w.setframerate(rate)
        w.writeframes(b"\x00" * int(seconds * rate) * channels * width)
    return buffer.getvalue()


def test_valid_mono_16k_pcm_is_accepted() -> None:
    assert validate_wav(wav(2.5)).seconds == pytest.approx(2.5)


@pytest.mark.parametrize(
    ("data", "code"),
    [
        (wav(rate=44100), "unsupported_wav_format"),
        (wav(channels=2), "unsupported_wav_format"),
        (wav(width=1), "unsupported_wav_format"),
        (wav(seconds=0), "empty_audio"),
        (b"ID3" + b"\x00" * 100, "not_wav"),
        (wav()[:60], "truncated_chunk"),
    ],
)
def test_contract_violations_are_rejected_from_the_bytes(data: bytes, code: str) -> None:
    with pytest.raises(WavError) as info:
        validate_wav(data)
    assert info.value.code == code


def test_duration_and_size_limits() -> None:
    with pytest.raises(WavError, match="audio_too_long"):
        # 15.5 s fits under 512 KiB? 15.5 s * 32000 B/s = 496 kB, so the frame count check is what rejects it.
        validate_wav(wav(15.5))
    with pytest.raises(WavError, match="audio_too_large"):
        validate_wav(b"RIFF" + b"\x00" * MAX_BYTES)
    lying = bytearray(wav(1.0))
    struct.pack_into("<I", lying, 40, 10_000_000)  # data chunk claims far more bytes than present
    with pytest.raises(WavError, match="truncated_chunk"):
        validate_wav(bytes(lying))


def test_noise_markers_are_removed_and_output_is_bounded() -> None:
    assert clean_transcript(" [BLANK_AUDIO]\n Six by five metres. (wind blowing) ") == "Six by five metres."
    assert len(clean_transcript("a" * 5000)) == 2000


def fake_whisper(tmp_path: Path, body: str) -> Path:
    script = tmp_path / "whisper-cli"
    script.write_text("#!/bin/sh\n" + body + "\n")
    script.chmod(script.stat().st_mode | stat.S_IXUSR)
    return script


def test_adapter_uses_fixed_argv_and_deletes_the_temporary_audio(tmp_path: Path) -> None:
    seen = tmp_path / "seen.txt"
    binary = fake_whisper(tmp_path, f'printf "%s\\n" "$@" > "{seen}"; echo " Six by five metre room."')
    model = tmp_path / "model.bin"
    model.write_bytes(b"x")
    result = WhisperCppTranscriber(binary, model).transcribe(wav(), timeout_seconds=5)
    assert result.text == "Six by five metre room."
    args = seen.read_text().splitlines()
    assert args[:2] == ["-m", str(model)]
    audio_path = Path(args[3])
    assert audio_path.name == "input.wav"
    assert not audio_path.exists()
    assert not audio_path.parent.exists()


def test_adapter_failures_are_safe_codes(tmp_path: Path) -> None:
    model = tmp_path / "model.bin"
    model.write_bytes(b"x")
    failing = fake_whisper(tmp_path, "echo 'internal path /secret' >&2; exit 3")
    with pytest.raises(TranscriptionFailed, match="asr_failed"):
        WhisperCppTranscriber(failing, model).transcribe(wav(), timeout_seconds=5)
    slow_dir = tmp_path / "slow"
    slow_dir.mkdir()
    slow = fake_whisper(slow_dir, "sleep 5")
    with pytest.raises(TranscriptionFailed, match="asr_timeout"):
        WhisperCppTranscriber(slow, model).transcribe(wav(), timeout_seconds=0.3)
    with pytest.raises(WavError):
        WhisperCppTranscriber(failing, model).transcribe(b"not audio", timeout_seconds=5)
    with pytest.raises(TranscriptionFailed, match="asr_model_missing"):
        WhisperCppTranscriber(failing, tmp_path / "absent.bin")
    assert os.access(failing, os.X_OK)
