"""Scene-proposal job API and worker against real PostgreSQL (technical-spec S13.1-S13.4)."""

from __future__ import annotations

import asyncio
import json
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any

from httpx import AsyncClient
from jsonschema import Draft202012Validator
from referencing import Registry, Resource
from sqlalchemy import select, text, update
from sqlalchemy.ext.asyncio import AsyncEngine, async_sessionmaker

from tryscape_api.config import Settings
from tryscape_api.creation.intent import Extraction
from tryscape_api.db import Consent, Job
from tryscape_api.worker import Worker

CONTRACTS = Path(__file__).resolve().parents[3] / "contracts" / "v1"
SCHEMAS = {p.name: json.loads(p.read_text()) for p in (CONTRACTS / "schemas").glob("*.schema.json")}
REGISTRY = Registry().with_resources((s["$id"], Resource.from_contents(s)) for s in SCHEMAS.values())
API = SCHEMAS["api.schema.json"]["$id"]
CONSENT = "r0-dev-2026-11"
REFERENCE = "Prepare a six by five metre living room for a sofa delivery. There is a console table in the way."


def valid(defn: str, doc: Any) -> bool:
    return Draft202012Validator({"$ref": f"{API}#/$defs/{defn}"}, registry=REGISTRY).is_valid(doc)


def body(transcript: str = REFERENCE, **overrides: Any) -> dict[str, Any]:
    return {
        "schema_version": "1.0.0",
        "transcript": transcript,
        "locale": "en",
        "scenario_pack_id": "moving_day",
        "catalog_version": "1.0.0",
        **overrides,
    }


async def new_session(client: AsyncClient) -> str:
    response = await client.post("/v1/anonymous-sessions", json={})
    assert response.status_code == 201
    token: str = response.json()["session_token"]
    return token


def headers(token: str, key: str | None = None, consent: str | None = CONSENT) -> dict[str, str]:
    h = {"Authorization": f"Bearer {token}", "Idempotency-Key": key or str(uuid.uuid4())}
    if consent is not None:
        h["X-Processing-Consent-Version"] = consent
    return h


def worker(engine: AsyncEngine, settings: Settings, **kwargs: Any) -> Worker:
    return Worker(async_sessionmaker(engine, expire_on_commit=False), settings, **kwargs)


async def create(client: AsyncClient, token: str, transcript: str = REFERENCE, key: str | None = None) -> str:
    response = await client.post("/v1/scene-proposal-jobs", json=body(transcript), headers=headers(token, key))
    assert response.status_code == 202, response.text
    receipt = response.json()
    assert valid("job_receipt", receipt)
    assert response.headers["cache-control"] == "no-store"
    job_id: str = receipt["job_id"]
    return job_id


async def poll(client: AsyncClient, token: str, job_id: str) -> dict[str, Any]:
    response = await client.get(f"/v1/jobs/{job_id}", headers={"Authorization": f"Bearer {token}"})
    assert response.status_code == 200, response.text
    view: dict[str, Any] = response.json()
    assert valid("job", view), view
    return view


