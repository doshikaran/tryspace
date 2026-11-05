# Phase 1 summary: implemented, blocked on manual/device gates

Date: 5 November 2026 · Input commit `70b445f` · Integration commit `135f1fa` · Status: **blocked** (manual simulator walkthrough, Quest device check).

## Objective (phases.md S7)

Create a prepared scene with hands, move an editable blocker, reject invalid placement, undo/redo and reset with correct revision semantics. Tabletop resizing cannot resize domain geometry.

## Execution model

Sequential, single agent; no parallel workers. Workstreams A, B and D were done. Workstream C (recording upload, ASR job flow) was **not started**, because the provider/deployment decision is deferred by the owner and phases.md forbids a native-ASR detour.

## Observable behaviour now available

From the welcome panel, **Create with hands** opens example values (6 x 5 m, console in the way, direct route), adjustable within 5–8 m. **Create** builds the room through a staged swap. The console can be pinch- or ray-grabbed. While dragging, a floor outline shows the snapped footprint, and invalid spots add stripes plus a reason. Release on clear floor commits one move, at a new revision. An invalid release returns the console and states why. Rotate (quarter turn), Switch route, Undo/Redo/Reset (each a new revision) and tabletop Larger/Smaller/Recenter all work by hand. Voice and saved projects say they are unavailable.

## Gates

| Gate | Status | Evidence |
| --- | --- | --- |
| Core command tests | pass | EditMode 82/82 (`artifacts/phase-1/editmode.xml`) |
| Input-to-command PlayMode | pass | Experience 12/12 through the real Interaction SDK `Grabbable` |
| Basic flow without controllers | automated: pass; **manual simulator walkthrough not run** | procedure in `docs/progress.md` |
| Android build healthy | pass | `135f1fa`, clean tree, sha256 `2ac67bfc…` |
| Drag cancel / lost tracking | automated: pass; **device (AC12) blocked** | no headset |
| Backend/model deployment record | recorded as blocker | `P0-model-access-deployment` |

## Defects found and fixed in this phase

1. **Strict JSON:** duplicate-key detection depended on Newtonsoft message text. It is now structural.
2. **Strict JSON:** `JToken.Parse` silently converted ISO timestamps into Date tokens. Parsing now goes through one configured reader.
3. **Drag timing:** a loss of exactly 200 ms didn't freeze, because of floating-point subtraction. Thresholds now have a 1 µs tolerance.
4. **Drag preview lag:** Meta's `Grabbable` raises Move *before* moving the transform, so the preview lagged one event and a release could commit a stale pose. The preview is now computed in `LateUpdate` and recomputed at Unselect, after `ForceMove`.
5. **Rig wiring:** the code-generated scene lacked the camera-rig, player-origin, eye and camera references Meta's sample sets. This caused 24 `NullReferenceException`s and SDK assertions that the test framework had not counted. The rig is now wired, and the simulator smoke test fails on any Error, Assert or Exception log.
6. **OpenXR settings:** 66 orphaned OpenXR settings sub-assets made every Android build dirty the tree. They are removed, and verification fails if any return.
7. **Weak tests:** three tests sent `Select` without `Hover`, so no drag started and two of them passed vacuously. They now hover first and assert that a drag is active.

## Not done in this phase

Evaluator, replay, explanation, comparison (Phase 2); persistence (Phase 4); voice (blocked); sofa selection; locomotion removal; comfort sizing.

## Next phase (Phase 2) when gates allow

- **B:** evaluator against `expectations.json`.
- **A:** deterministic playback.
- **D:** explanation, stale-result and comparison UI.
- **C:** stays blocked until the provider decision.
