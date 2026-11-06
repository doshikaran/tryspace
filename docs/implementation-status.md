# Implementation status (R0 competition release)

Scope: R0 per technical-spec S01/S23 and demo-scope D11. Scenario: **Moving Day pack 1.0.0, provisional** (CLAUDE.md; demo-scope D01); no other scenario is implemented or implied. R1 accounts/sync and R2 production gates are out of scope.

Legend:
- **done**: implemented and integrated, with automated checks.
- **partial**: works but is missing a spec element.
- **missing**: not implemented.
- **blocked**: needs an external decision, credential or hardware.

The Notes column names only checks that were actually run. Device, usability and human-speech checks are in `docs/progress.md`; none of them has been performed. **Nothing here is release-ready**: the S23.1 gates still require a deployment, a model-assisted interpreter and Quest evidence.

Initial assessment at `ec990bb`; updated after the implementation pass, which ran EditMode 188/188, PlayMode 47/47, backend 171, contracts 317, XR simulator 2/2 and VerifyBatch with 0 problems.

| # | Requirement (spec ref) | Status | Implementation location | Notes |
| --- | --- | --- | --- | --- |
| 1 | Pinned Unity 6000.6.8f1, Meta XR 207.0.1, OpenXR 1.17.1; Android ARM64 IL2CPP build (S03) | done | `app-unity/ProjectSettings`, `Packages/`, `Editor/BuildCli.cs`, `Editor/ProjectConfigurator.cs`, `scripts/unity_batch.sh` | Unity 6.6 is not LTS (D11 decision). No release keystore. |
| 2 | Canonical contracts, schemas, fixtures (S06, S09.1) | done | `contracts/v1`, `scripts/check_contracts.py` | 317 checks. |
| 3 | Validation, catalog, staged scene construction (S06, S05.2) | done | `Core/Validation`, `Core/Model/Catalog.cs`, `Content/MovingDay`, `Unity/Presentation/ScenePresenter.cs` | Greybox art (D07 permits it). |
| 4 | Commands, revisions, undo/redo/reset (S07) | done | `Core/Commands`, `Application/Editing` | |
| 5 | Exact evaluator; evidence-based explanations (S08/S09) | done | `Core/Evaluation`, `Application/Rehearsal/Explanation.cs` | |
| 6 | Hand selection, drag preview, quarter turns, menus (S11, D05) | done | `Unity/Presentation/BlockerDragAdapter.cs`, `Unity/Experience/PanelController.cs` | Real-hand usability unverified. |
| 7 | Tabletop zoom, recentre **and orientation**, saved separately from geometry (S11) | done | `Unity/Experience/TabletopController.cs` (`RotateView`, `SetYaw`, `ZoomStep`); `presentation` in project schema 1.1.0 | PlayMode: scene and evidence hashes unchanged by view turns. |
| 8 | Voice capture, transcription job, proposal, review, confirmation (S10, S13) | done (local service) | `Application/Creation`, `Unity/Creation`, `services/api` | whisper.cpp on the developer Mac only; human speech untested. |
| 9 | Model-assisted intent extraction (S10.2, AC02) | **blocked** | `services/api/src/tryscape_api/creation/intent.py` (`IntentExtractor` interface) | Provider/budget decision (B-provider-budget). `deterministic_rules` is labelled non-AI everywhere. |
| 10 | Judge-reachable HTTPS deployment (S13, AC29) | **blocked** | `docs/operations.md` (checklist) | Hosting/budget decision. Release builds have no endpoint. |
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
| 21 | Pause replay on hand-tracking loss (S05.3) | missing | — | Needs the Interaction SDK tracking signal wired in, and a device to verify. |
| 22 | First-run onboarding / scope notice; loading, empty, error, offline states | done | `TryscapeApp.OnboardingText` (PlayerPrefs flag); voice, creation and projects state messages | A guided tour is optional (D11) and not built. |
| 23 | Backend global queue ceiling, kill switch, per-IP session issuance limit (S13.2/S13.3) | done | `jobs._enqueue` (`CREATION_ENABLED`, `MAX_ACTIVE_JOBS`), `limits.py`, `main.py` | The IP window is per process (single host); edge limits are a deployment item. |
| 24 | Permissions only on feature entry; microphone denial keeps the app usable (S11) | done | `Unity/Creation/UnityMicrophone.cs`, `VoiceRecorder` | Device check outstanding. |
| 25 | Dependency and licence records | done | `docs/dependencies.md` | Legal review of the Oculus SDK and Unity terms still owed. |
| 26 | Service operations documentation (S21) | done | `docs/operations.md` | Deployment steps documented but blocked. |
| 27 | Passthrough/MR anchoring (S11) | deferred (optional) | — | S11: optional; not needed for the synthetic scene. |
| 28 | Release signing, store channel, submission video (AC26–28) | blocked | — | Needs a keystore, the Meta release channel and owner action. |
| 29 | Device performance, comfort and usability evidence (AC22, AC24, AC25) | blocked | — | Needs a Quest 3/3S and test participants; QA phase. |
