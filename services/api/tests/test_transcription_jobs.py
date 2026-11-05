"""Transcription jobs (technical-spec S13.2/S13.3) against real PostgreSQL: raw WAV upload, validation before storage,
consent, idempotency, ownership, worker execution, cancellation, deadlines and audio deletion.

The ASR here is a stand-in `Transcriber`, so these tests cover transport and lifecycle, not recognition quality.
Real whisper.cpp runs are recorded separately (scripts/asr_spike.py and the Unity live-backend run)."""

from __future__ import annotations

import io
import json
import os
import time
import uuid
import wave
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any

import pytest
from httpx import AsyncClient
from jsonschema import Draft202012Validator
from referencing import Registry, Resource
from sqlalchemy import select, text, update
from sqlalchemy.ext.asyncio import AsyncEngine, async_sessionmaker

from tryscape_api.audio_store import AudioStore
from tryscape_api.config import Environment, Settings
from tryscape_api.creation.transcription import Transcript, TranscriptionFailed
from tryscape_api.db import Consent, Job
from tryscape_api.worker import Worker

CONTRACTS = Path(__file__).resolve().parents[3] / "contracts" / "v1"
SCHEMAS = {p.name: json.loads(p.read_text()) for p in (CONTRACTS / "schemas").glob("*.schema.json")}
REGISTRY = Registry().with_resources((s["$id"], Resource.from_contents(s)) for s in SCHEMAS.values())
API = SCHEMAS["api.schema.json"]["$id"]
CONSENT = "r0-dev-2026-11"
PATH = "/v1/transcription-jobs"


def valid(defn: str, doc: Any) -> bool:
    return Draft202012Validator({"$ref": f"{API}#/$defs/{defn}"}, registry=REGISTRY).is_valid(doc)


@pytest.fixture
def settings(migrated_database: str, tmp_path: Path) -> Settings:
    return Settings(env=Environment.TEST, database_url=migrated_database, audio_dir=str(tmp_path / "audio"))  # type: ignore[arg-type]


