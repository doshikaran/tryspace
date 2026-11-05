# Phase 3 summary: typed-transcript creation pipeline; speech spike; phase blocked

Date: 5 November 2026 · Input commit `b7cd769` · Integration commit `7bb7c32` (docs in the following commit) · Status: **blocked**

What's blocking:
- the model-assisted interpreter, with its provider, deployment and voice benchmark;
- in-app microphone capture;
- token rotation;
- manual and device checks.

**Automated completion:** done for the bounded milestone *supported language input → validated proposal → hand confirmation → constructed environment → existing rehearsal*. It uses **typed development input** and a **deterministic rule-based interpreter**.

**Phase acceptance:** not reached. phases.md S9 gates require actual voice on a deployed, judge-reachable backend with model-assisted interpretation. None of that exists, and this phase claims none of it.

## What the input is (and is not)

- The transcript is typed in the panel or picked from four presets. It is labelled `DEVELOPMENT INPUT - typed transcript, not voice recognition` on screen and in the review card.
- The interpreter is `DeterministicIntentExtractor`: provider id `deterministic_rules`, version 1.0.0. It is a rule-based parser for the Moving Day 1.0.0 vocabulary, **not an AI model**.
  - Every proposal carries this provider metadata, and the review card says "rule-based, not an AI model".
  - Spec S10.2 forbids quietly replacing model-assisted creation with a keyword parser. Here it was done explicitly at the owner's direction, for this milestone only. It sits behind the `IntentExtractor` interface, so a model-assisted extractor can replace it under the same validation.
- AC02 (voice → deployed ASR → model-assisted intent) is **not** passed.

## Implemented behaviour

