# M0 environment record

Recorded 4–5 November 2026 (IST) on the primary development Mac. Every version below was observed with commands or lockfiles on this machine.

## Host

| Item | Observed |
| --- | --- |
| Model | MacBook Air `Mac17,3`, Apple M5, 10 cores, `arm64` native (`sysctl.proc_translated = 0`) |
| GPU / memory | Apple M5 (Metal 4) / 24 GB |
| OS | macOS 26.6 (25G72) |
| Free disk | ~265 GiB at M0 start |

## Pinned XR toolchain (installed and exercised)

| Component | Version | Evidence / note |
| --- | --- | --- |
| Unity Editor | **6000.6.8f1** (Unity 6.6, revision 237b6593792b, arm64) | `app-unity/ProjectSettings/ProjectVersion.txt`. Unity labels 6.6 a *Supported* (non-LTS) release; see decision D11 in technical-spec S24. |
| Render pipeline | URP 17.6.0 (template `com.unity.template.urp-blank-17.2.6`) | `Packages/packages-lock.json` |
| Meta XR Core SDK | **207.0.1** | Meta scoped registry `https://npm.developer.oculus.com` |
| Meta XR Interaction SDK (+ OVR integration) | **207.0.1** | single SDK family; no mixed majors |
| Unity OpenXR | **1.17.1** | Meta Core 207's version defines reference up to 1.17.0-pre.2; 1.19.0 was not chosen |
| XR Management / XR Hands / XR Core Utils | 4.6.1 / 1.9.0 / 2.6.0 | lockfile |
| Newtonsoft JSON (Unity) | 3.2.2 | lockfile |
| Unity Test Framework | 1.8.0 (built-in) | lockfile |
| Meta XR Simulator | **207.1** (`207.1.0.49.123`, arm64, `/Applications/MetaXRSimulator.app`) | OpenXR runtime "Meta XR Simulator" 207.1.0, OpenXR API 1.1.54 reported in smoke tests |
| Android Build Support | OpenJDK 17.0.18+8, SDK build-tools 36.0.0, platform-tools 37.0.1, platforms 34/36/37, cmdline-tools 16.0, Gradle 9.3.1 / AGP 9.1.1 | Unity Hub modules under `PlaybackEngines/AndroidPlayer` |
| Android NDK | **r27c (27.2.12479018)** at `AndroidPlayer/SDK/ndk/27.2.12479018` | See "Machine fixes" below |
| CMake | 3.22.1 | Installed by `sdkmanager` (see below) |
| Unity licence | Entitlement licence present; batch mode resolves it headless | `~/Library/Unity/licenses/UnityEntitlementLicense.xml` |

Player configuration, set and verified by `Tryscape.Editor.ProjectConfigurator` (`VerifyBatch` fails on any mismatch): application ID `com.tryscape.app` (**provisional**, not registered with a Meta app); IL2CPP; ARM64 only; min API 32 and target API 34 (Meta 207 rules); Vulkan only; linear colour; landscape-left; OpenXR loader on Android and Standalone; Meta Quest Support, Meta XR, Hand Tracking Subsystem, Meta Hand Tracking Aim and Oculus Touch profile features; hand tracking `ControllersAndHands` at HIGH frequency.

## Machine fixes applied during M0 (reproduce on new machines)

1. **CMake 3.22.1 missing** even though the Hub marked the module as selected:
   `JAVA_HOME=<AndroidPlayer>/OpenJDK <AndroidPlayer>/SDK/cmdline-tools/16.0/bin/sdkmanager --sdk_root=<AndroidPlayer>/SDK --install "cmake;3.22.1"`
2. **Embedded NDK folder incomplete** (`AndroidPlayer/NDK` lacks `source.properties`). Set `TRYSCAPE_ANDROID_NDK_ROOT=<AndroidPlayer>/SDK/ndk/27.2.12479018` for builds. `BuildCli` validates it and records the revision in the build manifest. The setting persists in this machine's EditorPrefs.

## Known toolchain issues

