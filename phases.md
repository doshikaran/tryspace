# Tryscape — Four-Agent Development Phases

Version: 1.1 · Prepared: 4 November 2026

Status: execution instructions and planned work; no phase has been executed by creating this file.

## 1. Objective and authority

Build Tryscape through a repeatable loop: **assign four parallel engineering workstreams → implement → integrate → verify → summarize → advance**. Phase 0 establishes the repository/folder structure, reproducible Unity foundation and the required Python AI-backend baseline/deployment plan. The loop advances automatically when required gates pass; routine phase transitions do not require another user confirmation.

Read `CLAUDE.md`, `docs/demo-scope.md` and relevant sections of `docs/technical-spec.md` before executing. This file controls sequencing, agent ownership and phase handoffs. The technical specification controls architecture/contracts; demo-scope controls the product experience and acceptance cases. Do not silently change either to make a phase pass.

The current implementation reference is Moving Day, explicitly provisional after the expanded demo discussion. Build reusable boundaries and this reference pack, not every proposed use case. If the owner chooses another demo, update scope and its fixtures before changing scenario-specific work. Foundational repository/XR work remains reusable.

**This Markdown file is not an executable scheduler.** It instructs the main Claude Code session to coordinate the process. Phase 0 creates the agent configurations and progress/evidence checks needed for reliable execution. If the installed runtime cannot run four parallel workers, report that limitation and the available fallback; do not pretend sequential work was parallel or that this document alone installed an orchestration system.

## 2. Technical stack

| Layer | Selected stack | Implementation status / constraint |
| --- | --- | --- |
| Native application | Unity 6 LTS, C#, URP | Select a compatible LTS patch and pin it during Phase 0. |
| XR runtime/input | Unity OpenXR, Meta Core and Interaction SDK | SDK 207.0 family is the initial compatibility candidate; confirm the installed combination. |
| Desktop testing | Standalone Meta XR Simulator | Candidate 207.1 on supported Windows/Apple Silicon systems; actual platform compatibility must be checked. |
| Simulation authority | Pure C# domain/evaluator | Deterministic commands, fixed-orientation footprint checks, replay and evidence; no Unity physics authority over outcomes. |
| Speech | Unity audio capture → required Python backend ASR adapter | Select approved hosted or server-run ASR at Phase 0; no on-device ASR requirement. |
| Scene creation | Backend model-assisted intent → strict validation/bounded SceneSpec assembly → Unity validation and curated-prefab instantiation | Real model-assisted interpretation is required; arbitrary generated meshes/foundation-model training remain deferred. |
| Local data | Versioned JSON snapshots via Unity-compatible Newtonsoft.Json | Atomic recovery, immutable saved runs, no audio/secret retention in projects. |
| Required AI backend | Python 3.12, FastAPI, Pydantic 2, Uvicorn, HTTPX | R0 primary voice/scene creation; native evaluation, replay, editing and local save stay in C#. |
| Backend database | PostgreSQL 18, SQLAlchemy 2, Alembic | R0 minimum jobs/sessions/idempotency and consent/audit records; account/project synchronization remains R1. |
| Testing | Unity Test Framework; required pytest/Ruff/mypy and backend/database checks | Shared fixtures plus real Android/device checks; simulator and mocks have defined limits. |
| Build/review | Git, isolated worktrees, Unity batch builds; GitHub Actions or existing runner | Runner/license availability must be established; no paid infrastructure assumed. |
| Development orchestration | Main Claude Code coordinator plus four engineering subagents | Prefer worktree isolation; concurrency/cost are bounded and configured explicitly. |

Meta currently lists SDK 207.0 and a Unity minimum of 6000.0.66f2; that published minimum is not evidence that our candidate package set has been tested. Pin exact editor, package and backend model/provider versions only after a successful baseline build. See technical-spec S03 for details.

## 3. Coordinator and four engineering agents

