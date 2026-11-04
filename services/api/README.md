# Tryscape creation API (`services/api`)

Required R0 Python backend (technical-spec S13). Python 3.12, FastAPI, Pydantic 2, SQLAlchemy 2 (async, psycopg 3), Alembic, PostgreSQL 18. Dependencies are locked in `uv.lock`.

## Implemented (M0 baseline)

| Route | Behavior |
| --- | --- |
| `GET /health/live` | 200; no database or provider access. |
| `GET /health/ready` | 200 when PostgreSQL answers, otherwise 503 without leaking connection details. |
| `POST /v1/anonymous-sessions` | Body must be exactly `{}` (strict JSON). 201 with a 256-bit URL-safe token returned once; only its SHA-256 is stored; 1 h expiry; `Cache-Control: no-store`. |

Cross-cutting: validated/echoed `X-Request-ID`, 16 KiB JSON body limit enforced before parsing, S13.3 error envelope, metadata-only access logs, OpenAPI/docs disabled outside development/test, startup fails closed on missing/invalid configuration.

Schema (`migrations/versions/0001_*`): `anonymous_sessions`, `jobs`, `idempotency_records`, `consents`. No R1 account/project tables.

## Not implemented yet

Transcription/proposal job routes, polling/cancellation, worker leases, idempotency handling, consent capture, session-issuance rate limits and deployment. Job routes are deliberately absent (404) rather than stubbed until a real ASR/intent provider is approved — see `docs/progress.md`.

## Commands (run from `services/api`)

```bash
uv sync --frozen
uv run ruff check .
uv run ruff format --check .
uv run mypy src
uv run pytest -q          # needs a reachable PostgreSQL; recreates database `tryscape_test`

export TRYSCAPE_ENV=development TRYSCAPE_DATABASE_URL=postgresql+psycopg://localhost/tryscape_dev
createdb tryscape_dev     # once
uv run alembic upgrade head
uv run uvicorn tryscape_api.main:app --host 127.0.0.1 --port 8000
```

`TRYSCAPE_TEST_DATABASE_URL` overrides the test server; the database name must end in `_test` because the suite drops and recreates it. Localhost is developer-only and not reachable from a Quest headset.
