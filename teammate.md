# Tryscape: teammate setup and handoff

Last updated: 8 November 2026

This guide takes you from nothing installed to running and testing Tryscape, and then tells you what is left to do. Do the sections in order.

Everything here has only been verified on a **Mac with Apple Silicon**. Windows and Intel Macs have not been tried, and the on-device voice plugin for the Unity Editor is built only for macOS.

## 1. What Tryscape is

Tryscape is a Meta Quest mixed reality app built for the Meta hackathon. You describe a situation, it builds a small model of it on your real table, and you rehearse it with your hands.

The one demo is **Moving Day**: getting a sofa through a room with a console table in the way.

1. Create a room by voice or by hand.
2. Press **Rehearse**. The sofa follows a route and gets blocked by the console.
3. Move the console with your hands and rehearse again.
4. Compare the two attempts, and save the project.

Rules we have to keep to:

- **Voice stays in the product.** Never remove or hide it. It runs on the headset, with no server.
- **No server in the competition build.** The Python service in `services/api` is an optional development tool.
- **Passthrough, table placement and gaze are required features.**
- **Don't describe the interpreter as AI.** It is rule-based and is labelled `deterministic_rules`.
- **Don't claim anything is tested unless it was actually run.** Simulator results do not count as headset results.
- **Never commit** tokens, keystores, `.env` files or APKs.

## 2. What to download

| What | Version | Where to get it | Required? |
| --- | --- | --- | --- |
| Git | any recent | Xcode Command Line Tools: `xcode-select --install` | Yes |
| Unity Hub | latest | https://unity.com/download | Yes |
| Unity Editor | **exactly 6000.6.8f1** | See below | Yes |
| Meta XR Simulator | **207.1** | Meta's page: https://developers.meta.com/vr/documentation/unity/unity-simulate-xrsim/ | Yes, unless you have a Quest |
| uv (Python tool) | 0.7 or newer | `curl -LsSf https://astral.sh/uv/install.sh \| sh` | For repo checks and the backend |
| PostgreSQL | 18 | `brew install postgresql@18` | Only for the optional backend |
| Meta Quest 3 or 3S in developer mode | n/a | n/a | Needed for the final device checks |

### Install the right Unity version

Do not upgrade Unity or any package. The versions are pinned on purpose.

1. Install Unity Hub and sign in. A free Unity account is enough.
2. Paste this into a browser to install the exact version through Hub: `unityhub://6000.6.8f1/237b6593792b`
3. When Hub asks for modules, tick:
   - **Android Build Support**
   - **OpenJDK** and **Android SDK & NDK Tools** (underneath it)

### Fix missing CMake (only needed for Android builds)

Hub sometimes says CMake is installed when it isn't. If an Android build complains, run this:

```bash
A=/Applications/Unity/Hub/Editor/6000.6.8f1/PlaybackEngines/AndroidPlayer
JAVA_HOME=$A/OpenJDK $A/SDK/cmdline-tools/16.0/bin/sdkmanager --sdk_root=$A/SDK --install "cmake;3.22.1"
```

`docs/environment/m0-environment.md` lists other machine-specific fixes if you hit something strange.

## 3. Get the project

Ask the repo owner to add your GitHub account as a collaborator first.

```bash
git clone https://github.com/doshikaran/tryspace.git tryscape
cd tryscape
scripts/fetch_asr_model.sh
```

The last command downloads the 148 MB speech model, which is too big for GitHub, and checks its SHA-256. Voice will not work and Android builds will refuse to run without it. Run it once per clone.

The compiled speech plugins are already in the repo, so you don't need to build them unless you change `native/whisper_shim`.

### Open it in Unity

1. In Unity Hub choose **Add → Add project from disk** and select the `app-unity` folder inside the clone.
2. Open it with **6000.6.8f1**. The first import can take a long time, so let it finish.
3. If Unity asks to restart or enable new input backends, accept.
4. If a menu asks you to upgrade any package, say no.
5. In the Project window open **Assets → Tryscape → Scenes → Bootstrap**.
6. If the Interaction SDK example scenes are missing, run **Tryscape → Import Interaction SDK Example Scenes** from the top menu.

## 4. Run it in the simulator

