# Phase 2 summary: evaluator implemented and automated-verified; phase blocked

Date: 5 November 2026 · Input commit `1a53ddd` · Integration commit `05ac0f6` (docs recorded in the following commit) · Status: **blocked** (manual walkthrough, provider decision, DevAgent token rotation).

**Automated completion:** done for workstreams B (evaluator) and A/D (replay, explanation, stale result). **Overall phase acceptance:** not reached; see open gates.

## Objective (phases.md S8)

The reference route is blocked. A meaningful hand edit makes it clear. A different edit can block the alternative route. The explanation names the real modelled reason, and each result is attributed to its scene, revision and evaluator.

## Execution model

Sequential, single agent; no parallel workers or worktrees. Workstream C (backend intent extraction) was **not started**, because the owner deferred the provider decision (B-provider-budget). Local evaluation does not depend on it.

## Implemented behaviour

| Layer | What exists now |
| --- | --- |
| Core: `Geometry/Rational` | Exact rational in lowest terms; checked 64-bit comparisons. |
| Core: `Evaluation/RouteEvaluator` (v1.0.0) | S08.2 in doubled mm. Each floor-obstructing blocker is expanded by the payload extents plus the 25 mm margin, and every route segment is intersected with that closed rectangle (slab method, exact). The room is the permitted-centre rectangle, closed. Earliest violation: segment, then fraction, then room before blocker, then UUID ordinal. Contact at segment 0, t=0 becomes `invalid_start`. An invalid scene or unknown route gives `invalid_input` with validation errors and no scene hash. No float, frame, Unity, clock or network input. |
| Core: `Evaluation/EvaluationModel` | `Violation`, `EvaluationEvidence` (deterministic projection; its constructor rejects contract-violating combinations), `EvaluationResult` (run ID + `created_at` envelope; `ToWire()` gives the full contracts/v1 field set). |
| Core: `Evaluation/PlaybackTimeline` | 20 Hz logical ticks. Position at tick k is exact (k·speed/20 mm, clamped at the stop). Display interpolation happens only after the exact positions are fixed. A blocked replay stops at the contact centre, where the footprints are still the model margin apart. |
| Core: `Canonical/*` | Restricted canonical JSON (S09.1), serializer v1.0.0, SHA-256 `scene_hash` and evidence hash. The scene hash includes revision and active route and excludes `confirmed_at`. |
| Application: `Rehearsal/*` | `RehearsalSession`: evaluates the live active route, keeps 20 immutable runs (each with its own scene snapshot) and detects stale results by revision. `Explanation`: three authored layers (observed / modelled reason / supported action) plus the scope line, selected by `message_key`. |
| Unity | Panel **Rehearse / Replay / Details** and a result card. After any edit the card shows "OUT OF DATE … Rehearse again" and stale replay is refused. Playback moves the sofa view from the timeline; pause and resume work, and replay restarts from the beginning. When playback ends, the contact footprint, contact pin and an outline on the named blocker are shown. |
| Contracts | `fixtures/canonical/hashes.json`: cross-runtime bytes and hashes from a second, Python serializer in `check_contracts.py` (148 checks). |
| Tooling | `unity_batch.sh` takes `--artifact` (deleted before the run; must be fresh and non-empty after), writes `<step>.status.json` and reports `clean_success` vs `success_with_shutdown_abort` separately from Unity's exit code (`--strict` exits 3 for the latter). `VerifyBatch` now writes `artifacts/unity/verify-config.json`. |

### Fixture/spec review

All eleven expectations reproduce unchanged, so no fixture–specification contradiction was found. Two clarifications are recorded:

- A room/blocker tie at an equal fraction can't occur for a valid scene with axis-aligned segments: the blocker would have to lie beyond the wall. The tie rule stays defined, but no test can reach it.
- `unsupported` is reserved. Evaluator 1.0.0 never emits it, because every R0 limitation (arbitrary yaw, diagonal segment) is already rejected by semantic validation and reported as `invalid_input`.

