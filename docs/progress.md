# Tryscape progress

Last updated: 5 November 2026 · Integration commit `05ac0f6` · Authoritative gate state: [`docs/progress/phase-state.json`](progress/phase-state.json) (`python3 scripts/check_release.py --phase 0` / `1` / `2`).

| Phase | Status | Open gates |
| --- | --- | --- |
| 0 / M0 baseline | **Technical baseline complete; phase blocked** on owner/human items | second-contributor repeat, worker-isolation decision, model/provider + deployment (deferred by owner) |
| 1 hand-driven slice | **Implemented and automated-verified; phase blocked** on manual/device checks | manual simulator walkthrough, Quest lost-tracking check (AC12) |
| 2 evaluation, replay, explanation | **Evaluator, replay, explanation and stale result implemented and automated-verified; phase blocked** | manual simulator walkthrough, backend intent extraction (provider decision), DevAgent token regeneration |

Moving Day is still the **provisional** reference pack. Nothing here claims a headset run or voice creation. Route results are "in this model" (floor footprint only), never a physical guarantee.

## Implemented and verified

| Item | Evidence |
| --- | --- |
| Git repo, ignore/attributes, contracts/v1, contract checker (141 checks) | `uv run --locked scripts/check_contracts.py` |
| Backend baseline: health, anonymous sessions, error envelope, body limits, R0 migration | 78 pytest tests on PostgreSQL 18; ruff/format/mypy clean |
| Unity project `app-unity/` (6000.6.8f1, URP, OpenXR 1.17.1, Meta 207.0.1) created and configured headlessly | `ProjectConfigurator.VerifyBatch`: 0 problems |
| Hand-interaction sample + Tryscape rig on Meta XR Simulator 207.1 (tracked simulated hands, enabled interactors, zero error/assert/exception logs) | `scripts/run_xrsim_smoke.sh`: 2/2 |
| Android ARM64 IL2CPP development APK from a clean commit; tree stays clean after build | `artifacts/phase-1/apk-manifest.json` (commit `135f1fa`, sha256 `2ac67bfc…`) |
| Clean clone, no `Library/`: full import + APK build | clone of `c90a36c`, 10m57s, exit 0 |
| **Tryscape.Core**: immutable SceneSpec, catalog, doubled-mm footprints, semantic validator, placement rules, `SceneSession` (atomic commands, expected revision, undo/redo/reset as new revisions, bounded duplicate cache) | EditMode 82/82 |
| **Infrastructure**: strict SceneSpec reader (duplicate keys, quotes, comments, NaN, size, no date coercion) | fixture parity tests |
| **Application**: `EditingSession`, `DragController` (one owner, 200 ms freeze, 1000 ms cancel, no commit on reacquisition), model mapping | EditMode |
| **Moving Day pack**: catalog (identical to contract fixture) and template; fixed internal coordinates for 5–8 m rooms | EditMode (F01/F02 reproduced) |
| **Unity slice**: hands-only creation (width/depth/console/route), Interaction SDK grab + ray grab of the console with floor preview (outline, stripes, reason text), invalid drop returns, rotate quarter turn, route switch, undo/redo/reset, tabletop zoom/recenter, focus-loss cancel | PlayMode Experience 12/12 via the real `Grabbable` |
| **Route evaluator** (`Tryscape.Core.Evaluation`, v1.0.0): exact doubled-mm Minkowski checks, closed blocker contact, closed wall margin, earliest-violation tie-breaks; reproduces all 11 shared expectations; canonical scene/evidence SHA-256 matching a second (Python) serializer | EditMode 122/122; `check_contracts.py` 148; mutation check caught by 4 tests |
| **Rehearsal slice**: Rehearse / Replay / Details, result card (observed / reason / action + scope), OUT OF DATE after edits, 20 Hz logical replay stopping at the modelled contact, contact + blocker highlight | PlayMode Experience 16/16 |
| `unity_batch.sh`: fresh `--artifact` evidence, `<step>.status.json`, `clean_success` vs `success_with_shutdown_abort` reported separately from Unity's exit code | used for every Phase 2 Unity step |
| Security: Meta DevAgent access token stripped from builds (0 hits in APK, 0 in any git object). **Not yet regenerated.** Dev APKs carry this Mac's LAN IP in `boot.config` (Unity's standard dev-build player connection; not a credential) | APK scan, `git cat-file --batch-all-objects` scan |