1. Open the **Meta XR Simulator** app and turn the runtime toggle at the top **on** (green).
2. In the simulator, set **Left input** and **Right input** to **Hand**.
3. In Unity go to **Edit → Project Settings → XR Plug-in Management → OpenXR**, click the **monitor icon** tab, and confirm **Render Mode** is **Multi-pass**. With Single Pass Instanced, the app draws nothing on a Mac.
4. Press **Play** in Unity and wait about 5 seconds.

You should see the Tryscape panel in the simulator window, with the title and buttons such as **Create with hands** and **Describe with voice**.

Rules while using Unity:

- If Unity asks to save the Bootstrap scene after Play, choose **Don't Save**.
- Only one person should change `Bootstrap.unity` at a time. It is generated by **Tryscape → Rebuild Bootstrap Scene**.
- Close Unity before running any batch command in section 7.

### Simulator keyboard controls

Click once on the simulator's view first, so that it receives your keys.

| Key | Action |
| --- | --- |
| W A S D | Move forward, left, back, right |
| R / F | Move up / down |
| Arrow keys | Look around |
| Q / E | Tilt |
| Space | Reset the simulated hands |
| 2 | Poke gesture |
| 3 | Pinch gesture |
| 4 | Grab gesture |
| 1 | Default hand pose |

### Known simulator problems

| Problem | What we know | State |
| --- | --- | --- |
| **No simulated hands appear, so the buttons can't be pressed** | No hands or pointer appear, including with Point & click and ISDK pointer offset. The log shows a failed binding for `/interaction_profiles/microsoft/hand_interaction`. For Mac, **Hand Tracking Subsystem** and **Hand Interaction Profile** are still off in the saved settings. | **Open, and blocking manual testing.** Fix this first (section 8). |
| Background is black behind the panel | The simulated room showed before the render mode change, and went black after it. | Open |
| Yellow `MRUK EnableWorldLock` warning, repeated constantly | MRUK's World Lock is on and moves the view by about 0.5 m. The panel may look tilted because of it. | Open |
| `synth_env_server` crashing | It has crashed in past sessions, which makes passthrough black. | Comes and goes |
| Black Game tab behind the panel | Normal. The Game tab never shows passthrough. | Not a bug |

## 5. What is finished

"Automated" means our tests pass. It does **not** mean a person has checked it on a headset.

| Area | State |
| --- | --- |
| Route and collision evaluator, commands, undo/redo, revisions | Done, automated |
| Create a room by hand | Done, automated |
| Voice on the device: whisper.cpp in the APK, plus the rule interpreter in C# | Done, automated with **synthetic** speech only |
| Interpreter matches the Python version | Done, 38 cases match exactly |
| Rehearse, replay, explanations | Done, automated |
| Compare attempts, save, reopen, autosave and resume | Done, automated |
| Passthrough with a virtual background toggle | Done, automated; never seen on a real headset |
| Table placement with MRUK, with manual fallback | Done, automated with a fake room |
| Gaze with hand-ray fallback | Done, automated; never tried with real eye tracking |
| Pausing the replay when hands are lost | Done, automated |
| Android builds | A development APK and a release APK both built on 7 November |

Latest recorded test results (before the current uncommitted settings changes):

| Check | Result |
| --- | --- |
| Unity EditMode | 278 of 278 passed |
| Unity PlayMode, Experience category | 57 of 57 passed |
| Backend pytest | 172 passed |
| Project configuration check | 0 problems |
| Secret scan on the release APK | 0 hits |

**Never tried on a real Quest:** installing the APK, frame rate, passthrough, surface placement, eye tracking, microphone, permissions, comfort.

## 6. How to test the app by hand

Do these in the simulator once the hands problem is fixed. Write down pass or fail and anything awkward.

**Creating and editing**

1. Press **Got it**, then **Create with hands**.
2. Press **+0.5** on the width, then **Create example room**.
3. Pinch or ray-grab the console, drag it towards you, and release on clear floor. It should snap into place and the revision number should go up.
4. Drag the console onto the sofa and release. You should see a red striped outline and "Overlaps the sofa's starting position", and the console should return.
5. Press **Rotate**, **Switch route**, **Undo** twice, **Redo**, then **Reset**. Revision numbers should only go up, and Reset should restore the original layout.
6. Press **Larger**, **Smaller** and **Recenter**. The view changes, but the room's dimension label must not.

**Rehearsing**

7. Press **Reset**, then **Rehearse**. The sofa should stop short of the console, with a red marker at the contact point and a card saying "Blocked in this model".
8. Drag the console south and release. The card should say **OUT OF DATE** and the button should say **Rehearse again**.
9. Rehearse again. It should say "Clear in this model" and the sofa should reach the destination.

