# Implementation status (R0 competition release)

Scope: R0 per technical-spec S01/S23 and demo-scope D11. Scenario: **Moving Day pack 1.0.0, provisional** (CLAUDE.md; demo-scope D01); no other scenario is implemented or implied. R1 accounts/sync and R2 production gates are out of scope.

Legend:
- **done**: implemented and integrated, with automated checks.
- **partial**: works but is missing a spec element.
- **missing**: not implemented.
- **blocked**: needs an external decision, credential or hardware.

The Notes column names only checks that were actually run. Device, usability and human-speech checks are in `docs/progress.md`; none of them has been performed. **Nothing here is release-ready**: the S23.1 gates still require Quest evidence, human-speech results, release signing and submission. Revision 2.2 (decision D12, 6 November 2026) removed the hosted server from R0, kept voice by running it on the headset, and made passthrough with MRUK surface placement and gaze required.

Initial assessment at `ec990bb`. Latest update after the D12 pass, which ran EditMode 278/278, PlayMode (Experience) 56/56, backend 172, contracts 317, VerifyBatch with 0 problems, plus development and release Android builds (release APK secret scan: 0 hits).

| # | Requirement (spec ref) | Status | Implementation location | Notes |
| --- | --- | --- | --- | --- |
| 1 | Pinned Unity 6000.6.8f1, Meta XR 207.0.1, OpenXR 1.17.1; Android ARM64 IL2CPP build (S03) | done | `app-unity/ProjectSettings`, `Packages/`, `Editor/BuildCli.cs`, `Editor/ProjectConfigurator.cs`, `scripts/unity_batch.sh` | Unity 6.6 is not LTS (D11 decision). No release keystore. |
| 2 | Canonical contracts, schemas, fixtures (S06, S09.1) | done | `contracts/v1`, `scripts/check_contracts.py` | 317 checks. |
| 3 | Validation, catalog, staged scene construction (S06, S05.2) | done | `Core/Validation`, `Core/Model/Catalog.cs`, `Content/MovingDay`, `Unity/Presentation/ScenePresenter.cs` | Greybox art (D07 permits it). |
| 4 | Commands, revisions, undo/redo/reset (S07) | done | `Core/Commands`, `Application/Editing` | |
| 5 | Exact evaluator; evidence-based explanations (S08/S09) | done | `Core/Evaluation`, `Application/Rehearsal/Explanation.cs` | |
| 6 | Hand selection, drag preview, quarter turns, menus (S11, D05) | done | `Unity/Presentation/BlockerDragAdapter.cs`, `Unity/Experience/PanelController.cs` | Real-hand usability unverified. |
| 7 | Tabletop zoom, recentre **and orientation**, saved separately from geometry (S11) | done | `Unity/Experience/TabletopController.cs` (`RotateView`, `SetYaw`, `ZoomStep`); `presentation` in project schema 1.1.0 | PlayMode: scene and evidence hashes unchanged by view turns. |
| 8 | Voice capture, transcription, proposal, review, confirmation (S10, S13) | done (on device) | `Unity/Creation/OnDeviceTranscriptionProvider.cs` (whisper.cpp through `native/whisper_shim`, base.en model bundled in the APK and SHA-256 verified), `Content/MovingDay/Scripts/MovingDayLocalProposals.cs`, `Application/Creation` | PlayMode end-to-end with **synthetic** speech on the Mac Editor plugin. Human speech, Quest latency and memory untested. The optional Python service is still tested. |
| 9 | Intent extraction (S10.2, AC02) | done (rule-based) | `Content/MovingDay/Scripts/MovingDayInterpreter.cs`: C# port of `deterministic_rules` | Matches the Python implementation exactly on all 38 shared cases (EditMode parity) and the hand-written expectations. It is **not** an AI model, and is labelled `deterministic_rules`. A model-assisted interpreter is R1 (D12). |
| 10 | Judge-reachable HTTPS deployment (S13, AC29) | not applicable to R0 (D12) | `docs/operations.md` (checklist kept for R1) | R0 has no server; the release APK has no endpoint and makes no network calls. |
| 11 | Replay, pause/resume, comparison with immutable attempts (S08.3, S09) | done | `Core/Evaluation/PlaybackTimeline.cs`, `Application/Rehearsal/Attempts.cs` | |
| 12 | Route direction markers (D07) | done | `ScenePresenter.BuildRoute`: chevrons every 0.5 m | PlayMode chevron count; visual legibility unverified. |
| 13 | Comparison ghosts of changed-object positions (D07, D03.6) | done | `ResultHighlight.ShowGhosts`, `TryscapeApp.InspectAttempt` | Floor outline labelled with the other attempt; PlayMode ghost-count test. |
| 14 | Two schematic movers (D07) | done | `PlaybackController.CreateMovers`; they flank the payload perpendicular to travel | Labelled "Mover (schematic)"; capsules, no animation. PlayMode: never ahead of the payload. |
| 15 | Local save, open, delete; recovery; limits (S12) | done | `Infrastructure/Persistence` | |
| 16 | Schema migrations as explicit functions with fixtures (S12) | done | `SnapshotJson.ReadProject` (project 1.0.0 → 1.1.0); fixtures `persistence/valid_project_v1_0_0_legacy.json` and `valid_project.json` | The original is kept until the next verified save, as `.bak`. EditMode: idempotent, attribution preserved, C# output equals the Python migration. |
| 17 | Persist before suspension; offer resume on restart (S05.3, S12) | done | `TryscapeApp.AutosaveCommitted` (Suspend, OnApplicationQuit), `ResumeLast` | Committed state only, and only when changed. It never auto-resumes movement or the microphone. Device lifecycle unverified. |
| 18 | Settings, Help, Pause and Reload always reachable (S11, D05) | done | Global `Settings` button; Settings section (turn view, reset view, pause/resume replay, reload last save, help, about) | Opening Settings stops recording and interpretation and pauses replay. |
| 19 | Provenance labels `Example environment` / `User-confirmed dimensions` / `Not assessed` at review **and results** (S11) | done | First line of `ResultText`; room label in `ScenePresenter.Render`; review card; scope line | Fixed: the room label used to say "Example environment" even for measured scenes. |
| 20 | Pause replay on focus loss and app pause (S05.3) | done | `PlaybackController.OnApplicationFocus`, `TryscapeApp.Suspend` | |
| 21 | Pause replay on hand-tracking loss (S05.3) | done | `Application/Rehearsal/HandLossPolicy.cs` (0.5 s grace, never auto-resumes), `Unity/Experience/HandTrackingWatcher.cs` wired to the rig's hands | EditMode policy tests; PlayMode with a fake hand source. Device tracking unverified. |
| 22 | First-run onboarding / scope notice; loading, empty, error, offline states | done | `TryscapeApp.OnboardingText` (PlayerPrefs flag); voice, creation and projects state messages | A guided tour is optional (D11) and not built. |
| 23 | Backend global queue ceiling, kill switch, per-IP session issuance limit (S13.2/S13.3) | done | `jobs._enqueue` (`CREATION_ENABLED`, `MAX_ACTIVE_JOBS`), `limits.py`, `main.py` | The IP window is per process (single host); edge limits are a deployment item. |
| 24 | Permissions only on feature entry; microphone denial keeps the app usable (S11) | done | `Unity/Creation/UnityMicrophone.cs`, `VoiceRecorder` | Device check outstanding. |
| 25 | Dependency and licence records | done | `docs/dependencies.md` | Legal review of the Oculus SDK and Unity terms still owed. |
| 26 | Service operations documentation (S21) | done | `docs/operations.md` | Deployment steps documented but blocked. |
| 27 | Passthrough by default with a virtual-room toggle (S11, AC33) | done | `Unity/MixedReality/MixedRealityController.cs`; `SceneBuilder.ConfigurePassthrough` (underlay layer, transparent camera, startup permission prompts off) | PlayMode toggle and remembered preference; scene wiring test. Quest unverified. |
| 28 | Release signing, store channel, submission video (AC26–28) | blocked | — | Needs a keystore, the Meta release channel and owner action. |
| 29 | Device performance, comfort and usability evidence (AC22, AC24, AC25) | blocked | — | Needs a Quest 3/3S and test participants; QA phase. |
| 30 | Real-surface placement with MRUK (S11, AC34) | done | `Application/Placement/SurfacePlacement.cs` (pure ranking: 0.4–1.2 m high, within 1.5 m, at least 0.3 m, facing up); `Unity/MixedReality/SurfaceSources.cs` (MRUK 207.0.1, room loaded on demand); Settings: next surface, set up my space, float | EditMode ranking tests; PlayMode with a fake room (fallback reasons, stale scan discarded, placement presentation-only). Never tested in a real room. |
| 31 | Gaze look-and-pinch with hand-ray fallback (S11, AC35) | done | Interaction SDK v207 gaze quick actions on the panel and console (`ExperienceBuilder`); `Unity/MixedReality/GazeController.cs` (eye gaze where available and permitted, optional head gaze) | Scene wiring test (interactors, interactables, fallback filters). Eye tracking needs a Quest Pro-class headset; untested. |
| 32 | On-device speech engine build and packaging | done | `native/whisper_shim` (C ABI v2 with vocabulary prompt), `scripts/build_whisper_plugin.sh` (pinned whisper.cpp commit, NDK r27c), `scripts/fetch_asr_model.sh` (model not in git, hash-checked), `BuildCli` refuses to build without a verified model | Android arm64 `.so` (2.9 MB) and macOS Editor `.dylib`; release APK 227.8 MB including the 148 MB model. |