| Layer | What exists now |
| --- | --- |
| Backend `creation/intent.py` | `IntentExtractor` protocol plus the deterministic extractor. It reads room width and depth (pairs such as "six by five", "6 x 5 m", labelled "600 cm wide"; number words, decimals, "and a half", "point two five"; m/cm/mm), the payload, the console position (in the way / parked south), and the route (direct / northern). Unitless sizes are interpreted only when exactly one unit gives a 5–8 m room, and that interpretation is listed as an assumption. Feet, areas, conflicting sizes, non-whole millimetres and out-of-range sizes produce clarification questions; they are never clamped. Floor plans, scans, prediction, doors and stairs, and other furniture are reported as `unsupported`. Clarification answers come back as explicit `Answer: <label>` lines. |
| Backend `creation/assembler.py` | Deterministic Moving Day 1.0.0 assembly. Internal coordinates are fixed (D06.5); only the room changes. Fail-closed invariants (containment, margins, axis-aligned segments, shared destination, no overlap with the start). Reproduces the independent `proposals/valid_ready.json` scene exactly. |
| Backend `creation/proposals.py` | Status, at most 3 questions, and acknowledged defaults (`payload_default`, `obstruction_default`, `route_default`, `unit_interpreted`, `example_room_*`) as assumptions with `requires_confirmation`. Provenance is always `illustrative`. Every proposal is validated against the Pydantic contract mirror. |
| Backend `jobs.py`, `worker.py`, migration `0002` | `POST /v1/scene-proposal-jobs`, `GET /v1/jobs/{id}` and `DELETE /v1/jobs/{id}`:<ul><li>bearer anonymous session;</li><li>`X-Processing-Consent-Version` (minimal consent record);</li><li>`Idempotency-Key` (same key and body returns the existing job; a different body gets 409);</li><li>at most 4 jobs per minute and 1 active per session (429 with Retry-After);</li><li>30 s deadline, lazy expiry on poll;</li><li>owner-scoped lookups, so another session's job returns 404.</li></ul>A separate worker process claims jobs with `FOR UPDATE SKIP LOCKED` and a lease, retries at most once, and completes conditionally (cancelled, expired or re-leased jobs discard late output). Job input is purged to SQL NULL at terminal states, and jobs are deleted after 24 h. Provider exceptions become the safe code `provider_unavailable`. |
| Backend `creation/transcription.py` | WAV contract checked on the bytes (PCM, mono, 16 kHz, 16-bit, ≤15 s, ≤512 KiB). The whisper.cpp CLI adapter uses fixed argv with no shell, a timeout and a private temp file that is always deleted, and reports safe failure codes. **Spike only:** no `/v1/transcription-jobs` route uses it. |
| Unity `Application/Creation` | `SceneProposal`, `ISceneProposalProvider`, `ITranscriptionProvider` (interface only), and `CreationCoordinator`, which manages:<ul><li>a monotonic operation ID and per-operation cancellation;</li><li>a 30 s budget that does **not** rely on the provider honouring cancellation;</li><li>superseded, cancelled, suspended and stale-scene results, which are discarded and counted;</li><li>typed failures that never carry raw exception text.</li></ul> |
| `Content/MovingDay/MovingDayProposals` | A ready proposal is accepted only if it passes Core semantic validation, is illustrative with no measurement sources, and is **byte-identical (canonical bytes)** to the scene the C# template builds from the proposal's own intent. Hand corrections rebuild through the template, replacing only the assumptions about the corrected fields. |
| `Infrastructure/Json/CreationJson` | Strict readers for proposals, jobs (status/result rules and expected kind), receipts and sessions. They handle duplicate keys, unknown fields, the 1 MiB limit, bounded text and no control characters. |
| `Unity/Creation/BackendSceneProposalProvider` | UnityWebRequest adapter. The session token is held in memory only. A lost create request is retried once with the same idempotency key, polling runs at 1 s then about 2 s with jitter, and on cancel the request is aborted (on the main thread) and the job deleted. Endpoint configuration: development and the Editor use `http://127.0.0.1:8000` (reached from a Quest via `adb reverse tcp:8000 tcp:8000`, so no LAN address is compiled in); release builds have **no endpoint** and report "service not configured". `insecureHttpOption` is set to development builds only, and `VerifyBatch` checks it. |
| Unity panel and `TryscapeApp` | Welcome gets **Describe (typed, dev input)**. The describe section has a banner, an input field (rich text off), four presets, Interpret and Cancel. The review card shows the transcript as plain text, the interpreter, the interpreted values, "Example environment: illustrative", and "Check:" lines for assumptions needing confirmation. Actions: Confirm and build, Adjust with hands (reuses the width/depth/console/route controls), clarification choices, Describe again, and Create with hands for unsupported requests. Confirmation goes through the shared staged `CommitScene`: validate, build under staging, then switch the session. Any scene replacement invalidates older proposals, and focus loss or pause cancels interpretation. |

## Verification (this Mac; commit `7bb7c32` unless noted)