**Comparing and saving**

10. Create the example, **Rehearse**, then **Keep as A**. Drag the console south, **Rehearse**, **Keep as B**, then **Compare**. The card should show both attempts and what changed.
11. **Save**, press **Larger** once, stop Play and start it again. **Open saved project → Open** should bring back both attempts and the saved zoom.
12. **Open saved project → Delete** (press it twice). The list should be empty.

**Typed description**

13. **Describe (typed, dev input)**, choose the 6 x 5 m example, and **Interpret**. A review card should appear.
14. Try **No size given** and **Unsupported request**. The first should ask a question, and the second should show reasons with no build button.

**Voice** (needs microphone permission for Unity in macOS settings)

15. **Describe with voice**, allow recording, and say: "Prepare a six by five metre living room for a sofa delivery."
16. A transcript and a review card should appear. No server is involved.

**Mixed reality** (needs the simulator's room working)

17. In **Settings**, toggle passthrough. Try **Place on surface**, then **Place floating**.
18. Toggle the gaze option between eyes and head.

**On a real Quest** (the APK has never been installed)

```bash
adb devices
adb install -r artifacts/builds/tryscape-dev.apk
```

`adb` lives in `/Applications/Unity/Hub/Editor/6000.6.8f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/`. The package name is `com.tryscape.app`. Build the APK first (section 7), then repeat the steps above on the headset, and also check: microphone prompt, table placement in a real room, eye tracking, frame rate, comfort, and restarting the app.

## 7. Command-line checks and builds

**Close Unity first.** Run these from the repo root.

Run this once per terminal session:

```bash
export TRYSCAPE_UNITY_EDITOR=/Applications/Unity/Hub/Editor/6000.6.8f1/Unity.app/Contents/MacOS/Unity
export TRYSCAPE_ANDROID_NDK_ROOT=/Applications/Unity/Hub/Editor/6000.6.8f1/PlaybackEngines/AndroidPlayer/SDK/ndk/27.2.12479018
```

**Project configuration check**

```bash
scripts/unity_batch.sh verify-config "Verify complete: 0 problem" --artifact "$PWD/artifacts/unity/verify-config.json" -- -batchmode -quit -projectPath "$PWD/app-unity" -executeMethod Tryscape.Editor.ProjectConfigurator.VerifyBatch
```

**Regenerate the scene**

```bash
scripts/unity_batch.sh scene-build "Bootstrap scene written" -- -batchmode -quit -projectPath "$PWD/app-unity" -executeMethod Tryscape.Editor.SceneBuilder.BuildBootstrapBatch
```

**EditMode tests**

```bash
scripts/unity_batch.sh editmode "Saving results to" --artifact "$PWD/artifacts/unity/editmode.xml" -- -batchmode -projectPath "$PWD/app-unity" -runTests -testPlatform EditMode -testResults "$PWD/artifacts/unity/editmode.xml"
python3 scripts/check_test_results.py artifacts/unity/editmode.xml --min-tests 278 --no-skips
```

**PlayMode tests**

```bash
scripts/unity_batch.sh playmode "Saving results to" --artifact "$PWD/artifacts/unity/playmode.xml" -- -batchmode -projectPath "$PWD/app-unity" -runTests -testPlatform PlayMode -testCategory Experience -testResults "$PWD/artifacts/unity/playmode.xml"
python3 scripts/check_test_results.py artifacts/unity/playmode.xml --min-tests 57 --no-skips
```

Judge tests from the XML check, not from the log. If Unity exits with code 134 *after* the work succeeded, that is a known shutdown crash, and the wrapper reports it as `success_with_shutdown_abort`.

**Development APK** (output goes to `artifacts/builds/`)

```bash
scripts/unity_batch.sh android "Build result=Succeeded" --artifact "$PWD/artifacts/builds/tryscape-dev.apk" --artifact "$PWD/artifacts/builds/tryscape-dev.apk.manifest.json" -- -batchmode -quit -projectPath "$PWD/app-unity" -buildTarget Android -executeMethod Tryscape.Editor.BuildCli.BuildAndroid
```

For a release APK, add `-tryscapeRelease` and change the two `--artifact` paths to `tryscape-release.apk`. Then scan it for secrets:

```bash
python3 scripts/scan_build_secrets.py artifacts/builds/tryscape-release.apk
```

Release builds currently use the debug keystore.

**Contracts and release gates**

```bash
uv run --locked scripts/check_contracts.py
python3 scripts/check_release.py --profile r0
```

`check_release.py` currently exits with an error and lists 15 open gates. That's expected; see section 8.

**Optional backend** (not needed for the competition build)

```bash
cd services/api
uv sync --frozen && uv run ruff check . && uv run ruff format --check . && uv run mypy src && uv run pytest -q
```

See `services/api/README.md` and `docs/operations.md` for running it.

## 8. What to do next, in order

**A. Fix the simulator so people can test (highest priority)**

1. With Play stopped, in the **monitor icon** OpenXR tab, add **Hand Interaction Profile** under Enabled Interaction Profiles and tick **Hand Tracking Subsystem**. Press Play and see whether hands appear. If they still don't, check the Console for red errors.
2. Once it works, add the Mac settings to `app-unity/Assets/Tryscape/Editor/ProjectConfigurator.cs` so they can't drift: Multi-pass render mode and any hand features that turned out to be needed.
3. Fix MRUK World Lock (`EnableWorldLock` on the MRUK object, set up in `Editor/SceneBuilder.cs`). Turn it off, or keep it and stop the view shifting.
4. Find out why the simulated room went black after the Multi-pass change.
5. Rerun the checks in section 7 before committing.

**B. Run the manual tests in section 6** and record results in `docs/progress/phase-state.json`.

**C. Do the items that need a person or hardware**

| Item | What it needs |
| --- | --- |
| Install and test on a Quest | A Quest 3 or 3S. Without one, five release gates cannot pass. |
| Real speech recordings for accuracy | People recording the 33 cases; procedure in `docs/progress.md`. All voice runs so far used synthetic speech. |
| Regenerate the Meta DevAgent token | Unity → Meta XR AI Tools (Agent Bridge) settings → Remote Agent Server → Access Token → **Regenerate**, then **Start**, then run `python3 scripts/check_devagent_rotation.py`. |
| Second person repeats the setup | This is you. Tell us every step in this guide that was wrong or unclear. |
| Fix the `P1-backend-deployment-record` evidence hash | It fails because `docs/progress.md` changed after it was recorded. |
| Demo video and Devpost submission | Team decision. The deadline we have noted is 18 November 2026; confirm it on the Devpost page. |

**D. Release gates still open (15)**

- Manual simulator walkthrough for phases 1, 2, 3 and 4
- Quest device checks: `P1-drag-loss-device`, `P3-quest-device`, `P4-device-restart-resume`, `P3-ondevice-voice-quest`, `P4b-device-mr-gaze`
- DevAgent token rotation: `P2` and `P3`
- `P3-asr-human-speech-mac`
- `P0-second-contributor-repeat`, `P0-worker-isolation`
- `P1-backend-deployment-record` (stale hash)

## 9. Where to find things

| Path | Contents |
| --- | --- |
| `CLAUDE.md` | Project rules and engineering standards. Read this first. |
| `docs/demo-scope.md` | The experience and acceptance cases |
| `docs/technical-spec.md` | Architecture, contracts, algorithms |
| `docs/progress.md`, `docs/progress/phase-state.json` | Current state and gates |
| `docs/implementation-status.md` | Feature-by-feature status |
| `docs/dependencies.md` | Versions and licences |
| `docs/environment/m0-environment.md` | Toolchain and machine fixes |
| `app-unity/Assets/Tryscape/Core` and `Application` | Pure C# logic |
| `app-unity/Assets/Tryscape/Unity` | Unity adapters: experience, creation, mixed reality |
| `app-unity/Assets/Tryscape/Content/MovingDay` | The demo pack and rule interpreter |
| `app-unity/Assets/Tryscape/Editor` | Scene builder, project configurator, build script |
| `native/whisper_shim` | Native speech plugin source |
| `contracts/v1` | Shared JSON schemas and fixtures |
| `services/api` | Optional Python backend |

## 10. Working together

- Pull before you start: `git pull --rebase origin main`.
- Check `git status` before committing. Don't commit `app-unity/RuntimeActionBindings.json` or the copy in `Assets/StreamingAssets`; the Meta tools generate them while the simulator runs.
- Commit `.meta` files along with their assets.
- Don't upgrade Unity or packages, and don't overwrite lockfiles to get past a compile error.
- Run the relevant checks before pushing, and report honestly what you did and didn't run.
