# Operations: Tryscape creation service (R0)

> **Decision D12 (6 November 2026):** R0 ships without a server. Voice runs on the headset (whisper.cpp plus the C# port of `deterministic_rules`), and the release APK has no endpoint. Everything below describes the **optional development service**. Its deployment checklist is kept for R1.

The service turns speech and typed descriptions into validated scene proposals. **It has only ever run on a developer Mac (127.0.0.1); there is no judge-accessible deployment.** Hosting, budget and a model-assisted interpreter await the owner decision B-provider-budget. Everything else in the app (hand creation, editing, rehearsal, comparison, local projects) works without it.

## Components

| Process | Command (from `services/api`) | Role |
| --- | --- | --- |
| API | `uv run uvicorn tryscape_api.main:app --host 127.0.0.1 --port 8000` | Health, anonymous sessions, transcription and scene-proposal job creation, polling and cancellation. Holds no work in-process. |
| Worker | `uv run python -m tryscape_api.worker` | Claims jobs (`FOR UPDATE SKIP LOCKED`, 10 s lease), runs whisper.cpp and the `deterministic_rules` interpreter, completes conditionally, purges expired jobs and sweeps audio every 60 s. Stops cleanly on SIGINT/SIGTERM. |
| PostgreSQL 18 | `createdb tryscape_dev`, then `uv run alembic upgrade head` (head = `0002`) | Sessions (hashed tokens), jobs, idempotency records, minimal consent records. |

The API and the worker must share the same database and the same `TRYSCAPE_AUDIO_DIR`. That's a single-host R0 constraint (S13.3).

## Configuration (environment, prefix `TRYSCAPE_`)

| Variable | Default | Notes |
| --- | --- | --- |
| `ENV` | (required) | `development`, `test`, `judge` or `production`. OpenAPI and docs are only enabled in development and test. |
| `DATABASE_URL` | (required) | `postgresql+psycopg://…` only; startup fails closed otherwise. Secret: never commit it. |
| `CONSENT_VERSIONS` | `r0-dev-2026-11` | Comma-separated notice versions the headset may affirm. Must include the client's `CreationServiceConfig.ConsentVersion`. |
| `JOB_DEADLINE_SECONDS` | 30 | Per-job deadline (S13.2). |
| `JOB_RESULT_TTL_SECONDS` | 86400 | Jobs, results and transcripts are deleted after this. |
| `JOBS_PER_MINUTE_PER_SESSION` | 4 | One spoken attempt uses two jobs (transcription and proposal). |
| `MAX_ACTIVE_JOBS` | 50 | Global queued and running ceiling; overflow gets 503 with Retry-After (no unbounded queue). |
| `SESSIONS_PER_IP_PER_HOUR` | 20 | In-process window per API process; it resets on restart. Multi-instance hosting needs edge limits. |
| `TRUST_FORWARDED_FOR` | false | Enable only behind a trusted reverse proxy that sets `X-Forwarded-For`. |
| `CREATION_ENABLED` | true | **Kill switch.** When false, new creation jobs get 503 `provider_unavailable`; polling, cancellation and local app features are unaffected. |
| `JSON_BODY_LIMIT_BYTES` | 16384 | JSON routes. The audio route is fixed at 512 KiB. |
| `SESSION_TTL_SECONDS` | 3600 | |
| `AUDIO_DIR` | `<tmp>/tryscape-audio-<uid>` (0700) | Temporary job audio; random names; deleted at terminal states and swept after `AUDIO_MAX_AGE_SECONDS` (at most 900). |
| `ASR_BINARY`, `ASR_MODEL` | empty | whisper.cpp `whisper-cli` and ggml model paths. If unset, transcription jobs fail with `provider_unavailable` (honest). |
| `ASR_PROVIDER_ID`, `ASR_THREADS`, `ASR_TIMEOUT_SECONDS` | `whisper_cpp_base_en`, 4, 20 | |
| `WORKER_LEASE_SECONDS`, `WORKER_POLL_SECONDS` | 10, 0.25 | |

## Health and logs

- `GET /health/live` reports the process only. `GET /health/ready` returns 503 if the database is unavailable.
- Access logs record method, path, status, duration and request ID, never bodies. Worker logs record job IDs and safe codes only, never transcripts or audio (this was checked in the Phase 3b local run).

## Data retention

| Data | Retention |
| --- | --- |
| Audio | Deleted when its job ends, is cancelled or expires; anything older than 15 minutes is swept. |
| Transcripts | Kept in job input until the terminal state, and in results until the job TTL (24 h), then deleted. |
| Consent records | Session, purpose, version and time only; deleted with the session row (cascade). |

## Client configuration

- Development builds and the Editor use `http://127.0.0.1:8000`. On a Quest, run `adb reverse tcp:8000 tcp:8000`, so no LAN address is compiled in. `TRYSCAPE_API_URL` overrides this in the Editor (HTTPS or loopback only).
- Release builds have no endpoint, and plain HTTP is allowed only in development builds. Adding a deployment means setting an allowlisted HTTPS origin in `Unity/Creation/BackendSceneProposalProvider.cs` (`CreationServiceConfig.ForThisBuild`); no pipeline change is needed.

## Deployment checklist (not done; blocked)

1. Owner approves the host, budget and model provider (B-provider-budget).
2. Provision PostgreSQL 18, the API, the worker, a shared private audio volume, and whisper.cpp with its model (or the approved ASR).
3. Put HTTPS termination with body limits and trusted proxy headers in front, and set `TRUST_FORWARDED_FOR=true`.
4. Set the `TRYSCAPE_*` secrets in the host's secret store, and run `alembic upgrade head`.
5. Add the HTTPS origin to the release client configuration, rebuild, and run the AC23/AC29 benchmark from a headset.
6. Freeze the service, model and client versions for judging (AC28).