There are **four engineering workers**. The existing main Claude Code session coordinates them, integrates commits and records phase outcomes. It is not a fifth parallel engineering workstream. Each worker runs a bounded assignment for the current phase and returns a structured handoff. Workers do not spawn more workers.

| ID | Agent | Primary ownership |
| --- | --- | --- |
| A | XR / Client Engineer | Unity project, OpenXR/Meta setup, app coordinator, scene/presentation adapters, local persistence, Android builds. |
| B | Simulation / Contracts Engineer | Pure C# state, schemas/fixtures, commands/revisions, geometry evaluator, deterministic result evidence and core tests. |
| C | Voice / Services Engineer | Capture/HTTP adapters, required FastAPI API/worker, ASR/intent models, strict proposals, job/session security and deployed service reliability. |
| D | Experience / Quality Engineer | UI/experience prefabs, onboarding, acceptance harness, content/license checks, usability and release evidence. |

### 3.1 Proposed agent configuration

Phase 0 creates four project-local definitions under `.claude/agents/`, using the installed Claude Code's supported configuration fields. Names: `xr-client`, `simulation-contracts`, `voice-services`, `experience-quality`. Each definition must include role, owned paths, relevant spec sections, current-phase-only boundary, required return format and prohibition on claiming unrun tests.

Use worktree isolation for code-writing subagents where supported (`isolation: worktree` in the documented subagent configuration). Verify actual isolation before concurrent writes. Keep experimental agent-team behavior disabled unless deliberately selected and tested: current Claude documentation notes that named workers can be launched as teammates under team mode with different working-directory behavior. A configuration field alone is not proof that a worker is isolated.

If using agent teams instead of subagents, verify each worker's branch and checkout and maintain disjoint write ownership. Agent teams are experimental and disabled by default in the current official documentation; use the supported setup for the installed version rather than inventing a CLI command.

The user has authorized four parallel engineering agents. Stay within available account limits and approved spending. Do not silently add unlimited teammates, run indefinite repair loops, enable bypass-permission modes, or purchase infrastructure. No specific model name is required; use the configured available model and reserve expensive reasoning for difficult design/debugging when budget permits.

### 3.2 Write ownership and Unity serialization

| Path | Writer |
| --- | --- |
| Root build config, `.claude/`, `phases.md`, progress state and phase summaries | Coordinator; workers propose changes through handoff. |
| `app-unity/Packages/`, `ProjectSettings/`, bootstrap scenes and shared XR rig | A only; coordinator integrates. |
| `app-unity/Assets/Tryscape/Core/`, `contracts/` | B. |
| `app-unity/Assets/Tryscape/Application/` | A; B/C request interface additions rather than editing it concurrently. |
| `app-unity/Assets/Tryscape/Unity/` excluding `Experience/`, `XR/`, `Infrastructure/Persistence/`, `Editor/` | A. |
| `app-unity/Assets/Tryscape/Infrastructure/Voice/`, `Infrastructure/Network/`, `services/api/` | C. |
| `app-unity/Assets/Tryscape/Unity/Experience/`, `Content/`, `Tests/PlayMode/Acceptance/` | D. |
| `Tests/EditMode/Core/` | B. |
| `Tests/EditMode/Voice/`, `Tests/PlayMode/Voice/` | C. |
| `Tests/PlayMode/XR/`, `Tests/EditMode/Persistence/`, build scripts | A. |
| Contract/content/release check scripts and acceptance matrix | D; B supplies contract fixture requirements. |

Longest matching path ownership wins. Do not infer permission to edit a shared file from owning a parent directory. Check actual branch paths before merge. Shared contract changes are integrated at a barrier and published to all workers before dependent work starts.

Commit Unity `.meta` files. A is the only bootstrap-scene editor; D supplies separate UI/content prefabs and setup instructions. Do not open one Unity project directory in multiple editor processes. Worktrees have separate Unity caches; limit simultaneous heavy imports/builds according to machine resources. Parallel code work does not imply four concurrent Android builds are affordable or useful.