## Blocked / not run (needs a person)

| Item | Exact action |
| --- | --- |
| **Model/provider + deployment** (deferred by owner) | Choose ASR/intent provider(s), hosting and spending cap. Until then: no transcription/proposal job routes, no voice capture, and Phase 1 workstream C (recording upload/ASR jobs) is not started. |
| **Manual simulator walkthrough** (P1) | See procedure below; record pass/fail and any issue. |
| **Quest device checks** (AC12 lost tracking; install/launch of the APK) | Connect a Quest 3/3S in developer mode. The APK at `artifacts/builds/tryscape-dev.apk` has never been installed. |
| **Second contributor** repeat (S03.1) | Another team member: clone, open with Unity 6000.6.8f1, apply the two machine fixes in `docs/environment/m0-environment.md`, run the commands in `README.md`. |
| **Four-agent execution** (phases.md) | Decide whether to use parallel workers. Everything so far ran sequentially in one agent. |
| **Regenerate the DevAgent token** (P2) | Unity Editor → Meta XR AI Tools (Agent Bridge) settings → Remote Agent Server → Access Token → **Regenerate** → confirm. Removing it from git history did not revoke it. |

### Manual simulator walkthrough (P1-manual-simulator-walkthrough)

1. Open `app-unity` in Unity 6000.6.8f1. If prompted, run *Tryscape → Import Interaction SDK Example Scenes*. Open `Assets/Tryscape/Scenes/Bootstrap.unity`.
2. In Meta's XR Simulator menu, activate the simulator, then press Play.
3. Use simulated hands only. Press **Create with hands**, try **+0.5** width, then **Create example room**.
4. Pinch-grab or ray-grab the console. Drag it south (towards you) and release on clear floor: it should snap, and status should show a new revision.
5. Drag it onto the sofa and release: red striped outline with "Overlaps the sofa's starting position"; the console returns.
6. **Rotate**, **Switch route**, **Undo** ×2, **Redo**, **Reset**: revisions only increase; Reset restores the original layout.
7. **Larger/Smaller/Recenter**: presentation changes; the room's dimension label is unchanged.
8. **Reset**, then **Rehearse** (Phase 2): the sofa moves along the Direct route and stops short of the console. A red outline and pin mark the contact, and the console is outlined. The card says "Blocked in this model", names the console table, and suggests moving it or trying the Northern approach. Try **Pause/Resume/Replay** and **Details**.
9. Drag the console south and release: the card shows **OUT OF DATE** and the button reads **Rehearse again**. Rehearse: "Clear in this model", and the sofa reaches the destination. Move the console north (about 4 m), **Switch route**, Rehearse: the Northern approach is blocked.
10. Record the outcome in `phase-state.json` (or report it), including anything awkward about reach, text size, result-card truncation or panel placement.

## Known limitations (deliberate, not bugs)

- No Attempt A/B comparison UI yet (deferred to Phase 4 with persistence); no movers or route direction markers; no seek control in the UI.
- No local save/load (Phase 4). "Open saved" and "Speak a situation" explain that they are unavailable.
- The sofa is not hand-selectable (its label is always shown). Locomotion and teleport interactors from Meta's comprehensive rig are still present; consider disabling them for seated use.
- Greybox art; legacy `TextMesh`/uGUI text; panel sizes are untested for comfort.
- Unity 6.6 is not an LTS release (decision D11).

## Next smallest task

Human: steps 8–9 of the walkthrough above, then regenerate the DevAgent token. Agent (when directed): Phase 4 Attempt A/B comparison on the existing immutable `RehearsalRun` snapshots, with local atomic save/reopen.