| Check | Command | Result | Evidence (`artifacts/phase-3/`) |
| --- | --- | --- | --- |
| Backend | `uv sync --frozen; ruff check; ruff format --check; mypy src; pytest -q` (PostgreSQL 18) | **155 passed** (78 at baseline); lint, format and types clean; exit 0 | `backend-checks.txt` |
| Contracts | `uv run --locked scripts/check_contracts.py` | **300 passed** (148 before plus 38 interpretation cases × 4 checks) | `check_contracts.txt` |
| Backend mutation 1 | out-of-range sizes silently clamped | **4 tests failed as intended**; restored, 50/50 | `backend-mutation-clamp.txt` |
| Backend mutation 2 | JSONB without `none_as_null` (the real bug found by the live run) | **2 tests failed as intended**; restored, 15/15 | `backend-mutation-json-null.txt` |
| EditMode | `unity_batch.sh editmode-p3 … --artifact …`; `check_test_results.py --min-tests 149 --no-skips` | **149/149**, 0 skipped (122 at baseline, 27 new); unity exit 0, `clean_success` | `editmode.xml`, `editmode-p3.status.json` |
| C# mutation A | coordinator generation check removed, plus pack check accepting any scene | 5 failed: all from the pack-check mutation. **The generation-check mutation survived**: `SceneReplaced()` always cancels, so that check is a redundant second guard. | `editmode-mutation-a.xml` |
| C# mutation B | `SceneReplaced()` no longer cancels | **2 tests failed as intended**; restored (0 markers) | `editmode-mutation-b.xml` |
| PlayMode Experience | `-testCategory Experience --min-tests 29` | **29/29** (16 at baseline, 13 new); unity exit 0, `clean_success` | `playmode.xml` |
| PlayMode live local backend | uvicorn and worker as separate processes on 127.0.0.1:8000 with PostgreSQL; `-testCategory Backend` | **2/2**, `clean_success`. The server logged 2 sessions, 5 proposal jobs (202) and 5 polls (200); all 5 jobs `succeeded` with input purged (SQL NULL). | `playmode-backend.xml`, `local-api.log` |
| Config verify | `VerifyBatch` | 0 problems, including the new HTTP-development-only check; `clean_success` | `verify-config.json` |
| Scene build | `SceneBuilder.BuildBootstrapBatch` | `clean_success` | `scene-build-p3.status.json` |
| XR simulator smoke | `scripts/run_xrsim_smoke.sh` | 2/2, `clean_success` each | `xrsim-*.xml` |
| Android development APK | `BuildCli.BuildAndroid` | `clean_success`; clean tree at `7bb7c32`; sha256 `10de57dd…`; 123,921,789 B; ARM64 | `apk-dev-manifest.json` |
| Android non-development APK | `BuildCli.BuildAndroid -tryscapeRelease` (debug keystore; no release keystore exists) | see the APK inspection below | `apk-release-manifest.json` |

No Unity step this phase ended in `success_with_shutdown_abort`. The non-zero Unity exits were all real failures, reported as such:
- two compile-error runs (exit 1), fixed;
- one PlayMode run with a test-helper failure (exit 2), fixed;
- the intended mutation runs (exit 2).

### Defects found and fixed during verification

1. **Job inputs were not purged to SQL NULL.** SQLAlchemy JSONB stores Python `None` as JSON `null`. The transcript content was replaced, but `IS NULL` retention checks failed. The original test read the value back through the ORM, which hid the problem. The live-backend run exposed it. Fixed with `none_as_null=True`; the test now checks `IS NULL` in raw SQL, and mutation 2 confirms it.
2. **The client timeout depended on the provider honouring its token.** Fixed: the coordinator races the provider against its own cancellation signal.
3. **The parser missed "6 and a half"**, written with digits. The ASR spike found it. Fixed, and the fixture case `digits_and_a_half` was added.

## Speech-recognition feasibility spike (time-boxed)

| Item | Value |
| --- | --- |
| Runtime | whisper.cpp **v1.9.5**, commit `d1be6fde11ac6e0407606b4e42fe72d34add8037` (2026-10-06), MIT licence. Built locally with CMake 4.4.5 and Apple clang 21 (Release, static) in `~/.cache/tryscape/whisper.cpp`, outside the repository. |
| Models | `ggml-tiny.en.bin` (77,704,715 B, sha256 `921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f`) and `ggml-base.en.bin` (147,964,211 B, sha256 `a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002`), from huggingface.co/ggerganov/whisper.cpp. OpenAI Whisper weights, MIT. Stored in `~/.cache/tryscape/models`, not in git. |
| Harness | `scripts/asr_spike.py`: audio → `WhisperCppTranscriber` → `ProposalService` → scored against `moving_day_cases.json`. Audio goes to a temp directory that is deleted; nothing is recorded into the repo. |

**Mac results, SYNTHETIC speech** (macOS `say`; voices Samantha en_US, Daniel en_GB, Karen en_AU, Rishi en_IN, Moira en_IE and Tessa en_ZA; 33 speakable cases each, 198 utterances; median clip 4.5 s; MacBook Air, Apple M5, macOS 26.6; 4 threads; a cold `whisper-cli` process per utterance, so model load is included):