### 3.3 Safe bootstrap exception

Before spawning workers, the coordinator verifies the working tree, preserves user edits, establishes a Git repository if absent, and creates a minimal baseline commit containing the existing documents and orchestration state. Worktrees need a valid Git baseline. This brief setup is sequential by necessity, not a reason to serialize the whole phase.

Phase 0 then runs four independent workstreams from that same baseline. Until A's canonical Unity project is integrated, B/C/D work on contracts, feasibility notes, checks and plans outside the Unity asset tree. This avoids independently generating incompatible project GUIDs or four competing Unity projects.

## 4. Phase execution loop

### 4.1 State machine

`planned → ready → running → integrating → verifying → summarizing → passed → next phase`.

Failure goes to `repairing`, then back to integrating/verifying. A missing external prerequisite goes to `blocked`; dependent phases cannot advance. Phase 6 may use `ready_for_external_action` when all preparation is verified but the external action is not yet authorized/completed. Terminal completion is `complete` after the applicable Phase 6 gates pass. Shutdown, context exhaustion or runtime interruption must leave a resumable checkpoint rather than a false passed state.

### 4.2 Coordinator algorithm

1. Read progress state, the last passed summary, current repository commit and working-tree status. Reconcile missing or stale worker sessions before launching replacements.
2. Check phase prerequisites. Freeze the phase input commit and interfaces. Set status to running.
3. Assign all four workers independent phase-specific tasks with exact allowed paths, dependencies, required evidence and time/turn budget. Start them concurrently where supported.
4. Let workers implement/test their owned scope. A worker encountering an upstream contract issue reports it; it does not write another worker's files or begin a future phase.
5. Collect all four handoffs. `Implemented` and `verified` are separate fields. An idle worker may inspect/review relevant work, but must not fabricate a feature solely to stay busy.
6. Inspect actual diffs and commits. Integrate sequentially in dependency order B → C → A → D by default; change order only for declared dependencies. Never auto-resolve a semantic conflict by accepting one entire file.
7. Run integrated checks, not just branch-local checks. Confirm the test runner executed tests and that expected artifacts exist.
8. Run a bounded cross-review round: A reviews D, D reviews C, C reviews B and B reviews A. Reviewers read the integrated snapshot and report issues; fixes return to the owning worker. D additionally reconciles acceptance evidence.
9. Repair actual failures under the bounded policy below. Mark unresolved hardware/tooling checks explicitly.
10. Write the phase summary with outputs, integration commit, evidence, decisions, known limitations and next tasks. Update progress atomically.
11. If required gates pass, mark passed and automatically start the next phase. No routine permission question. If blocked, state the exact missing prerequisite, save the checkpoint and continue independent authorized work where possible.

This is a bounded phase loop, not a shell `while true` around an unconstrained prompt. The coordinator owns advancement; individual workers cannot mark the whole phase passed.

### 4.3 Repair and budget policy

Allow up to two targeted repair cycles for the same unresolved failure before re-evaluating the approach. Each cycle records hypothesis, change and check. A third cycle requires a new concrete hypothesis or a declared blocker; do not repeat identical failing commands indefinitely. This is not a two-bug limit: unrelated defects can each receive appropriately scoped repairs within the phase budget.

At phase start, record a finite session/turn budget supported by the runtime and inspect available account limits. At the boundary, record actual usage if the tool exposes it. Do not invent cost figures. If limits interrupt work, checkpoint and stop launching new workers. User authorization to parallelize is not authorization for unlimited spending.

No destructive repository reset, credential disclosure, external publishing, paid resource creation or Devpost submission is implied by this loop. Prepare reviewable release artifacts; follow the user's separate authorization for external actions. Routine local code changes, tests and reversible integration proceed autonomously.

## 5. Persistent state and summary format

Phase 0 creates these future files; this plan does not claim they already exist:

