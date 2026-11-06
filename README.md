# Tryscape

**Understand the situation. Rehearse your options. Act prepared.**

Tryscape turns a described situation into an interactive spatial rehearsal in mixed reality. Voice or hands create the initial environment, which sits on your real table in passthrough; after that, hands (or gaze and pinch) do the editing, rehearsal, replay and comparison. **Everything runs on the headset; there is no server** (decision D12). The reference scenario, Moving Day, is **provisional**.

> **Status:** all R0 development is implemented and automated-verified on a developer Mac (Editor, simulator, Android builds): hand and voice creation, the exact route evaluator with replay and explanations, Attempt A/B comparison, local projects with recovery, passthrough with MRUK table placement, gaze with hand-ray fallback, hand-loss replay pause, and on-device speech (whisper.cpp). Interpretation is a **rule-based parser** (`deterministic_rules`), not an AI model. **Nothing has run on a Quest yet**, and voice is tested only with synthetic speech. Not release-ready; see [docs/progress.md](docs/progress.md) and [docs/implementation-status.md](docs/implementation-status.md).

## Layout

| Path | Content | State |
| --- | --- | --- |
| `docs/` | Specs ([technical](docs/technical-spec.md), [demo scope](docs/demo-scope.md)), progress, environment records | Present |
| `contracts/v1/` | JSON Schemas and shared fixtures | Present |
| `scripts/` | `check_contracts.py`, `check_release.py`, `unity_batch.sh`, `asr_spike.py`, `scan_build_secrets.py`, `check_devagent_rotation.py` | Present |
| `services/api/` | R0 creation backend (FastAPI/PostgreSQL): sessions, scene-proposal jobs, worker | Local only; not deployed |
| `app-unity/` | Unity 6000.6.8f1 client (URP, OpenXR 1.17.1, Meta XR 207.0.1) | Phase 3 slice |
| `artifacts/` | Local logs and builds (git-ignored) | Generated |

## Verified commands (macOS, Apple Silicon)

Prerequisites: Git, [uv](https://docs.astral.sh/uv/), Python 3.12 (uv can provide it), PostgreSQL 18 reachable locally.

```bash
uv run --locked scripts/check_contracts.py      # contracts + fixtures + reference expectations + canonical hashes
python3 scripts/check_release.py --phase 0      # gate report; exits 1 while gates are open

cd services/api                                  # backend; see services/api/README.md
uv sync --frozen && uv run ruff check . && uv run ruff format --check . && uv run mypy src && uv run pytest -q

# Local service (developer only; two processes, PostgreSQL running)
export TRYSCAPE_ENV=development TRYSCAPE_DATABASE_URL=postgresql+psycopg://localhost/tryscape_dev
createdb tryscape_dev; uv run alembic upgrade head          # once
uv run uvicorn tryscape_api.main:app --host 127.0.0.1 --port 8000
uv run python -m tryscape_api.worker                         # second terminal
# voice: give the worker whisper.cpp (built outside the repo; see docs/environment/m0-environment.md)
#   TRYSCAPE_ASR_BINARY=~/.cache/tryscape/whisper.cpp/build/bin/whisper-cli TRYSCAPE_ASR_MODEL=~/.cache/tryscape/models/ggml-base.en.bin
#   (worker and API must share TRYSCAPE_AUDIO_DIR if set)
adb reverse tcp:8000 tcp:8000                                # Quest dev build reaches the Mac without a LAN address
```

### Unity (verified on macOS Apple Silicon; see `docs/environment/m0-environment.md` for machine fixes)

```bash
export TRYSCAPE_UNITY_EDITOR=/Applications/Unity/Hub/Editor/6000.6.8f1/Unity.app/Contents/MacOS/Unity
export TRYSCAPE_ANDROID_NDK_ROOT=/Applications/Unity/Hub/Editor/6000.6.8f1/PlaybackEngines/AndroidPlayer/SDK/ndk/27.2.12479018

# On-device speech (once per clone): the native plugin binaries are committed, the 148 MB model is not (GitHub limit).
scripts/fetch_asr_model.sh                 # installs + SHA-256-verifies app-unity/Assets/StreamingAssets/asr/ggml-base.en.bin
scripts/build_whisper_plugin.sh all        # only after changing native/whisper_shim or the whisper.cpp pin
(cd services/api && uv run python -m tryscape_api.creation.parity --write)   # only after an intentional interpreter change

# Every step: --artifact files are deleted first and must be fresh afterwards; artifacts/unity/<step>.status.json
# records Unity's exit code and clean_success vs success_with_shutdown_abort (add --strict to exit 3 on the latter).
# configuration check (exit 1 lists mismatches); regenerate the bootstrap scene and prefabs
scripts/unity_batch.sh verify-config "Verify complete: 0 problem" --artifact "$PWD/artifacts/unity/verify-config.json" -- -batchmode -quit -projectPath "$PWD/app-unity" -executeMethod Tryscape.Editor.ProjectConfigurator.VerifyBatch
scripts/unity_batch.sh scene-build "Bootstrap scene written" -- -batchmode -quit -projectPath "$PWD/app-unity" -executeMethod Tryscape.Editor.SceneBuilder.BuildBootstrapBatch

# tests: judged from the result XML, never from log text
scripts/unity_batch.sh editmode "Saving results to" --artifact "$PWD/artifacts/unity/editmode.xml" -- -batchmode -projectPath "$PWD/app-unity" -runTests -testPlatform EditMode -testResults "$PWD/artifacts/unity/editmode.xml"
python3 scripts/check_test_results.py artifacts/unity/editmode.xml --min-tests 186 --no-skips
scripts/unity_batch.sh playmode "Saving results to" --artifact "$PWD/artifacts/unity/playmode.xml" -- -batchmode -projectPath "$PWD/app-unity" -runTests -testPlatform PlayMode -testCategory Experience -testResults "$PWD/artifacts/unity/playmode.xml"
python3 scripts/check_test_results.py artifacts/unity/playmode.xml --min-tests 43 --no-skips
# with the local service running: live round trip through Unity's HTTP adapter
scripts/unity_batch.sh playmode-backend "Saving results to" --artifact "$PWD/artifacts/unity/playmode-backend.xml" -- -batchmode -projectPath "$PWD/app-unity" -runTests -testPlatform PlayMode -testCategory Backend -testResults "$PWD/artifacts/unity/playmode-backend.xml"
scripts/run_xrsim_smoke.sh          # Meta XR Simulator runtime; needs the Interaction SDK example scenes imported

# Android ARM64 development APK + manifest (artifacts/builds/)
scripts/unity_batch.sh android "Build result=Succeeded" --artifact "$PWD/artifacts/builds/tryscape-dev.apk" --artifact "$PWD/artifacts/builds/tryscape-dev.apk.manifest.json" -- -batchmode -quit -projectPath "$PWD/app-unity" -buildTarget Android -executeMethod Tryscape.Editor.BuildCli.BuildAndroid
# non-development APK (debug keystore): add -tryscapeRelease and use tryscape-release.apk paths; then
python3 scripts/scan_build_secrets.py artifacts/builds/tryscape-dev.apk artifacts/builds/tryscape-release.apk   # counts only
```

Close any Unity Editor that has `app-unity` open before running batch commands.