| Model | Parameters correct | Mean WER* | ASR latency median / p95 | Peak RSS |
| --- | --- | --- | --- | --- |
| tiny.en | **196/198** | 0.276 | 0.16 s / 0.19 s | 258 MB |
| base.en | **197/198** | 0.275 | 0.37 s / 1.01 s | 373 MB |

\* The word error rate is not normalised for number formatting ("six" against "6") or spelling ("metre" against "meter"), so it overstates the errors that matter. The *parameters correct* column is the meaningful one.

What the failures were:
- One 15.5 s clip (Tessa, prompt-injection case; duration re-measured from a regenerated clip) was correctly rejected by the 15 s audio limit.
- tiny.en misheard "eight" as "egg" once; that produced a clarification question, not a wrong room.

- **Not evidence of accuracy on human speech.** The audio is synthetic and has no room noise. **Human-speech run: not run**, because I can't speak. The procedure is in `docs/progress.md`.
- **Android/Quest: not run** (no device). Under the spec's architecture, ASR runs on the backend, so the headset only captures and uploads audio. On-device whisper.cpp is not the R0 baseline.
- **Rendering impact:** not applicable on the Mac, because ASR runs in a separate process outside Unity. Quest microphone capture and its frame-time impact have not been measured.
- **Integration status:** connected through the backend transcription interface (`WhisperCppTranscriber`), with tests. **Not connected end to end:**
  - no `/v1/transcription-jobs` route;
  - no Unity microphone capture or `ITranscriptionProvider` implementation;
  - no deployed host.

  The voice feature is **not complete**.
- **Recommended next experiment:**
  1. Human recordings of the 33 cases from two or three speakers in a quiet room, scored with `--source recordings`.
  2. Then a resident `whisper-server` (model loaded once) behind the worker.
  3. Then Unity 16 kHz microphone capture with upload, timed end to end on Quest over `adb reverse`.

## Security and manual checks

| Item | Status |
| --- | --- |
| DevAgent token rotation | **Not done; human action.** The agent server was not running (nothing listening on port 48735; no Unity Editor process), and no agent-bridge operation was performed. EditorPrefs currently holds no `RemoteServer_AccessToken`, so the server would mint a new token when it next starts. The exposed value still sits in the git-ignored local `DevAgentSettings.asset`, and nothing proves revocation. `scripts/check_devagent_rotation.py` prints only PASS/FAIL: it needs the server running and checks that the old token gets 401 and the new token 200 on the authenticated read-only `/agentbridge/messages`. Its run today: server unreachable, so FAIL. |
| Secret scan | `scripts/scan_build_secrets.py` (counts only). Working tree and all git objects: 0 DevAgent token and server hits. APK results are below. |
| Phase 1/2 simulator walkthrough, result-card readability | **Not run**: needs a person with the Editor and simulator. |
| Phase 3 walkthrough (describe, review, correct, confirm, rehearse) | **Not run**: needs a person. |
| Real hand-interaction usability | **Not run.** The automated tests press panel buttons; they are not a usability check. |
| Quest frame rate, tracking, comfort, lifecycle; IL2CPP/device hash parity | **Not run**: no Quest connected. |
| Microphone and transcription on device | **Not run**: no capture implemented and no device. |

## Remaining limitations

- No model-assisted interpretation, deployment, transcription route, microphone capture or voice benchmark (all required by phases.md S9).
- On Quest, typed entry depends on the system keyboard appearing for a world-space uGUI `InputField`; this is untested. The four presets work without a keyboard.
- Clarification handles one question at a time. If a resubmitted answer would push the transcript over 2000 characters, the hand editor opens instead.
- The rate limit and one-active-job check are serialised per session with a row lock. There are no IP or global limits and no edge configuration; that is a deployment item.
- The worker processes one job at a time; there is no queue-depth limit beyond the per-session limits.

## Next

Human:
1. Regenerate the DevAgent token, start the server, run `python3 scripts/check_devagent_rotation.py`.
2. Run the simulator walkthroughs.
3. Record human speech for the spike.
4. Make the provider and budget decision.

Agent (when directed): the transcription-job route with resident whisper-server, then Unity microphone capture, measured on device.