- `docs/progress/phase-state.json` — coordinator-owned authoritative progress.
- `docs/progress/phase-0-summary.md` through `phase-6-summary.md` — concise human-readable handoffs.
- `docs/progress/phase-N-agent-A.md` etc. — optional concise worker reports, no raw private prompts/audio.
- `artifacts/phase-N/` — ignored logs, test reports, build manifests and profiler captures; summarize durable metadata/checksums in committed records.

### 5.1 Initial state shape

```json
{
  "schema_version": "1.0.0",
  "current_phase": 0,
  "status": "planned",
  "input_commit": null,
  "integration_commit": null,
  "worker_runs": {
    "A": {"status": "planned", "branch": null, "commit": null},
    "B": {"status": "planned", "branch": null, "commit": null},
    "C": {"status": "planned", "branch": null, "commit": null},
    "D": {"status": "planned", "branch": null, "commit": null}
  },
  "gates": [],
  "blockers": [],
  "completed_phases": [],
  "next_action": "Bootstrap repository, then launch Phase 0 workstreams"
}
```

Each gate record is `{id, required, status, command_or_procedure, environment, evidence_path, evidence_sha256, notes}`. Gate status is one of `not_run`, `pass`, `fail`, `blocked`, `not_applicable`. Only genuinely conditional features may be not_applicable, with rationale. A mandatory device test cannot be not_applicable because no headset was available. Enforce allowed transitions and atomic state-file replacement; never infer a pass from an absent gate entry.

On resume, a recorded running worker is not assumed alive. Check the actual session/branch/commit, preserve its work and reconcile before restart. Evidence references must match the integrated commit. A source change affecting a passing gate invalidates that gate until rerun or explicitly unaffected with a reason. Do not repeat an expensive test after unrelated prose edits.

### 5.2 Required worker handoff

For every worker: phase and agent ID; input commit; output branch/commit; owned files changed; implemented behavior; exact checks actually run and exit status; manual tests not run; assumptions/decisions; blocking dependencies; integration instructions; next smallest task. Keep concise; link logs instead of pasting thousands of lines.

### 5.3 Required phase summary

For every phase: objective, four worker outcomes, integrated commit, observable behavior now available, gates and evidence, unresolved limitations, changes to contracts/toolchain/scope, selected repair decisions, next phase assignments, and final status. A summary is written for failed/blocked phases too, so restarting does not erase the reasoning.

## 6. Phase 0 — Repository structure and reproducible foundation

**Goal:** one canonical project layout, clear agent boundaries, shared contracts and a repeatable Unity/Android baseline.

| Agent | Parallel assignment |
| --- | --- |
| A | Inspect installed tools; create canonical `app-unity/` via supported Unity/Meta setup; configure OpenXR, hand sample and Android ARM64; pin versions; create build entry point and environment record. Own Unity metadata/lockfiles. |
| B | Extract v1 JSON Schemas/fixtures from technical-spec S06/S09/S10 into `contracts/`; identify exact command/result invariants; independently check F01–F04 expected values. Work outside Unity until canonical project integrates. |
| C | Create the required FastAPI/PostgreSQL baseline and initial migrations/contracts. Audit available ASR/intent providers, licenses, quotas and approved spending; perform a real provider/compute feasibility check and establish the judge-reachable HTTPS deployment path. Do not purchase or publish infrastructure without the applicable authorization. |
| D | Define acceptance checklist and evidence records; implement initial document/fixture/content check utilities where useful; draft ignore/license inventory requirements and a clean-clone verification procedure. |

Coordinator creates agent definitions, progress schema, write-ownership rules and a short `README.md` with tested setup commands after integration. Add a concise reference to this file in `CLAUDE.md`; preserve its existing product/quality instructions. No wholesale rewrite of the technical spec.

