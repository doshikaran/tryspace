# M0 environment record

Recorded 4–5 November 2026 (IST) by the Claude Code session on the primary development Mac. Everything here was observed with commands on this machine unless marked **candidate** (chosen from official documentation, not yet installed or tested).

## Host

| Item | Observed |
| --- | --- |
| Model | MacBook Air, `Mac17,3` |
| CPU / architecture | Apple M5, 10 cores, `arm64` native (`sysctl.proc_translated = 0`, not Rosetta) |
| GPU | Apple M5, Metal 4 |
| Memory | 24 GB |
| OS | macOS 26.6 (build 25G72) |
| Free disk | ~265 GiB on `/` |

## Installed tools

| Tool | Version / state | Relevant to |
| --- | --- | --- |
| Git | 2.50.1 (Apple Git-155) | Repository |
| Python | 3.12.8 (`/opt/homebrew/bin/python3.12`); default `python3` is 3.14.7 | Backend uses 3.12 via `.python-version` |
| uv | 0.7.22 | Backend and script locking |
| PostgreSQL | 18.0 (Homebrew `postgresql@18`, running as a launchd service, socket `/tmp:5432`) | Backend tests and local dev |
| Node | 20.19.1 (nvm) | Not used |
| Homebrew | 7.0.12 | Package source |
| Xcode | Command Line Tools only (`/Library/Developer/CommandLineTools`) | Not required for Android |
| **Unity Hub / Editor** | **Not installed** (no app bundles found by Spotlight or under `/Applications`) | M0 blocker |
| **JDK** | **Not installed** (`java` stub only) | Supplied by Unity Android module |
| **Android SDK / adb** | **Not installed** | Supplied by Unity Android module; `adb` needed for device install |
| **Meta XR Simulator** | **Not installed** | M0 blocker |
| .NET SDK / Mono | Not installed | Optional; see "Pure-core tests outside Unity" |
| Docker | Not installed | Not required for M0 |
| `gh` | Not installed | Not needed: no remote is created |

Local databases created during M0 on the Homebrew server: `tryscape_dev` (migrated), and test databases `tryscape_test`, `tryscape_migration_test`, `tryscape_cleanclone_test`. Tests recreate them, so it's safe to drop them all.

## XR toolchain candidates (not installed, not yet tested)

| Component | Candidate | Basis |
| --- | --- | --- |
| Unity Editor | Latest **6000.3 LTS** patch offered by Unity Hub at install time; record the exact `6000.3.xfy` in `ProjectVersion.txt` | Unity 6.0 LTS support ended October 2026; 6.3 LTS is supported until December 2027. technical-spec S03 already prefers 6000.3. Meta's minimum is 6000.0.66f2. |
| Android modules | Unity Hub "Android Build Support" + "OpenJDK" + "Android SDK & NDK Tools" | Unity 6.3 lists OpenJDK 17, Android SDK API 35 and NDK r27c as Hub defaults; Gradle 9.1.0 / AGP 9.0.0; minimum Android API 25. |
| Meta XR SDK | All-in-One **207.0** (Core, Interaction SDK Essentials, Audio, Haptics); no mixed major families | Meta download page lists 207.0 with minimum Unity 6000.0.66f2. |
| XR provider | Unity OpenXR plugin with the Meta Quest feature group; **1.15.x** candidate per spec; take the version the Meta packages resolve and lock it | Meta recommends OpenXR; simulator needs OpenXR ≥ 1.13.0. |
| Simulator | **Standalone Meta XR Simulator for macOS (Apple Silicon)**; installs under `~/Library/MetaXR/MetaXRSimulator/<version>`. Don't use the deprecated UPM simulator or the old Homebrew build. | Meta docs: macOS support is ARM-only, needs OpenXR ≥ 1.13 and SDK ≥ v66. This M5 Mac is ARM, so it qualifies on paper. **macOS 26.6 + M5 compatibility is unverified.** |

Exact versions are pinned only after a successful baseline build (technical-spec S03, D08).

## Sources checked (5 November 2026)

- Meta XR All-in-One SDK (UPM): https://developers.meta.com/vr/downloads/package/meta-xr-sdk-all-in-one-upm/
- Meta XR Simulator overview / macOS: https://beta.developers.meta.com/horizon/documentation/unity/xrsim-intro/ and https://developers.meta.com/vr/documentation/unity/unity-simulate-xrsim/
- Unity 6 support lifecycle: https://unity.com/releases/unity-6/support
- Unity 6.3 system requirements: https://docs.unity.com/en-us/engine/6000.3/manual/get-started/install-and-upgrade/getting-started-installing-unity/system-requirements
- Unity Android dependency versions: https://docs.unity3d.com/Manual/android-supported-dependency-versions.html

Re-check these pages at install time. If the Hub offers a different 6000.3 patch or Meta has released a newer SDK family, record the actual choice here and don't mix families.

## Pure-core tests outside Unity (option, not adopted)

`Tryscape.Core` must compile under Unity's C# profile. Installing the .NET SDK (`brew install --cask dotnet-sdk`, free) would let a `netstandard2.1`/C# 9 test project run core tests without a Unity licence, which helps CI. It would be a second test harness alongside Unity EditMode tests, so it needs an explicit decision in M1. Not installed.