| Issue | Impact | Handling |
| --- | --- | --- |
| Intermittent editor shutdown abort, exit 134 (`libc++abi: terminate_handler unexpectedly returned`) after a step has finished; seen in about 1 in 4 runs, near Meta native telemetry teardown | A raw exit code can misreport a successful step | `scripts/unity_batch.sh` (Phase 2 revision) classifies a step `success_with_shutdown_abort` only when exit is 134, the teardown signature is logged **after** the success marker, there are no compile errors **and** every `--artifact` is fresh (deleted before the run, non-empty and newer afterwards). Unity's exit code and the classification are written to `artifacts/unity/<step>.status.json`; `--strict` exits 3 instead of 0. Observed again on 5 Nov on one `VerifyBatch` run |
| Meta DevAgent build processor injects this Mac's LAN IP and Remote Agent token into `Resources/DevAgentSettings` on **every** build | Credential leak into APKs | `StripDevAgentCredentials` (build preprocessor, order 100) blanks both; the asset is git-ignored; APK scan found 0 token hits (rechecked at `05ac0f6`; 0 hits in all git objects). The token itself (an Editor-generated Remote Agent Server Guid kept in EditorPrefs) has **not** been regenerated: Meta XR AI Tools → Remote Agent Server → Access Token → Regenerate |
| Unity development builds write `player-connection-ip=<this Mac's LAN IP>` into `assets/bin/Data/boot.config` | Private LAN address in dev APKs (not a credential) | Accepted for development builds; release `boot.config` not yet checked (no release build/keystore) |
| Meta agentic "operator" AAR (screen-capture foreground permission, open port) | Present in Development builds only | Meta's own processor excludes it from release builds; verify when the first release build exists |
| Headless first import left 66 orphaned OpenXR settings sub-assets | Every Android build re-serialized the file | `ProjectConfigurator` removes unreachable sub-assets; `VerifyBatch` fails if any return |
| Meta editor telemetry runs in batch mode | Privacy/CI noise | Owner preference (EditorPrefs); not changed by tooling |

## Other tools

Git 2.50.1; Python 3.12.8 (default `python3` 3.14.7); uv 0.7.22; PostgreSQL 18.0 (Homebrew, running); Homebrew 7.0.12; Xcode Command Line Tools only. Not installed: Docker, .NET SDK, `gh`, standalone `adb` (Unity's `platform-tools/adb` exists).

Local databases created on the Homebrew server: `tryscape_dev` plus test databases ending in `_test`. All of them can be safely dropped.

## Sources checked (5 November 2026)

- Meta XR SDK All-in-One: https://developers.meta.com/vr/downloads/package/meta-xr-sdk-all-in-one-upm/
- Meta XR Simulator (macOS, Apple Silicon): https://developers.meta.com/vr/documentation/unity/unity-simulate-xrsim/
- Unity 6 release support: https://unity.com/releases/unity-6/support and https://discussions.unity.com/t/unity-6-6-is-now-available/1735357
- Unity 6.3 requirements and Android dependencies: https://docs.unity.com/en-us/engine/6000.3/manual/get-started/install-and-upgrade/getting-started-installing-unity/system-requirements, https://docs.unity3d.com/Manual/android-supported-dependency-versions.html

## Phase 3 additions (5 November 2026)

| Item | Record |
| --- | --- |
| Speech spike runtime | whisper.cpp v1.9.5, commit `d1be6fde11ac6e0407606b4e42fe72d34add8037`, MIT. Built with CMake 4.4.5 and Apple clang 21 (`-DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF`) in `~/.cache/tryscape/whisper.cpp` (outside git). |
| Speech models | `ggml-tiny.en.bin` sha256 `921e4cf8…920b1f`, `ggml-base.en.bin` sha256 `a03779c8…d6c002` (OpenAI Whisper weights, MIT), in `~/.cache/tryscape/models` (outside git). |
| Insecure HTTP | `PlayerSettings.insecureHttpOption = DevelopmentOnly` (checked by `VerifyBatch`). Development builds reach the Mac's API through `adb reverse tcp:8000 tcp:8000`; release builds have no creation endpoint. |
| Non-development APK | `-tryscapeRelease` is debug-keystore signed, because there is no release keystore. Its `boot.config` has no `player-connection-*` keys and 0 hits for this Mac's LAN address; development APKs keep `player-connection-ip`. The release build exited 134 after a successful build (`success_with_shutdown_abort`). |
| DevAgent token | Still not regenerated. Verify after rotation with `python3 scripts/check_devagent_rotation.py` (PASS/FAIL only). |
