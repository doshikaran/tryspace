# Tryscape

**Understand the situation. Rehearse your options. Act prepared.**

Tryscape turns a described situation into an interactive spatial rehearsal. Voice creates the initial environment through a Python AI backend; after that, hands do the editing, rehearsal, replay and comparison. The reference scenario, Moving Day, is **provisional**.

> **Status:** M0 technical baseline and the Phase 1 hands-only editing slice are implemented and automated-verified. Voice creation (provider decision deferred), route evaluation, saving and on-headset validation are **not** done. See [docs/progress.md](docs/progress.md).

## Layout

| Path | Content | State |
| --- | --- | --- |
| `docs/` | Specs ([technical](docs/technical-spec.md), [demo scope](docs/demo-scope.md)), progress, environment records | Present |
| `contracts/v1/` | JSON Schemas and shared fixtures | Present |
| `scripts/` | `check_contracts.py`, `check_release.py` | Present |
| `services/api/` | R0 creation backend (FastAPI/PostgreSQL) | Baseline only |
| `app-unity/` | Unity 6000.6.8f1 client (URP, OpenXR 1.17.1, Meta XR 207.0.1) | Phase 1 slice |
| `artifacts/` | Local logs and builds (git-ignored) | Generated |

## Verified commands (macOS, Apple Silicon)

Prerequisites: Git, [uv](https://docs.astral.sh/uv/), Python 3.12 (uv can provide it), PostgreSQL 18 reachable locally.

```bash
uv run --locked scripts/check_contracts.py      # contracts + fixtures + reference expectations
python3 scripts/check_release.py --phase 0      # gate report; exits 1 while gates are open

cd services/api                                  # backend; see services/api/README.md
uv sync --frozen && uv run ruff check . && uv run ruff format --check . && uv run mypy src && uv run pytest -q
```

### Unity (verified on macOS Apple Silicon; see `docs/environment/m0-environment.md` for machine fixes)

```bash
export TRYSCAPE_UNITY_EDITOR=/Applications/Unity/Hub/Editor/6000.6.8f1/Unity.app/Contents/MacOS/Unity
export TRYSCAPE_ANDROID_NDK_ROOT=/Applications/Unity/Hub/Editor/6000.6.8f1/PlaybackEngines/AndroidPlayer/SDK/ndk/27.2.12479018

# configuration check (exit 1 lists mismatches); regenerate the bootstrap scene and prefabs
scripts/unity_batch.sh verify-config "Verify complete: 0 problem" -- -batchmode -quit -projectPath "$PWD/app-unity" -executeMethod Tryscape.Editor.ProjectConfigurator.VerifyBatch
scripts/unity_batch.sh scene-build "Bootstrap scene written" -- -batchmode -quit -projectPath "$PWD/app-unity" -executeMethod Tryscape.Editor.SceneBuilder.BuildBootstrapBatch

# tests: judged from the result XML, never from log text
scripts/unity_batch.sh editmode "Saving results to" -- -batchmode -projectPath "$PWD/app-unity" -runTests -testPlatform EditMode -testResults "$PWD/artifacts/unity/editmode.xml"
python3 scripts/check_test_results.py artifacts/unity/editmode.xml --min-tests 82
scripts/unity_batch.sh playmode "Saving results to" -- -batchmode -projectPath "$PWD/app-unity" -runTests -testPlatform PlayMode -testCategory Experience -testResults "$PWD/artifacts/unity/playmode.xml"
python3 scripts/check_test_results.py artifacts/unity/playmode.xml --min-tests 12 --no-skips
scripts/run_xrsim_smoke.sh          # Meta XR Simulator runtime; needs the Interaction SDK example scenes imported

# Android ARM64 development APK + manifest (artifacts/builds/)
scripts/unity_batch.sh android "Build result=Succeeded" -- -batchmode -quit -projectPath "$PWD/app-unity" -buildTarget Android -executeMethod Tryscape.Editor.BuildCli.BuildAndroid
```

Close any Unity Editor that has `app-unity` open before running batch commands.