async def test_full_round_trip_produces_validated_ready_proposal(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await new_session(client)
    job_id = await create(client, token)
    assert (await poll(client, token, job_id))["status"] == "queued"
    assert await worker(engine, settings).run_once() == "succeeded"
    view = await poll(client, token, job_id)
    assert view["status"] == "succeeded"
    assert view["kind"] == "scene_proposal"
    proposal = view["result"]
    assert proposal["status"] == "ready"
    assert proposal["intent"]["room_width_mm"] == 6000
    assert proposal["provider_metadata"] == {"provider_id": "deterministic_rules", "version": "1.0.0"}
    async with engine.connect() as conn:
        purged = (
            await conn.execute(text("SELECT input_payload IS NULL FROM jobs WHERE id = :id"), {"id": uuid.UUID(job_id)})
        ).scalar_one()
    assert purged is True  # SQL NULL, not JSON null: the transcript input is gone at the terminal state


async def test_clarification_and_unsupported_are_successful_jobs(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await new_session(client)
    w = worker(engine, settings)
    a = await create(client, token, "Prepare my room for a delivery")
    await w.run_once()
    b = await create(client, token, "Scan my floor plan PDF")
    await w.run_once()
    assert (await poll(client, token, a))["result"]["status"] == "needs_clarification"
    assert (await poll(client, token, b))["result"]["status"] == "unsupported"


async def test_authentication_consent_and_media_are_enforced(client: AsyncClient) -> None:
    token = await new_session(client)
    no_auth = await client.post("/v1/scene-proposal-jobs", json=body(), headers={"Idempotency-Key": str(uuid.uuid4())})
    assert no_auth.status_code == 401
    bad_token = await client.post("/v1/scene-proposal-jobs", json=body(), headers=headers("not-a-real-token"))
    assert bad_token.status_code == 401
    no_consent = await client.post("/v1/scene-proposal-jobs", json=body(), headers=headers(token, consent=None))
    assert no_consent.status_code == 422
    assert no_consent.json()["error"]["details"] == [
        {"field": "x-processing-consent-version", "code": "consent_required"}
    ]
    old_consent = await client.post("/v1/scene-proposal-jobs", json=body(), headers=headers(token, consent="v0"))
    assert old_consent.status_code == 422
    no_key = await client.post(
        "/v1/scene-proposal-jobs",
        json=body(),
        headers={"Authorization": f"Bearer {token}", "X-Processing-Consent-Version": CONSENT},
    )
    assert no_key.status_code == 400
    text_body = await client.post(
        "/v1/scene-proposal-jobs", content=b"hello", headers={**headers(token), "Content-Type": "text/plain"}
    )
    assert text_body.status_code == 415


async def test_request_validation_happens_before_enqueue(client: AsyncClient, engine: AsyncEngine) -> None:
    token = await new_session(client)
    cases = [
        body(transcript=""),
        body(transcript="x" * 2001),
        body(locale="fr"),
        body(catalog_version="9.9.9"),
        {**body(), "prefab_path": "Assets/x.prefab"},
    ]
    for case in cases:
        response = await client.post("/v1/scene-proposal-jobs", json=case, headers=headers(token))
        assert response.status_code == 422, case
        assert valid("error_envelope", response.json())
    duplicate = (
        b'{"schema_version":"1.0.0","schema_version":"1.0.0","transcript":"a","locale":"en",'
        b'"scenario_pack_id":"moving_day","catalog_version":"1.0.0"}'
    )
    response = await client.post(
        "/v1/scene-proposal-jobs", content=duplicate, headers={**headers(token), "Content-Type": "application/json"}
    )
    assert response.status_code == 400
    huge = await client.post(
        "/v1/scene-proposal-jobs",
        content=b'{"transcript":"' + b"x" * 20000 + b'"}',
        headers={**headers(token), "Content-Type": "application/json"},
    )
    assert huge.status_code == 413
    async with engine.connect() as conn:
        assert (await conn.execute(text("SELECT count(*) FROM jobs"))).scalar_one() == 0


async def test_idempotency_same_key_same_body_reuses_job_and_different_body_conflicts(client: AsyncClient) -> None:
    token = await new_session(client)
    key = str(uuid.uuid4())
    first = await create(client, token, key=key)
    again = await create(client, token, key=key)
    assert again == first
    conflict = await client.post(
        "/v1/scene-proposal-jobs", json=body("Seven by six metres sofa room"), headers=headers(token, key)
    )
    assert conflict.status_code == 409
    assert conflict.json()["error"]["code"] == "idempotency_conflict"


async def test_jobs_are_scoped_to_their_session(client: AsyncClient) -> None:
    owner = await new_session(client)
    other = await new_session(client)
    job_id = await create(client, owner)
    for method in ("GET", "DELETE"):
        response = await client.request(method, f"/v1/jobs/{job_id}", headers={"Authorization": f"Bearer {other}"})
        assert response.status_code == 404
    missing = await client.get(f"/v1/jobs/{uuid.uuid4()}", headers={"Authorization": f"Bearer {owner}"})
    assert missing.status_code == 404
    garbage = await client.get("/v1/jobs/not-a-uuid", headers={"Authorization": f"Bearer {owner}"})
    assert garbage.status_code == 404


async def test_one_active_generation_and_per_minute_limit(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await new_session(client)
    await create(client, token)
    busy = await client.post("/v1/scene-proposal-jobs", json=body(), headers=headers(token))
    assert busy.status_code == 429
    assert busy.headers["retry-after"] == "2"
    w = worker(engine, settings)
    await w.run_once()
    for _ in range(settings.jobs_per_minute_per_session - 1):
        await create(client, token)
        await w.run_once()
    limited = await client.post("/v1/scene-proposal-jobs", json=body(), headers=headers(token))
    assert limited.status_code == 429
    assert limited.headers["retry-after"] == "60"


async def test_consent_record_is_minimal_and_written_once(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await new_session(client)
    w = worker(engine, settings)
    await create(client, token)
    await w.run_once()
    await create(client, token)
    async with engine.connect() as conn:
        rows = (await conn.execute(select(Consent.purpose, Consent.policy_version))).all()
    assert [tuple(r) for r in rows] == [("remote_transcript_processing", CONSENT)]


async def test_cancel_queued_job_is_terminal_and_never_executed(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await new_session(client)
    job_id = await create(client, token)
    auth = {"Authorization": f"Bearer {token}"}
    assert (await client.delete(f"/v1/jobs/{job_id}", headers=auth)).status_code == 202
    assert (await client.delete(f"/v1/jobs/{job_id}", headers=auth)).status_code == 204  # repeat is safe
    assert await worker(engine, settings).run_once() is None
    view = await poll(client, token, job_id)
    assert view["status"] == "cancelled"
    assert view["result"] is None
    async with engine.connect() as conn:
        row = (
            await conn.execute(
                text("SELECT input_payload IS NULL, result IS NULL FROM jobs WHERE id = :id"), {"id": uuid.UUID(job_id)}
            )
        ).one()
    assert tuple(row) == (True, True)


async def test_cancel_while_running_discards_late_result(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await new_session(client)
    job_id = await create(client, token)
    w = worker(engine, settings)
    claimed = await w.claim()
    assert claimed is not None
    assert (await poll(client, token, job_id))["status"] == "running"
    response = await client.delete(f"/v1/jobs/{job_id}", headers={"Authorization": f"Bearer {token}"})
    assert response.status_code == 202
    outcome = w.execute(claimed)
    assert outcome[0] == "succeeded"  # the work itself finished...
    assert await w.complete(claimed, *outcome) == "cancelled"  # ...but its output is discarded
    view = await poll(client, token, job_id)
    assert view["status"] == "cancelled"
    assert view["result"] is None


async def test_deadline_expiry_discards_late_result_and_reports_on_poll(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await new_session(client)
    job_id = await create(client, token)
    w = worker(engine, settings)
    claimed = await w.claim()
    assert claimed is not None
    outcome = w.execute(claimed)
    async with engine.begin() as conn:
        await conn.execute(
            update(Job).where(Job.id == claimed.job_id).values(deadline_at=datetime.now(UTC) - timedelta(seconds=1))
        )
    assert await w.complete(claimed, *outcome) == "expired"
    view = await poll(client, token, job_id)
    assert view["status"] == "expired"
    assert view["error"] == {"code": "deadline_exceeded", "retryable": True}

    # A queued job past its deadline expires on poll without ever running.
    second = await create(client, token)
    async with engine.begin() as conn:
        await conn.execute(
            update(Job).where(Job.id == uuid.UUID(second)).values(deadline_at=datetime.now(UTC) - timedelta(seconds=1))
        )
    assert (await poll(client, token, second))["status"] == "expired"


async def test_lost_lease_is_reclaimed_and_stale_owner_cannot_complete(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await new_session(client)
    job_id = await create(client, token)
    first = worker(engine, settings, worker_id="worker-a")
    claimed_a = await first.claim()
    assert claimed_a is not None
    async with engine.begin() as conn:  # simulate worker A stalling past its lease
        await conn.execute(
            update(Job)
            .where(Job.id == claimed_a.job_id)
            .values(lease_expires_at=datetime.now(UTC) - timedelta(seconds=1))
        )
    second = worker(engine, settings, worker_id="worker-b")
    assert await second.run_once() == "succeeded"
    assert await first.complete(claimed_a, *first.execute(claimed_a)) == "discarded"
    async with engine.connect() as conn:
        attempts = (await conn.execute(select(Job.attempts).where(Job.id == uuid.UUID(job_id)))).scalar_one()
    assert attempts == 2
    assert (await poll(client, token, job_id))["status"] == "succeeded"


async def test_concurrent_workers_never_claim_the_same_job(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    tokens = [await new_session(client) for _ in range(3)]
    for token in tokens:
        await create(client, token)
    workers = [worker(engine, settings, worker_id=f"w{i}") for i in range(3)]
    claims = await asyncio.gather(*(w.claim() for w in workers))
    ids = [c.job_id for c in claims if c is not None]
    assert len(ids) == 3
    assert len(set(ids)) == 3


class ExplodingExtractor:
    provider_id = "exploding"
    version = "1.0.0"

    def extract(self, transcript: str) -> Extraction:
        raise RuntimeError("provider says: secret-internal-host:5432 timed out")


async def test_provider_failure_is_reported_with_a_safe_code(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await new_session(client)
    job_id = await create(client, token)
    assert await worker(engine, settings, extractor=ExplodingExtractor()).run_once() == "failed"
    view = await poll(client, token, job_id)
    assert view["error"] == {"code": "provider_unavailable", "retryable": True}
    assert "secret" not in json.dumps(view)


async def test_expired_jobs_and_transcripts_are_purged(
    client: AsyncClient, engine: AsyncEngine, settings: Settings
) -> None:
    token = await new_session(client)
    job_id = await create(client, token)
    w = worker(engine, settings)
    await w.run_once()
    assert await w.purge_expired() == 0
    async with engine.begin() as conn:
        await conn.execute(
            update(Job).where(Job.id == uuid.UUID(job_id)).values(expires_at=datetime.now(UTC) - timedelta(seconds=1))
        )
    assert await w.purge_expired() == 1
    response = await client.get(f"/v1/jobs/{job_id}", headers={"Authorization": f"Bearer {token}"})
    assert response.status_code == 404
