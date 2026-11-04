# Tryscape — Project Instructions

Version 2.1 · 4 November 2026

## Read and understand first

Tryscape turns a described situation into an interactive spatial rehearsal: understand, construct, watch, intervene with hands, replay, compare and prepare. Voice creates/configures the initial environment through the required Python AI backend. Hands operate everything afterward; hands also provide a complete alternative creation path.

Read [docs/demo-scope.md](docs/demo-scope.md) for the current experience and acceptance cases. Read [docs/technical-spec.md](docs/technical-spec.md) for architecture, contracts, algorithms and release gates. Load relevant sections for each task; do not import the entire technical spec into every prompt unnecessarily.

This package contains documentation only. Unity projects, scripts, services, tests and command entry points described below are **to be implemented**. Never report them as existing or passing without inspecting the repository and executing the relevant checks.

Moving Day is a **provisional reference implementation**, not final approval after all later demo discussions. Keep pack logic separate from reusable core. Do not change to another demo, implement multiple packs, or promise arbitrary-domain support on your own. Routine implementation choices within the specified scope do not require repeated permission questions; surface material scope/contract changes with their concrete impact.

## Product and engineering boundaries

- R0 is the competition release; R1 is connected pilot; R2 is public production. Follow technical-spec S01/S23. Do not build R1/R2 services before a demonstrated R0 need.
- Build maintainable, production-oriented code from the start. A document or successful compile is not production-readiness evidence.
- Core owns geometry, commands, results and revisions. AI proposes structured data and cannot bypass validation or execute generated code.
- Preserve local hand-created/rehearsal operation when speech/backend/network fails. Backend ASR + model-assisted interpretation are mandatory for the primary voice-creation feature; do not mark it complete from a keyword parser or template-only fallback. Editing, evaluation, replay, comparison and local saving must not require per-action server calls.
- Use generic licensed assets. Distinguish illustrative defaults from confirmed real-world measurements. Do not claim certified fit, safety, human intent prediction or an owned universal world model.
- No paid dependencies, hosting or model calls without an explicitly approved budget. Do not assume a free quota exists or will remain available to judges.

## Stack and module rules

- Unity 6 LTS + C#, URP, OpenXR, Meta hand interaction SDK; Android ARM64 IL2CPP target. Exact compatible versions are selected and committed in M0.
- Pin `ProjectVersion.txt`, `manifest.json` and `packages-lock.json`. No dependency upgrades during unrelated feature work.
- `Tryscape.Core` is pure C# with no UnityEngine, networking, filesystem, provider SDK or clock dependency. Keep Unity-specific transforms and input in adapters.
- Required R0 Python 3.12 backend: FastAPI/Pydantic 2/Uvicorn/HTTPX, pytest/Ruff/mypy, locked with uv. Backend ASR and model-assisted intent produce validated scene proposals. PostgreSQL/SQLAlchemy/Alembic support minimum R0 jobs/sessions/idempotency; accounts/cloud project synchronization remain R1.
- No extra frontend, Go service, Redis or Kubernetes without a demonstrated requirement.
- Preserve existing source and user edits. Check working-tree state before changing shared files; never discard unrelated work.

## Data and behavior invariants

1. Integer millimetres in model space; x east, z north. Tabletop/XR placement and scale cannot change geometry or results.
2. Follow SceneSpec, EntitySpec, RouteSpec and result contracts in S06–S10. Extract shared schemas/fixtures during M1. Never invent a second wire shape in a service.
3. R0 payload yaw is fixed during transport; routes have axis-aligned translation segments. Editable blocker yaw is quarter turns. Arbitrary rotations/lifting/3D collision are unsupported.
4. Use continuous segment-vs-expanded-rectangle checks; frame samples and Unity physics do not determine success. Follow exact boundary/margin rules in S08.
5. Display `clear_in_model`, `blocked_in_model`, `invalid_input` or `unsupported`; never silently turn these into guaranteed physical outcomes.
6. Commands commit atomically with expected revision; rejected edits change nothing. Undo/redo create new revisions. Drag is preview until validated release.
7. Results retain scene/hash, revision, route, catalog and evaluator version. Live results become stale after edits; saved comparisons stay immutable.
8. Scene generation uses a staged transaction. Malformed, cancelled or stale provider output cannot replace the current scene.
9. File paths derive from validated IDs, not labels. Snapshots use tested atomic recovery and bounded sizes. No raw audio/token in project files.
10. No per-frame network/model calls, sync disk I/O or heavyweight allocation on the rendering path.

## Working method

Before each task: inspect actual files and existing conventions; identify the relevant acceptance cases; state a short implementation approach. Do not start another large planning cycle when a bounded implementation is already defined.

Implement one coherent capability at a time with observable completion criteria. Integrate incrementally into a runnable scene. Prefer explicit interfaces/immutable DTOs and a simple composition root over global singletons or a speculative framework. Do not leave core behavior as TODOs behind a polished UI.

