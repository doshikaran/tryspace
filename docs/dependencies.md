# Dependencies and licences

Versions are the pinned values from `app-unity/Packages/packages-lock.json`, `services/api/uv.lock` and the environment record. Licences were read on 6 November 2026 from each package's own `LICENSE.md` (Unity/Meta) or installed metadata (Python). Before submission, a legal review of the Oculus SDK and Unity terms is still owed; this file is a record, not legal advice.

## Client (Unity)

| Component | Version | Licence | Ships in APK |
| --- | --- | --- | --- |
| Unity Editor / player runtime | 6000.6.8f1 | Unity Terms of Service (personal licence on this machine) | yes |
| Meta XR Core SDK `com.meta.xr.sdk.core` | 207.0.1 | Oculus SDK License Agreement | yes |
| Meta XR Interaction SDK `com.meta.xr.sdk.interaction(.ovr)` | 207.0.1 | Oculus SDK License Agreement | yes |
| OpenXR plugin `com.unity.xr.openxr` | 1.17.1 | Unity Companion License (source) / Unity Package Distribution License | yes |
| XR Management, XR Hands, XR Core Utils, Legacy Input Helpers | 4.6.1, 1.9.0, 2.6.0, 3.0.1 | Unity Companion License | yes |
| URP, render-pipelines core, Shader Graph | 17.6.0 | Unity Companion License | yes |
| Input System | 1.20.1 | Unity Companion License | yes |
| uGUI `com.unity.ugui` | 2.6.0 | Unity Companion License | yes |
| Newtonsoft Json `com.unity.nuget.newtonsoft-json` | 3.2.2 | Unity Companion License for the package (wraps Newtonsoft.Json, MIT) | yes |
| Unity Test Framework, NUnit | 1.8.0, 2.1.0 | Unity Companion License | no (Editor/tests) |
| Meta XR Simulator | 207.1.0 | Oculus SDK License Agreement | no (developer tool) |
| Android NDK (override) | r27c (27.2.12479018) | Android SDK License | toolchain only |

Art: all scene geometry is Tryscape-original greybox (`tryscape_original_greybox` in the catalog). No third-party models, textures, fonts beyond Unity's built-in LegacyRuntime font, or audio.

## Service (Python 3.12, `services/api`)

| Package | Version | Licence |
| --- | --- | --- |
| fastapi | 0.162.0 | MIT |
| starlette | 1.10.0 | BSD-3-Clause |
| pydantic / pydantic-settings | 2.14.2 / 2.16.2 | MIT |
| uvicorn | 0.54.0 | BSD-3-Clause |
| httpx | 0.28.1 | BSD-3-Clause |
| SQLAlchemy | 2.1.8 | MIT |
| alembic | 1.20.1 | MIT |
| psycopg (+binary) | 3.3.8 | LGPL-3.0-only (dynamically linked library; unmodified) |
| Dev only: pytest 9.2.0 (MIT), pytest-asyncio 1.4.1.post1 (Apache-2.0), ruff 0.17.3 (MIT), mypy 2.4.1 (MIT), jsonschema 4.26.0 (MIT) | | |
| PostgreSQL server | 18 | PostgreSQL License |

## Speech recognition (service host only; never in the APK or the repository)

| Component | Version | Licence | Location |
| --- | --- | --- | --- |
| whisper.cpp | v1.9.5, commit `d1be6fde11ac6e0407606b4e42fe72d34add8037` | MIT (repository LICENSE) | built in `~/.cache/tryscape/whisper.cpp` |
| Whisper model `ggml-base.en.bin` | sha256 `a03779c8…d6c002` | MIT (OpenAI Whisper weights) | `~/.cache/tryscape/models` |
| Whisper model `ggml-tiny.en.bin` | sha256 `921e4cf8…920b1f` | MIT | spike only |

## Not used

No paid model or ASR provider, no analytics SDK, no crash reporter, no cloud storage SDK. Meta's DevAgent/Agent Bridge ships inside the Core SDK, but Tryscape does not use it; `StripDevAgentCredentials` blanks its injected values at build time.
