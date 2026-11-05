# Tryscape progress

Last updated: 5 November 2026 · Integration commit `bfda97f` · Authoritative gate state: [`docs/progress/phase-state.json`](progress/phase-state.json) (`python3 scripts/check_release.py --phase 0` … `3`).

| Phase | Status | Open gates |
| --- | --- | --- |
| 0 / M0 baseline | **Technical baseline complete; phase blocked** on owner/human items | second-contributor repeat, worker-isolation decision, model/provider + deployment (deferred by owner) |
| 1 hand-driven slice | **Implemented and automated-verified; phase blocked** on manual/device checks | manual simulator walkthrough, Quest lost-tracking check (AC12) |
| 2 evaluation, replay, explanation | **Evaluator, replay, explanation and stale result implemented and automated-verified; phase blocked** | manual simulator walkthrough, backend intent extraction (provider decision), DevAgent token regeneration |
| 3 creation flow | **Speech or typed text → transcription (whisper.cpp) → proposal → hand review → staged build → rehearsal implemented and automated-verified on this Mac against a local service, including real microphone capture (synthetic speech replayed); phase blocked** | model-assisted interpreter and deployment (provider decision), voice benchmark on a deployment, human-speech accuracy, DevAgent token, manual and Quest checks |

Moving Day is still the **provisional** reference pack. Nothing here claims a headset run or voice creation. Phase 3 creation accepts **speech** (whisper.cpp on a **local developer service, not a judge-accessible deployment**) or typed development input, and interprets it with a **deterministic rule-based interpreter** (`deterministic_rules`, not an AI model); see [`phase-3-summary.md`](progress/phase-3-summary.md) and [`phase-3b-voice-summary.md`](progress/phase-3b-voice-summary.md). Route results are "in this model" (floor footprint only), never a physical guarantee.

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
| **Creation pipeline (Phase 3)**:<ul><li>backend scene-proposal jobs (session, consent, idempotency, limits, deadlines, cancellation) with a separate SKIP LOCKED worker;</li><li>deterministic interpreter and assembler;</li><li>Unity coordinator, which discards late, superseded or stale results;</li><li>pack check that only accepts the template's own scene;</li><li>review, correct and clarify UI; staged commit.</li></ul> | backend 155 pytest; contracts 300; EditMode 149/149; PlayMode 29/29; live local backend 2/2; 4 mutation checks |
| **Voice creation (Phase 3b)**:<ul><li>`POST /v1/transcription-jobs`: raw WAV validated before storage; consent, idempotency, ownership, deadlines, cancel; audio in an owner-only temp store, deleted at terminal states;</li><li>Unity `VoiceRecorder` and `UnityMicrophone` (permission, 15 s limit, device loss, focus-loss discard, silence);</li><li>one 30 s budget for transcription plus proposal;</li><li>Describe with voice: notice, Allow, elapsed time, Stop and Cancel; review shows "Spoken input" with Edit transcript.</li></ul> | backend 169; EditMode 170/170; PlayMode 41/41; live spoken requests 1/1 (3 requests); real Mac microphone loopback 1/1; mutation checks 3+2 |
| **ASR spike**: whisper.cpp v1.9.5 + tiny.en/base.en on this Mac with **synthetic** (macOS `say`) speech: 196/198 and 197/198 utterances with correct parameters. Human speech and Quest not run. | `artifacts/phase-3/asr-spike-mac-tts.json` |
| Security: Meta DevAgent access token stripped from builds (0 hits in APK, 0 in any git object). **Not yet regenerated.** Dev APKs carry this Mac's LAN IP in `boot.config` (Unity's standard dev-build player connection; not a credential) | APK scan, `git cat-file --batch-all-objects` scan |

## Blocked / not run (needs a person)