Use PascalCase for C# types/public members, camelCase for locals, `_camelCase` for private instance fields, and snake_case for JSON/Python. Nullable/optional semantics must be explicit. Validate external inputs at the boundary. Expected invalid user actions return typed failures; exceptional infrastructure failures are caught at application boundaries and logged with safe codes.

Use async cancellation and operation/revision IDs for generation and loading. Dispose Unity/native/audio/network resources explicitly. Never swallow an exception and fabricate a successful scene or test outcome.

One owner edits the shared bootstrap `.unity` scene at a time; prefer isolated prefabs for features. Commit `.meta` files. Exclude Unity caches, builds, model downloads, recordings, credentials and keystores. Keep secrets out of APK resources and public configuration. Do not overwrite lockfiles to make a local compile pass without understanding the version change.

Do not deploy, submit to Devpost, publish assets or send communications merely because development is requested. Prepare a concrete reviewable release and follow the user's authorization for those external actions.

## Verification expectations

Write meaningful tests for geometry, boundary cases, commands, cancellation, contracts, persistence and failures. Do not inflate coverage with tests that mirror implementation formulas. Use demo fixtures F01–F04 and independent expected values. A moved obstacle must actually change the result; an unrelated edit must not arbitrarily change it.

Use EditMode for pure-core checks; PlayMode for scene/input/lifecycle adapters; actual Quest for tracking, performance, microphone capture, backend round trips, permissions and comfort. Verify backend inference/service behavior on the actual deployment. Mocks do not establish model accuracy; simulator frames do not establish device performance. Do not repeat expensive suites without a change/failure that justifies them.

Run applicable checks and report exact commands, exit status, failures and untested areas. If Unity/Android toolchain or hardware is unavailable, continue useful pure-code work and clearly mark the missing verification. Never generate fake logs/screenshots, weaken assertions to hide a bug or mark a release gate passed from inference.

## Commands and bootstrap status

The commands below describe the intended interface after M0/M1 scaffolding. Confirm paths and entry points before running. See technical-spec S19 for full arguments and Windows/shell considerations.

- Unity EditMode: pinned editor `-batchmode -projectPath ... -runTests -testPlatform EditMode -testResults ... -logFile ...`.
- Unity PlayMode: same with `-testPlatform PlayMode`; rendering tests need a valid graphics environment.
- Android build: pinned editor `-batchmode -quit -projectPath ... -buildTarget Android -executeMethod Tryscape.Editor.BuildCli.BuildAndroid -logFile ...`.
- Do not add `-quit` to test runs without checking the installed Test Framework's guidance; tests own their completion lifecycle.
- Future repository checks: `python scripts/check_contracts.py`, `python scripts/check_content.py`, `python scripts/check_release.py --profile r0`.
- Required backend checks after scaffolding, from `services/api`: `uv sync --frozen`, `uv run ruff check .`, `uv run ruff format --check .`, `uv run mypy src`, `uv run pytest -q`.
- Local service after implementation: `uv run uvicorn tryscape_api.main:app --host 127.0.0.1 --port 8000`. Localhost is not a judge-accessible endpoint on Quest.

Create required output directories; check process exit codes and test result files, not just log text. A Unity process returning successfully with no executed tests does not pass. Android builds require a properly configured editor/license/modules and signing configuration; record unavailable prerequisites rather than invent them.

## First implementation task

Start with M0: inspect repository, establish/pin compatible Unity/Meta toolchain, configure OpenXR and a hand interaction sample, create the first Android build, record environment and evidence. In parallel with the human team, establish the required FastAPI/PostgreSQL API/worker baseline, select exact ASR/intent models, and verify a judge-reachable HTTPS deployment within approved spending. Record provider/model/prompt/compiler versions and limits; do not postpone service feasibility until final polish.

Then implement M1 core contracts and fixtures, followed by one complete hand-driven rehearsal. Develop the required backend creation jobs alongside the client through shared contracts; integrate voice-to-scene in M3. Defer unused R1 account/project-sync features rather than deferring the R0 backend. No local-parser substitution without an explicit architecture revision.

## Definition of a completed change

- Behavior matches the scoped acceptance case and interfaces; no hidden change to product claims.
- Relevant client/backend compile, contract, job/session isolation, cancellation and database tests pass; manual/device checks are performed or explicitly outstanding. Primary creation requires the real deployed ASR/intent-model path; provider mocks alone cannot pass it.
- Error, cancellation and stale-state behavior are covered where affected.
- New dependency/assets have version and license records; no secrets/private recordings introduced.
- Docs/contracts stay consistent. Future commands are not reported as already implemented.
- Handoff states: what changed, why, how verified, what is unverified, and the next smallest task.

Revision 2.1 makes backend-assisted AI creation mandatory in R0 while preserving offline local rehearsal and hand-template fallback.

For a production release, additionally satisfy S23 R2 gates. Neither Claude's confidence nor an impressive demo replaces measured readiness.