## Verification (commit `05ac0f6`, this machine)

| Check | Command | Result | Evidence |
| --- | --- | --- | --- |
| Contracts + canonical cross-check | `uv run --locked scripts/check_contracts.py` | 148 passed, exit 0 | `artifacts/phase-2/check_contracts.txt` |
| EditMode | `unity_batch.sh editmode-p2 … --artifact …/editmode.xml`; `check_test_results.py --min-tests 122 --no-skips` | 122/122, 0 skipped; unity exit 0 (`clean_success`) | `artifacts/phase-2/editmode.xml` |
| Mutation (closed→open interval in `Slab`) | same, mutated build | 118 passed, **4 failed as intended**; reverted, then 122/122 | `artifacts/phase-2/editmode-mutation.xml` |
| PlayMode Experience | `-testCategory Experience`, `--min-tests 16 --no-skips` | 16/16 (4 new); unity exit 0 | `artifacts/phase-2/playmode.xml` |
| Simulator smoke | `scripts/run_xrsim_smoke.sh` | 2/2, unity exit 0 each, zero error logs | `artifacts/phase-2/xrsim-*.xml` |
| Config verify | `VerifyBatch` + `--artifact verify-config.json` | 0 problems, unity exit 0. An earlier run in this phase: exit 134 after the marker (`success_with_shutdown_abort`). | `artifacts/phase-2/verify-config.json` |
| Android APK | `BuildCli.BuildAndroid` + 2 artifacts | `clean_success`, 6m21s, clean tree, sha256 `a3a7d503…`, 106,713,636 B, arm64-v8a | `artifacts/phase-2/apk-manifest.json` |
| Backend | not re-run | unchanged since `135f1fa` (78 tests then) | – |

New EditMode groups: RouteEvaluator (19), RehearsalSession (6), CanonicalHash (6), PlaybackTimeline (8), Rational (1). Expected values come from catalog dimensions, worked in test comments, or from the shared `expectations.json`. None is copied from evaluator output, except the cross-runtime hash pins, which are labelled as agreement between implementations.

## Security status

- **DevAgent token:** 0 hits in the unpacked APK and in every git object, including unreachable ones. It is a locally generated Remote Agent Server token for this Editor (stored in EditorPrefs and injected at build time), not a Meta account credential. It has **not been regenerated**, so the exposed value still authenticates to this Editor's agent server whenever that server runs, and it listens on all interfaces. Manual action: *Unity Editor → Meta XR AI Tools (Agent Bridge) settings → Remote Agent Server → Access Token → Regenerate → confirm.*
- **Correction:** development APKs contain this Mac's private LAN address in `boot.config` (`player-connection-ip`). This is Unity's standard development-build player/profiler connection, not DevAgent and not a credential. The Phase 1 note said "token/IP stripped (0 hits)"; that is true only of the DevAgent settings asset. Release-build `boot.config` is unverified because no release build has been made (no keystore).

## Remaining defects and unperformed checks

- **Not run:** manual simulator walkthrough, including result-card readability. The 150 px card can truncate long text, and nothing automated checks this.
- **Blocked:** backend intent extraction (provider decision); DevAgent token rotation (human).
- **Device:** no Quest, so playback smoothness, frame rate, comfort, IL2CPP hashing and NFC normalisation of non-ASCII labels on device are all unverified.
- **Not built (Phase 2 scope remainder):**
  - movers following the payload;
  - route direction markers;
  - Attempt A/B comparison UI, deferred to Phase 4 with persistence;
  - seek control in the UI (`PlaybackController.Seek` exists but has no button).
- The sofa view stays at the stop position after replay until the next edit or Rehearse; this is intended.

## Next

Human: the walkthrough in `docs/progress.md`, then regenerating the token. Agent, when directed: Phase 4 comparison and persistence, or the Phase 2 presentation remainder above.