Required folder targets: `app-unity/Assets/Tryscape/{Core,Application,Unity,XR,Infrastructure,Editor,Content,Tests}`, `contracts/v1/`, `scripts/`, `docs/progress/`, `docs/environment/`. Establish `services/api/` for required R0 creation endpoints/worker and minimal migrations. Deferred R1 account/sync APIs remain absent; no fabricated successful API. Exclude caches, native model downloads, logs, keystores and secrets. Preserve `.meta` files for actual imported assets.

**Gates:** exact toolchain recorded; Unity project imports/compiles; hand sample runs in simulator; first Android APK builds; contract/example checks pass; second clean checkout repeats required setup/build; four worker isolation/ownership settings verified; backend baseline imports/tests pass; real ASR/intent-model access and an approved release deployment path are verified or recorded as blockers. Missing Unity/Android licensing/modules is a blocker for the baseline, not a reason to fabricate project YAML or report a build pass.

**Summary:** `phase-0-summary.md`. This phase corresponds to M0 in the technical specification, plus orchestration setup. Full voice integration continues in Phase 3, but required model access/deployment feasibility is checked now. A missing viable backend/model path blocks dependent creation work; continue independent authorized core work without falsely passing that gate.

## 7. Phase 1 — Authoritative state and first hand-driven slice

**Prerequisite:** Phase 0 baseline and v1 contracts integrated.

| Agent | Parallel assignment |
| --- | --- |
| A | Implement app coordinator/composition root, model-to-tabletop transform, selection/drag preview/commit wiring and one prepared scene. |
| B | Implement pure-core SceneSpec validation, immutable state, command/revision logic, undo/redo and typed failures; write boundary and duplicate-command tests. |
| C | Implement actual recording upload, backend ASR job/session flow, resource/cancellation checks and real service observations. Keep client adapters behind the agreed contracts; no native-ASR detour. |
| D | Build hand-selectable creation/settings/help UI prefabs; add acceptance harness for select/move/reset and clear invalid-placement feedback. |

**Integrated outcome:** create a prepared scene with hands, move an editable blocker, reject invalid placement, undo/redo and reset with correct revision semantics. Tabletop resizing cannot resize domain geometry. Backend speech/model feasibility has real observations, not only SDK/provider availability claims.

**Gates:** core command tests pass; input-to-command PlayMode checks pass; complete basic scene flow works without controllers; Android build remains healthy; drag cancellation/lost-tracking behavior has defined automated checks and device evidence where required. Record the exact backend/model deployment configuration and any concrete blocker for dependent work.

**Summary:** `phase-1-summary.md`. Next tasks use the actual merged interfaces, not workers' independently guessed versions.

## 8. Phase 2 — Evaluation, replay and explained consequences

**Prerequisite:** stable state/commands and integrated hand manipulation.

| Agent | Parallel assignment |
| --- | --- |
| A | Integrate evaluator interfaces and deterministic playback, pause/seek/recenter; keep render interpolation separate from geometry authority. |
| B | Implement continuous path/expanded-footprint checks, earliest-violation tie-breaks, canonical evidence/hashes and F01–F04 fixtures. |
| C | Implement backend model-assisted intent extraction, strict Pydantic/catalog validation, deterministic bounded proposal assembly and parameter handling through published contracts; continue ASR corrections without changing client core state directly. |
| D | Build explanation/contact highlighting, stale-result indicator and basic attempt comparison UI; independently review fixture math and visible feedback. |

**Integrated outcome:** reference route is blocked, a meaningful hand edit changes it to clear, a different edit can block the alternative route, and the explanation identifies the real modelled reason. A saved result is attributed to its scene/revision/evaluator.

**Gates:** F01–F04 and thin-obstacle/boundary tests pass; render frame rate does not change outcomes; run-envelope IDs/timestamps are excluded from deterministic evidence hashing; a full edit/replay/explain cycle works. Basic comparison may be completed here, with persistence hardened in Phase 4.

**Summary:** `phase-2-summary.md`. No prerecorded success animation may substitute for evaluator integration.

