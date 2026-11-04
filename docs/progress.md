# Tryscape progress

Last updated: 5 November 2026 · Current phase: **0 / M0 — blocked** · Authoritative gate state: [`docs/progress/phase-state.json`](progress/phase-state.json) (check with `python3 scripts/check_release.py --phase 0`).

Moving Day is still the **provisional** reference pack (demo-scope D01). Nothing below claims the app works on a headset, in the simulator, or with voice.

## Implemented and verified

| Item | Where | Evidence |
| --- | --- | --- |
| Local Git repository (no remote) with baseline commit of the original spec package | `9f35e64` | `git log` |
| Ignore/attributes/editorconfig for Unity, Python, secrets, models, recordings and artifacts | `.gitignore`, `.gitattributes`, `.editorconfig` | Staged-file review showed no venv/cache/secret files |
| v1 JSON Schemas: SceneSpec, catalog, command envelope/result, EvaluationResult, SceneProposal, API bodies (sessions, proposal request, job, error envelope) | `contracts/v1/schemas/` | `check_contracts.py` meta-validates every schema |
| Shared fixtures: 11 valid scenes (F01–F04, 7×6 m variant, measured, quarter turn, 4 boundary), 20 shape-invalid, 14 semantic-invalid, proposals, commands, API, results | `contracts/v1/fixtures/` | 141 checks pass, exit 0 |
| Expected F01–F04 + boundary outcomes, transcribed from demo-scope D06 / S08.2 with written derivations | `contracts/v1/fixtures/evaluation/expectations.json` | Test-only exact-rational reference evaluator matches all 11 cases; 5 deliberate mutations were each caught |
| Contract checker (strict JSON, schema, semantic codes, reference evaluator, entity-order invariance) | `scripts/check_contracts.py` (+ `.lock`) | `uv run --locked scripts/check_contracts.py` |
| Release/gate reporter (missing gate = failure; verifies evidence hashes) | `scripts/check_release.py` | Exits 1 on the current blocked state, as it should |
| Backend baseline: FastAPI app factory, fail-closed settings, `/health/live`, `/health/ready`, `POST /v1/anonymous-sessions` (hash-only storage, 1 h expiry, no-store), S13.3 error envelope, request IDs, 16 KiB pre-parse body limit, strict JSON | `services/api/src/tryscape_api/` | 78 pytest tests against real PostgreSQL 18; ruff, format, mypy strict clean |
| R0 schema migration: `anonymous_sessions`, `jobs`, `idempotency_records`, `consents` (no R1 tables) | `services/api/migrations/versions/0001_*` | Upgrade/downgrade/upgrade round trip and model-drift test |
| Pydantic contract mirrors with parity tests against the shared fixtures | `services/api/src/tryscape_api/contracts/models.py` | `tests/test_contract_parity.py` |
| Clean-clone repeat of contracts + backend checks | fresh clone of `4976e6a` | All exit 0 (same machine and user, so not the second-contributor gate) |

## In progress

Nothing is running in the background. The next unblocked work is listed under "Next".

## Blocked (needs a human action)

| Blocker | Blocks | Exact action |
| --- | --- | --- |
| **B-unity-install** | Unity project, OpenXR/Meta config, simulator hand sample, Android APK, toolchain pin | 1. Install Unity Hub for macOS (Apple Silicon). 2. Sign in and activate a licence (Personal if eligible). 3. Install the latest **Unity 6000.3 LTS** editor with **Android Build Support → OpenJDK + Android SDK & NDK Tools**. 4. Install the standalone **Meta XR Simulator for macOS** from Meta's developer site. 5. Tell the agent the editor path, e.g. `export TRYSCAPE_UNITY_EDITOR="/Applications/Unity/Hub/Editor/6000.3.<patch>/Unity.app/Contents/MacOS/Unity"`. |
| **B-provider-budget** | Real ASR/intent-model access, judge-reachable HTTPS deployment (AC02, AC23, AC29) | Owner decides: (a) ASR and intent-model provider(s) or self-hosted models, (b) a hosting account, (c) a spending cap. Approved spending is currently **zero** and no free quota is assumed. See the recommendation in `docs/progress/phase-0-summary.md`. |
| **B-second-contributor** | Clean-checkout gate | After Unity lands, a second team member clones and repeats setup/build on their machine. |
| **Quest hardware** | All device gates (AC12, AC21–AC24) | Confirm which Quest 3/3S devices the team has and enable developer mode. Not needed for M0 itself. |

## Not run / unverified

- Four-agent parallel execution (phases.md S3): this session ran Phase 0 **sequentially as one agent**. `.claude/agents/` definitions don't exist yet, and worktree isolation is unverified.
- Anything in Unity: no `app-unity/` exists. No project YAML, `.meta`, `ProjectVersion.txt` or lockfiles were fabricated.
- C# consumption of the shared fixtures (M1).
- Canonical serializer and scene hashes (S09.1). Result fixtures carry a placeholder hash and are named `shape_example_*`.
- Session-issuance IP/global rate limits: not implemented, and required before any public deployment (S13.2).
- `scripts/check_content.py`: not implemented (there are no assets yet).

## Planned (not started)

M1 core (state, commands, evaluator in C#) · M2 hands-only rehearsal · M3 deployed voice creation (transcription/proposal jobs, worker leases, idempotency, consent, retention) · M4 device quality · M5 submission. See `phases.md` and technical-spec S22.

## Next

1. **When Unity is installed (A):** create `app-unity/` through Unity Hub with the URP template, add Meta XR SDK 207.0 and OpenXR, enable the Meta Quest feature group for Android and Standalone, open an Interaction SDK hand sample in the simulator, add `Tryscape.Editor.BuildCli.BuildAndroid`, build an ARM64 IL2CPP development APK, and pin `ProjectVersion.txt`, `manifest.json` and `packages-lock.json`.
2. **Unblocked now (B, smallest task):** create `Tryscape.Core` value types (`Rational`, `Footprint`, `SceneSpec` DTOs) plus a segment-vs-expanded-rectangle evaluator that reproduces `evaluation/expectations.json`. Its EditMode tests run once Unity is available, or in a .NET harness if one is approved.
3. **After the provider decision (C):** add the provider adapter and settings, then the transcription/proposal job routes with idempotency, session scoping, the worker lease loop and cancellation.