| Item | Exact action |
| --- | --- |
| **Model/provider + deployment** (deferred by owner) | Choose ASR/intent provider(s), hosting and spending cap. Until then: no transcription/proposal job routes, no voice capture, and Phase 1 workstream C (recording upload/ASR jobs) is not started. |
| **Manual simulator walkthrough** (P1) | See procedure below; record pass/fail and any issue. |
| **Quest device checks** (AC12 lost tracking; install/launch of the APK) | Connect a Quest 3/3S in developer mode. The APK at `artifacts/builds/tryscape-dev.apk` has never been installed. |
| **Second contributor** repeat (S03.1) | Another team member: clone, open with Unity 6000.6.8f1, apply the two machine fixes in `docs/environment/m0-environment.md`, run the commands in `README.md`. |
| **Four-agent execution** (phases.md) | Decide whether to use parallel workers. Everything so far ran sequentially in one agent. |
| **Regenerate the DevAgent token** (P2/P3) | Unity Editor → Meta XR AI Tools (Agent Bridge) settings → Remote Agent Server → Access Token → **Regenerate** → confirm → **Start** the server in the same panel. Then run `python3 scripts/check_devagent_rotation.py` (prints PASS/FAIL only; it expects the old token to get 401 and the new one 200). Removing it from git history did not revoke it. |
| **Human speech for the ASR spike** (P3) | Record the 33 single-line cases in `contracts/v1/fixtures/interpretation/moving_day_cases.json` (QuickTime or Voice Memos), convert each with `afconvert -f WAVE -d LEI16@16000 -c 1 in.m4a <case_id>.wav` into one folder outside the repo, then run `uv run --project services/api python scripts/asr_spike.py --source recordings --recordings <folder> --out artifacts/phase-3/asr-spike-mac-human.json`. |
| **Phase 3 walkthrough** | Steps 11–13 below. |

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
11. **Phase 3, typed creation.** In a terminal, from `services/api`, start the API and worker (README "Local service"), with PostgreSQL running. In the headset panel: **Menu → Describe (typed, dev input) → Example: 6 x 5 m → Interpret**. The review card should say "Typed development input (not voice)", "rule-based, not an AI model", and show Room 6.0 m x 5.0 m, Console in the way, Direct, and "Check:" lines. Press **Adjust with hands**, change the width, press **Review**, then **Confirm and build**, then **Rehearse**.
12. **No size given → Interpret**: choose **Use 6 m by 5 m example** (resubmitted) or **Enter dimensions** (hand editor). **Unsupported request → Interpret**: reasons shown, and no build button.
13. Stop the API, then Interpret again: you should get "not reachable … Create with hands" and the existing scene should be unchanged. Note whether the review card text fits.
14. **Phase 3b, voice** (service started with `TRYSCAPE_ASR_BINARY`/`TRYSCAPE_ASR_MODEL`, see README): **Describe with voice → Allow and record**, allow the OS microphone prompt, then say "Prepare a six by five metre living room for a sofa delivery. There is a console table in the way." Press **Stop**. The card should show the elapsed time, then Transcribing, then a review with "Spoken input, transcribed by whisper_cpp_base_en". Try **Edit transcript**, **Cancel** while transcribing, and denying the microphone. Report recognition errors.

## Manual and device checklist (Phase 1–3)

| Check | Status | Missing prerequisite |
| --- | --- | --- |
| Phase 1/2 simulator walkthrough (steps 1–10) | not run | a person at the Editor with the XR Simulator |
| Result-card clipping and readability | not run | the same; the 150 px card can truncate |
| Phase 3 walkthrough (steps 11–13) | not run | the same, plus the local API and worker |
| Real hand-interaction usability | not run | headset or simulator with a person; automated button presses do not count |
| Quest frame rate, tracking, comfort, lifecycle (pause/resume, focus loss) | not run | Quest 3/3S in developer mode |
| IL2CPP/device canonical hash parity | not run | Quest; compare EditMode hashes with an on-device run |
| Microphone capture and transcription on device (RECORD_AUDIO prompt, deny/revoke, quality, frame time while recording) | not run | Quest; `adb reverse tcp:8000 tcp:8000` to the Mac service |
| Voice walkthrough with a person on the Mac (step 14) | not run | a person speaking; macOS microphone permission for Unity |
| Human-speech ASR accuracy (Mac) | not run | recordings (procedure above); every voice run so far used synthetic `say` speech |

## Known limitations (deliberate, not bugs)

- No Attempt A/B comparison UI yet (deferred to Phase 4 with persistence); no movers or route direction markers; no seek control in the UI.
- No local save/load (Phase 4). "Open saved" and "Speak a situation" explain that they are unavailable. Typed description is development input, and release builds have no creation endpoint.
- The sofa is not hand-selectable (its label is always shown). Locomotion and teleport interactors from Meta's comprehensive rig are still present; consider disabling them for seated use.
- Greybox art; legacy `TextMesh`/uGUI text; panel sizes are untested for comfort.
- Unity 6.6 is not an LTS release (decision D11).

## Next smallest task

Human:
1. Record 5–10 human utterances (procedure above) and run `scripts/asr_spike.py --source recordings`.
2. Install the development APK on a Quest, run `adb reverse tcp:8000 tcp:8000`, and do one spoken creation (step 14).
3. Regenerate the DevAgent token.
4. Make the provider, hosting and budget decision.

Agent (when directed): score the human recordings, then measure frame time during capture on device.