## 9. Phase 3 — Voice-created environment and full creation flow

**Prerequisite:** validated scene creation contracts, working rehearsal and the required backend/model access baseline from Phase 0.

| Agent | Parallel assignment |
| --- | --- |
| A | Integrate capture/proposal/review/application state machines; transactional staged scene replacement and lifecycle cancellation. |
| B | Validate proposed scenes/catalog IDs/provenance; add invalid/oversized/unsupported and stale-revision fixtures; prevent guessed dimensions becoming measured claims. |
| C | Finish the deployed backend speech/intent-model pipeline, supported parameter extraction and bounded clarification. Implement required ASR/proposal jobs, polling/cancellation, session isolation, limits, retention and deadlines; R1 project/account APIs stay deferred. |
| D | Build recording/review/confirmation screens, hand-only parameter equivalent, understandable fallback messages and voice acceptance measurement protocol. |

**Integrated outcome:** user speaks a supported situation; the created scene reflects the request; the user reviews it; hands control the rest. A changed supported room dimension changes actual scene data and display. Mic denial, unsupported prompts, cancellation and service failure preserve the usable local experience.

**Gates:** actual supported voice benchmark run on the selected deployment; strict provider output validation; late/cancelled result cannot replace a scene; full hands-only alternative works; no secret/provider key embedded in APK; the required backend/model path is judge-reachable without our laptop or the judge's paid account, within approved spending, with measured complete-path latency. Local templates or keyword parsing cannot substitute for model-assisted creation. A functioning hand fallback does not itself pass the voice requirement.

**Summary:** `phase-3-summary.md`, including actual latency/reliability/memory observations and deployment limits.

## 10. Phase 4 — Persistence, comparison and recovery

**Prerequisite:** complete creation-to-rehearsal journey integrated.

| Agent | Parallel assignment |
| --- | --- |
| A | Implement atomic local project storage/recovery, stable resume, resource disposal and build configuration separation. |
| B | Harden immutable run snapshots, migrations/hash consistency, comparison attribution and undo/reset persistence invariants. |
| C | Harden client/backend cancellation, provider failure and temporary-audio cleanup; validate deployed job ownership, limits, expiry, idempotency and worker lease recovery. |
| D | Complete compare/save/open/delete UI, preparation summary, accessibility feedback and interrupted-session acceptance coverage. |

**Integrated outcome:** create, rehearse two attempts, compare, save, restart and reopen without losing attribution or secretly changing prior results. Corrupt files and interrupted writes recover predictably.

**Gates:** relevant persistence interruption/migration tests pass; stored data excludes raw audio/tokens; quotas and deletion work; local scene remains useful offline; stale comparison and background/resume tests pass; fresh Android build and core regression suite pass.

**Summary:** `phase-4-summary.md`. Cloud accounts/sync remain deferred unless explicitly brought into scope; their absence does not justify stubbing successful endpoints.

## 11. Phase 5 — Device quality and release candidate

**Prerequisite:** required R0 functionality implemented; known defects tracked.

| Agent | Parallel assignment |
| --- | --- |
| A | Profile Quest rendering/build, tracking/input lifecycle and crash/resource issues; fix client defects. |
| B | Review geometry/canonicalization against independent fixtures and IL2CPP behavior; resolve core correctness defects. |
| C | Validate the real backend ASR/intent-model creation pipeline from the target headset under realistic network/load conditions, privacy cleanup and approved resource limits. |
| D | Run full acceptance matrix, seated novice sessions, visual/audio/readability checks and license/content inventory; prepare actual-footage capture plan. |

**Integrated outcome:** a polished candidate whose limits are accurately described and whose core journey is reliable on actual target hardware.

**Gates:** technical-spec S23 R0 gates with evidence; required frame-rate criterion on Quest; thirty-minute soak; real hands/permissions/lifecycle tests; voice performance results; no critical/major unresolved core-path defect; cross-runtime fixtures; novice comprehension observations and asset/privacy checks.

