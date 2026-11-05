# Phase 0 / M0 summary: blocked

Date: 5 November 2026 · Input commit `9f35e64` · Integration commit `4976e6a` (docs/state recorded in the following commit) · Final status: **blocked** (external prerequisites).

## Objective

Reproducible baseline: one repository layout, shared contracts, a Unity/OpenXR/Meta project with a simulator hand sample and a first ARM64 APK, a FastAPI/PostgreSQL backend baseline, and a verified model/deployment path or a recorded blocker.

## Execution model

phases.md S3 calls for four parallel workers. This session ran Phase 0 **sequentially as a single agent**, because the Unity workstream (A) had no toolchain to work with and the remaining work was small and tightly coupled. No `.claude/agents/` definitions were created, and gate `P0-worker-isolation` is `not_run`. The sections below are workstream slices, not separate worker runs.

| Workstream | Outcome |
| --- | --- |
| A, XR/client | **Blocked.** No Unity Hub/Editor, Android modules, JDK or Meta XR Simulator on this Mac. Toolchain candidates were researched and recorded. No Unity files were created. |
| B, contracts | **Done for M0.** v1 schemas, fixtures, F01–F04/boundary expectations, contract checker. |
| C, services | **Baseline done; model/deployment blocked.** FastAPI/PostgreSQL baseline with tests; provider and hosting need an owner decision. |
| D, quality | **Partial.** Release/gate checker, progress tracking, ignore rules. Acceptance checklist and content/licence checker not started. |

## Observable behaviour now available

- `uv run --locked scripts/check_contracts.py` validates every contract and fixture, and independently reproduces the D06 expected outcomes.
- `services/api` serves health and anonymous-session routes against PostgreSQL under Uvicorn, and fails closed without configuration.
- `python3 scripts/check_release.py --phase 0` reports which gates are open.

## Gates

See `phase-state.json`. 2/9 pass (`P0-contract-checks`, `P0-backend-baseline`). Evidence logs are in the git-ignored `artifacts/phase-0/`; their SHA-256s are in the state file.

## Findings from the audit (documents vs reality)

1. **Unity line.** Unity 6.0 LTS reached end of support in October 2026. Use **6000.3 LTS**, which technical-spec S03 already prefers; phases.md's "Unity 6 LTS" is consistent with that. Recorded as decision D09.
2. **Simulator on this Mac.** The standalone Meta XR Simulator supports Apple Silicon macOS only, and this is an M5 (arm64) machine, so it qualifies on paper. Compatibility with macOS 26.6 and the M5 is unverified until installed.
3. **Spec gaps resolved during contract extraction:** UUID case, assumption `source` enum, `validation_errors` on invalid-input results, violation fraction semantics at walls vs blockers, tie-breaking between room and blocker, `invalid_start`, and snapping as a command rule. See `contracts/v1/README.md` and decision D10. None of these changes product meaning.
4. **Snapping vs boundary rules.** A 50 mm grid with a 25 mm margin makes exact blocker tangency unreachable by edits. So snapping is enforced on `move_entity`, not as a SceneSpec validity rule. Otherwise the S08.2 boundary fixtures could not be expressed.
5. **Progress files.** The user asked for `docs/progress.md`; phases.md specifies `docs/progress/`. Both exist: the first is the human tracker, the second holds authoritative state and summaries.
6. **Session issuance limits.** S13.2 needs IP/global limits on `POST /v1/anonymous-sessions`. These are not implemented and must exist (edge or app) before any public deployment.
7. **Zero budget vs mandatory hosted AI.** CLAUDE.md requires a judge-reachable backend ASR + intent model, but approved spending is zero and no free quota may be assumed. Until the owner resolves this, it blocks AC02/AC23/AC29. This is the most material open decision.

## Recommendation on B-provider-budget (owner decision)

Approve a small, explicitly capped budget for **hosted, pay-per-request ASR and intent-model APIs**, with the FastAPI service and PostgreSQL on a lightweight managed host. At demo/judging volume (low hundreds of requests), per-request APIs avoid paying for always-on inference compute and the operational risk of self-hosting whisper.cpp plus an LLM. If the budget must stay at zero, the remaining option is self-hosting on hardware the team already owns. Keeping that reachable over HTTPS through judging, without a developer laptop, is hard and must be tested before it's relied on. I have not checked any vendor's prices, quotas or terms; that comparison is the first C-workstream task once the owner names candidates or a cap. Ollama is installed on this Mac, but it is development-only and doesn't satisfy the judge-reachability requirement.

## Changes to contracts, toolchain and scope

- New: `contracts/v1/` (first extraction; no prior wire shape existed).
- Toolchain candidates recorded, nothing pinned (D08 still applies).
- No scope change. Moving Day remains provisional and no other pack was introduced.

## Repairs made in this phase

- A fixture defect caught by the checker: moving the payload start to x=925 left the Northern route diagonal. Fixed the fixture; the checker was unchanged.
- FastAPI dependency resolution: under `from __future__ import annotations`, a closure dependency turned into an unresolved forward reference, so every request returned 422. Moved the dependency to module level.
- The Alembic `path_separator` warning was resolved; the repository path contains spaces.

## Next phase assignments (when unblocked)

- **A:** create `app-unity/` (URP, OpenXR, Meta 207.0), run the simulator hand sample, write `BuildCli.BuildAndroid`, build the first APK, pin lockfiles, and record exact versions in `docs/environment/m0-environment.md`.
- **B (unblocked now):** `Tryscape.Core` value types and the evaluator against `expectations.json`. Decide on the .NET harness question.
- **C:** provider comparison once candidates or a cap are named; then the job routes, worker, idempotency and consent.
- **D:** acceptance checklist (AC01–AC32) evidence template, `check_content.py`, licence register.
- **Coordinator:** create `.claude/agents/` definitions and verify worktree isolation before any parallel writes.

## Update — 5 November 2026 (after Unity and simulator installation)

The owner installed Unity 6000.6.8f1 with Android support and Meta XR Simulator 207.1. The XR half of M0 was completed and verified:

| Gate | Status now | Evidence |
| --- | --- | --- |
| Toolchain recorded | pass | `docs/environment/m0-environment.md` (exact versions, machine fixes, known issues) |
| Unity imports/compiles | pass | headless template creation; `ProjectConfigurator.VerifyBatch` 0 problems |
| Simulator hand sample | pass (automated) | Meta HandGrabExamples + Tryscape rig on the simulator runtime; strict log check |
| First Android APK | pass | first built at `0ab2de6`; current `135f1fa` (arm64-v8a, IL2CPP, API 32/34) |
| Clean checkout repeat | pass (same machine) | clone of `c90a36c`, full import and build, 10m57s |
| Second-contributor repeat | blocked | needs a human |
| Worker isolation | not run | sequential single-agent execution |
| Model access / deployment | blocked | owner deferred the decision |

Toolchain decisions: Unity 6.6 (non-LTS) accepted as installed (D11); Meta 207.0.1 family; OpenXR 1.17.1 chosen to match Meta's version defines; development-build NDK override and CMake installation recorded.

Security and reproducibility issues found and fixed: the Meta DevAgent build processor injected a LAN IP and access token into every APK (now stripped, token purged from git history); 66 orphaned OpenXR settings sub-assets caused build churn; and the code-generated rig was missing scene references (see the Phase 1 summary).

Phase 0 remains **blocked** only on the three items above. Phase 1 proceeded because its prerequisites (the baseline and v1 contracts) were integrated.
