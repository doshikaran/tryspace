# Tryscape

**Understand the situation. Rehearse your options. Act prepared.**

Tryscape turns a described situation into an interactive spatial rehearsal. Voice creates the initial environment through a Python AI backend; after that, hands do the editing, rehearsal, replay and comparison. The reference scenario, Moving Day, is **provisional**.

> **Status: Phase 0 / M0, blocked.** No Unity app exists yet. What exists today is shared contracts and fixtures, check scripts, and a backend baseline (health + anonymous sessions). See [docs/progress.md](docs/progress.md).

## Layout

| Path | Content | State |
| --- | --- | --- |
| `docs/` | Specs ([technical](docs/technical-spec.md), [demo scope](docs/demo-scope.md)), progress, environment records | Present |
| `contracts/v1/` | JSON Schemas and shared fixtures | Present |
| `scripts/` | `check_contracts.py`, `check_release.py` | Present |
| `services/api/` | R0 creation backend (FastAPI/PostgreSQL) | Baseline only |
| `app-unity/` | Unity 6000.3 LTS client | **Not created**: needs Unity installed |
| `artifacts/` | Local logs and builds (git-ignored) | Generated |

## Verified commands (macOS, Apple Silicon)

Prerequisites: Git, [uv](https://docs.astral.sh/uv/), Python 3.12 (uv can provide it), PostgreSQL 18 reachable locally.

```bash
uv run --locked scripts/check_contracts.py      # contracts + fixtures + reference expectations
python3 scripts/check_release.py --phase 0      # gate report; exits 1 while gates are open

cd services/api                                  # backend; see services/api/README.md
uv sync --frozen && uv run ruff check . && uv run ruff format --check . && uv run mypy src && uv run pytest -q
```

Unity, Android build and simulator commands (technical-spec S19.1) are **not available yet**.