If required device testing is unavailable, record blocked coverage and complete other checks. Do not mark this phase passed from simulator-only evidence or silently advance to a supposedly verified release. Stop launching unrelated features during stabilization.

**Summary:** `phase-5-summary.md`, including known issues, measured performance and candidate artifact hashes.

## 12. Phase 6 — Package, verify access and prepare handoff

**Prerequisite:** release candidate gates pass.

| Agent | Parallel assignment |
| --- | --- |
| A | Produce signed release APK, build manifest, repeatable install instructions and frozen client/config versions. |
| B | Reconcile declared capabilities with evaluator/contracts and verify result fixtures against the exact candidate commit. |
| C | Verify availability/retention/monitoring for the required creation service and freeze model/catalog/provider configuration; document incident response. |
| D | Prepare submission text/checklist and actual-footage video under three minutes; verify required form fields and access-testing procedure. |

Coordinator assembles the final release summary and a concrete reviewable submission package. Actual external upload/publishing/Devpost submission follows the user's authorization; preparing this plan does not grant it. Do not claim an invite URL exists until created and tested.

**Gates:** exact APK/video/form package matches tested commit; channel access and real install are verified when authorized; service dependencies remain accessible through judging; no secrets or private recordings are included; freeze requirements and operational ownership are documented. If external actions remain unapproved, mark `ready_for_external_action`, preserve the package and identify the precise remaining action rather than claiming submission completion.

**Summary:** `phase-6-summary.md`. Terminal development state is `complete` only for the gates actually satisfied. Production R2 readiness remains a separate set of requirements in technical-spec S23, even if the competition build is ready.

## 13. Cross-phase rules and resumption

- Every phase begins from a known integrated commit and published interfaces.
- Every phase ends with a summary before dependent work starts.
- The coordinator can continue automatically through passing phases within available runtime/budget; there is no mandatory user approval after each ordinary phase.
- Missing toolchains/hardware/account access can block specific gates; describe the source of the block accurately. Elapsed time is not proof of approval or verification.
- Review/repair uses the same four workers; do not create an expanding agent tree.
- Never rewrite these phase definitions after a failure merely to change the gate to pass. A justified scope change is recorded with user direction and affected acceptance cases.
- If a session ends, the next session reads this file, state and the most recent summary, inspects current source and resumes the unfinished phase. It does not restart Phase 0 or trust stale memory over files.
- If the tooling cannot keep running across session endings, say so. Durable state supports resumption; it is not a background automation guarantee.

## 14. First coordinator instruction

Read this file and the existing specifications. Inspect actual repository/tooling state. Bootstrap the minimal Git/state baseline, then start Phase 0 with four isolated engineering workers using the ownership table. Integrate their work, verify phase gates, write the summary and advance automatically when gates pass. Preserve the current product scope, stop dependent work on genuine blockers, and report only checks actually performed.

Revision 1.1 — 4 November 2026: aligned with technical-spec/demo-scope/CLAUDE version 2.1. Required backend work starts in Phase 0; primary voice creation uses server-assisted ASR and model interpretation. Offline rehearsal and deferred R1 synchronization retain their existing boundaries. Orchestration policy is unchanged.

## 15. Sources and platform notes

Checked 4 November 2026. The phase sequence, repair policy and ownership boundaries above are project decisions, not guarantees supplied by Claude Code.

- Claude Code subagents / isolation: https://code.claude.com/docs/en/sub-agents
- Claude Code agent teams / experimental behavior: https://code.claude.com/docs/en/agent-teams
- Meta SDK 207 baseline: https://developers.meta.com/vr/downloads/package/meta-xr-sdk-all-in-one-upm/207.0/
- Backend ASR runtime candidate: https://github.com/ggml-org/whisper.cpp

Confirm features against the installed tool version before creating agent configurations. A runtime with no working concurrency support must not be represented as a four-agent execution.