def wav(seconds: float = 1.0, rate: int = 16000, tone: bool = True) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        frames = int(seconds * rate)
        w.writeframes((b"\x10\x27\xf0\xd8" * (frames // 2 + 1))[: frames * 2] if tone else b"\x00" * frames * 2)
    return buffer.getvalue()


class FakeTranscriber:
    provider_id = "fake_asr"

    def __init__(self, text: str = "six by five metre room for a sofa", fail: bool = False) -> None:
        self.text = text
        self.fail = fail
        self.seen: list[int] = []

    def transcribe(self, data: bytes, timeout_seconds: float) -> Transcript:
        self.seen.append(len(data))
        if self.fail:
            raise TranscriptionFailed("asr_failed")
        return Transcript(self.text, "en", self.provider_id, 0.01)


async def session(client: AsyncClient) -> str:
    response = await client.post("/v1/anonymous-sessions", json={})
    token: str = response.json()["session_token"]
    return token


def headers(token: str, key: str | None = None, **extra: str) -> dict[str, str]:
    return {
        "Authorization": f"Bearer {token}",
        "Idempotency-Key": key or str(uuid.uuid4()),
        "X-Processing-Consent-Version": CONSENT,
        "Content-Type": "audio/wav",
        "Content-Language": "en",
        **extra,
    }


async def upload(client: AsyncClient, token: str, data: bytes | None = None, key: str | None = None) -> str:
    response = await client.post(PATH, content=data if data is not None else wav(), headers=headers(token, key))
    assert response.status_code == 202, response.text
    assert valid("job_receipt", response.json())
    job_id: str = response.json()["job_id"]
    return job_id


async def poll(client: AsyncClient, token: str, job_id: str) -> dict[str, Any]:
    response = await client.get(f"/v1/jobs/{job_id}", headers={"Authorization": f"Bearer {token}"})
    assert response.status_code == 200, response.text
    view: dict[str, Any] = response.json()
    assert valid("job", view), view
    return view


def worker(engine: AsyncEngine, settings: Settings, asr: Any = None, **kwargs: Any) -> Worker:
    return Worker(async_sessionmaker(engine, expire_on_commit=False), settings, transcriber=asr, **kwargs)


def stored(settings: Settings) -> list[str]:
    return sorted(p.name for p in AudioStore(settings.audio_dir).root.iterdir())


async def test_round_trip_returns_contract_transcript_and_deletes_audio(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await session(client)
    job_id = await upload(client, token)
    assert len(stored(settings)) == 1
    async with engine.connect() as conn:
        ref = (await conn.execute(select(Job.input_ref).where(Job.id == uuid.UUID(job_id)))).scalar_one()
    assert ref is not None
    assert ref.startswith("aud_")  # opaque, random, application-owned name
    asr = FakeTranscriber()
    assert await worker(engine, settings, asr).run_once() == "succeeded"
    view = await poll(client, token, job_id)
    assert view["kind"] == "transcription"
    assert view["result"] == {"text": "six by five metre room for a sofa", "locale": "en", "provider_id": "fake_asr"}
    assert asr.seen == [len(wav())]
    assert stored(settings) == []
    async with engine.connect() as conn:
        row = (
            await conn.execute(
                text("SELECT input_ref IS NULL, input_payload IS NULL FROM jobs WHERE id = :id"),
                {"id": uuid.UUID(job_id)},
            )
        ).one()
        purposes = (await conn.execute(select(Consent.purpose))).scalars().all()
    assert tuple(row) == (True, True)
    assert purposes == ["remote_audio_processing"]


@pytest.mark.parametrize(
    ("data", "status", "code"),
    [
        (wav(rate=44100), 422, "unsupported_wav_format"),
        (wav(seconds=15.5), 422, "audio_too_long"),
        (wav(seconds=0), 422, "empty_audio"),
        (b"RIFF0000WAVEnot really audio at all" + b"\x00" * 20, 422, "truncated_chunk"),
    ],
)
async def test_invalid_audio_is_rejected_before_storage(
    client: AsyncClient, engine: AsyncEngine, settings: Settings, data: bytes, status: int, code: str
) -> None:
    token = await session(client)
    response = await client.post(PATH, content=data, headers=headers(token))
    assert response.status_code == status
    assert response.json()["error"]["details"] == [{"field": "body", "code": code}]
    assert stored(settings) == []
    async with engine.connect() as conn:
        assert (await conn.execute(text("SELECT count(*) FROM jobs"))).scalar_one() == 0


async def test_transport_limits_media_locale_and_consent(client: AsyncClient, settings: Settings) -> None:
    token = await session(client)
    oversized = await client.post(PATH, content=b"RIFF" + b"\x00" * (512 * 1024 + 1), headers=headers(token))
    assert oversized.status_code == 413
    json_body = await client.post(PATH, content=b"{}", headers=headers(token, **{"Content-Type": "application/json"}))
    assert json_body.status_code == 415
    mp3 = await client.post(PATH, content=b"ID3", headers=headers(token, **{"Content-Type": "audio/mpeg"}))
    assert mp3.status_code == 415
    french = await client.post(PATH, content=wav(), headers=headers(token, **{"Content-Language": "fr"}))
    assert french.status_code == 422
    no_consent = headers(token)
    del no_consent["X-Processing-Consent-Version"]
    assert (await client.post(PATH, content=wav(), headers=no_consent)).status_code == 422
    no_auth = headers(token)
    del no_auth["Authorization"]
    assert (await client.post(PATH, content=wav(), headers=no_auth)).status_code == 401
    assert stored(settings) == []


async def test_idempotent_upload_and_conflict(client: AsyncClient, settings: Settings) -> None:
    token = await session(client)
    key = str(uuid.uuid4())
    first = await upload(client, token, key=key)
    assert await upload(client, token, key=key) == first
    assert len(stored(settings)) == 1, "a replayed request must not store the audio again"
    conflict = await client.post(PATH, content=wav(seconds=2), headers=headers(token, key))
    assert conflict.status_code == 409


async def test_one_active_transcription_per_session_and_ownership(client: AsyncClient) -> None:
    owner = await session(client)
    other = await session(client)
    job_id = await upload(client, owner)
    busy = await client.post(PATH, content=wav(), headers=headers(owner))
    assert busy.status_code == 429
    for method in ("GET", "DELETE"):
        response = await client.request(method, f"/v1/jobs/{job_id}", headers={"Authorization": f"Bearer {other}"})
        assert response.status_code == 404


async def test_cancel_queued_deletes_audio_and_never_runs(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await session(client)
    job_id = await upload(client, token)
    response = await client.delete(f"/v1/jobs/{job_id}", headers={"Authorization": f"Bearer {token}"})
    assert response.status_code == 202
    assert stored(settings) == []
    asr = FakeTranscriber()
    assert await worker(engine, settings, asr).run_once() is None
    assert asr.seen == []
    assert (await poll(client, token, job_id))["status"] == "cancelled"


async def test_cancel_while_transcribing_discards_transcript_and_deletes_audio(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await session(client)
    job_id = await upload(client, token)
    w = worker(engine, settings, FakeTranscriber())
    claimed = await w.claim()
    assert claimed is not None
    await client.delete(f"/v1/jobs/{job_id}", headers={"Authorization": f"Bearer {token}"})
    outcome = w.execute(claimed)
    assert outcome[0] == "succeeded"
    assert await w.complete(claimed, *outcome) == "cancelled"
    view = await poll(client, token, job_id)
    assert view["status"] == "cancelled"
    assert view["result"] is None


async def test_run_once_deletes_audio_even_when_cancelled_mid_run(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await session(client)
    job_id = await upload(client, token)

    class CancellingAsr(FakeTranscriber):
        def transcribe(self, data: bytes, timeout_seconds: float) -> Transcript:
            # Simulate the user cancelling while ASR runs.
            import psycopg

            with psycopg.connect(settings.database_url.get_secret_value().replace("+psycopg", "")) as conn:
                conn.execute("UPDATE jobs SET status = 'cancel_requested' WHERE id = %s", (uuid.UUID(job_id),))
            return super().transcribe(data, timeout_seconds)

    assert await worker(engine, settings, CancellingAsr()).run_once() == "cancelled"
    assert stored(settings) == []


async def test_failures_use_safe_codes_and_delete_audio(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await session(client)
    job_id = await upload(client, token)
    assert await worker(engine, settings, FakeTranscriber(fail=True)).run_once() == "failed"
    view = await poll(client, token, job_id)
    assert view["error"] == {"code": "provider_unavailable", "retryable": True}
    assert stored(settings) == []

    second = await upload(client, token)
    no_asr = Worker(async_sessionmaker(engine, expire_on_commit=False), settings)  # nothing configured
    assert not no_asr.has_transcriber
    assert await no_asr.run_once() == "failed"
    assert (await poll(client, token, second))["error"]["code"] == "provider_unavailable"
    assert stored(settings) == []


async def test_silence_is_a_successful_empty_transcript(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await session(client)
    job_id = await upload(client, token, wav(tone=False))
    await worker(engine, settings, FakeTranscriber(text="")).run_once()
    assert (await poll(client, token, job_id))["result"]["text"] == ""


async def test_expiry_on_poll_and_sweep_delete_audio(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await session(client)
    job_id = await upload(client, token)
    async with engine.begin() as conn:
        await conn.execute(
            update(Job).where(Job.id == uuid.UUID(job_id)).values(deadline_at=datetime.now(UTC) - timedelta(seconds=1))
        )
    assert (await poll(client, token, job_id))["status"] == "expired"
    assert stored(settings) == []

    store = AudioStore(settings.audio_dir)
    old = store.put(wav())
    fresh = store.put(wav())
    past = time.time() - 16 * 60
    os.utime(store.path(old), (past, past))
    assert store.sweep(settings.audio_max_age_seconds) == 1
    assert stored(settings) == [fresh]
    with pytest.raises(ValueError, match="invalid audio reference"):
        store.path("../../etc/passwd")
