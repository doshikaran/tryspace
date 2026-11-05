# Phase 3b summary: speech → transcription → reviewed scene → rehearsal

Date: 6 November 2026 · Input commit `4185fb0` · Code commit `bfda97f` (docs in the following commit) · Phase 3 status: **blocked**

What's blocking:
- a model-assisted interpreter, deployment and voice benchmark (provider decision);
- human-speech accuracy;
- Quest checks;
- DevAgent token rotation.

**Functional outcome reached on the developer Mac:**
1. Speech is captured by the real Unity microphone adapter.
2. It is sent as a 16 kHz WAV to the local API.
3. whisper.cpp transcribes it in the worker.
4. The deterministic parser interprets it.
5. The proposal is reviewed.
6. It is confirmed with a hand press.
7. It is built and rehearsed.

The interpreter is still `deterministic_rules` (rule-based, **not** an AI model), and the service is a **local developer host, not a judge-accessible deployment**.

## Implemented

| Area | What exists now |
| --- | --- |
| `POST /v1/transcription-jobs` | Accepts the existing contract shape: a raw `audio/wav` body, `Content-Language: en`, a bearer session, `X-Processing-Consent-Version` (purpose `remote_audio_processing`) and an `Idempotency-Key`. The WAV is validated on the bytes before anything is stored: PCM, mono, 16 kHz, 16-bit, 15 s or less. A per-path limit of 512 KiB applies, while JSON routes keep 16 KiB. One active transcription per session is allowed, and the per-minute limit is shared with proposals. The result `{text, locale, provider_id}` matches `api.schema.json#/$defs/transcription_result`. |
| Shared job path | `_enqueue` covers idempotency, limits, consent, job and idempotency rows, and storing audio only once a new job is certain. A replay or rejection stores nothing. |
| `audio_store.py` | Random `aud_<128-bit>.wav` names in a 0700 directory; only the opaque name enters `jobs.input_ref`. Audio is deleted at every terminal state, on cancel and on expiry, and anything older than the 15-minute deadline is swept. Neither audio nor transcripts are logged; this was checked in the server logs, below. |
| Worker | Runs whisper.cpp through `WhisperCppTranscriber`, off the event loop. The binary, model and provider id are operator settings (`TRYSCAPE_ASR_*`), separate from the code. Silence gives a successful empty transcript. Failures are reported as `provider_unavailable`. If ASR isn't configured, transcription jobs fail honestly instead of pretending. |
| Unity `VoiceRecorder` + `IMicrophone` | Handles availability, the permission request, denial, a late grant after cancel (ignored), elapsed time, the automatic 15 s stop, device loss or revocation mid-recording, cancel, focus loss (stop and discard), silence (not uploaded) and too-short taps. |
| `AudioPreparation` | Downmixes to mono and resamples to 16 kHz (box low-pass when downsampling, linear interpolation when upsampling), bounded to 15 s; writes a PCM WAV header; computes RMS. |
| `UnityMicrophone` | Wraps `UnityEngine.Microphone` in a single bounded non-looping clip, destroyed after use. Permission comes from Android runtime permission on Quest and the OS prompt on the Mac. |
| `CreationCoordinator.SubmitAudio` | Transcription, then proposal, under **one** 30 s budget. An empty transcript fails as `no_speech` before interpretation. Cancelled, superseded, suspended and stale results are discarded. `SubmitAnswer` keeps the spoken origin for clarification answers. |
| `CreationServiceClient` | One in-memory session and transport shared by `BackendSceneProposalProvider` and the new `BackendTranscriptionProvider`. The WAV bytes are cleared after upload. |
| Panel / `TryscapeApp` | **Describe with voice**:<ul><li>a processing notice with **Allow and record** (asked once per app session);</li><li>a `RECORDING x.x s of 15 s` line with **Stop** and **Cancel**;</li><li>"Transcribing…", then "You said … Interpreting…";</li><li>**Record again**, **Type instead** and **Create with hands** on any failure.</li></ul>The review card says "Spoken input, transcribed by whisper_cpp_base_en. Interpreter: deterministic_rules 1.0.0 (rule-based, not an AI model)". **Edit transcript** opens the typed editor with the recognised words for hand or keyboard correction, or a preset. Focus loss or pause stops the microphone and cancels jobs. |

## Verification (this Mac, commit `bfda97f` unless noted)

| Check | Result | Evidence (`artifacts/phase-4/`) |
| --- | --- | --- |
| Backend: `uv sync --frozen; ruff check; ruff format --check; mypy src; pytest -q` | **169 passed** (155 before, 14 new transcription-route tests); exit 0 | `backend-checks.txt` |
| Contracts | 300 passed (unchanged; the transcription contract already existed) | `check_contracts.txt` |
| Backend mutation: audio not deleted after jobs | **3 tests failed as intended**; restored, 14/14 | `backend-mutation-audio-kept.txt` |
| EditMode | **170/170**, 0 skipped (149 before, 21 new); `clean_success` | `editmode.xml` |
| C# mutation: no obsolete check after transcription, plus focus loss leaving the mic running | **2 tests failed as intended**; restored (0 markers). Unity exited 134 in teardown, which **hid its own test-failure exit code**; the failures were caught only from the XML. | `editmode-mutation.xml` |
| PlayMode Experience | **41/41** (29 before, 12 new voice tests: consent, permission denial, unavailable or lost mic, focus loss while recording and while transcribing, cancel, silence and no-speech, service failure, clarification, edit transcript, hand fallback); `clean_success` | `playmode.xml` |
| **Live voice, real service** (`-testCategory BackendVoice`) | **1/1**. Three spoken requests (WAV file as the microphone source) went through `BackendTranscriptionProvider` → local API and worker → whisper.cpp base.en → `deterministic_rules` → review. Results: 6×5 m, in the way, Direct (confirmed and rehearsed, blocked at 11/120); 7×6 m, parked south, Northern; and unsupported. Server: 1 session, 3 transcription and 3 proposal jobs, all succeeded; inputs purged; audio store empty; consent recorded for both purposes; 0 transcript words in the API or worker logs. | `playmode-live-voice.xml`, `live-voice-transcripts.txt` |
| **Real Mac microphone loopback** (`-testCategory MicrophoneLoopback`) | **1/1**. `UnityMicrophone` on the "MacBook Air Microphone" recorded the reference request played through the speakers (`afplay`). whisper.cpp transcribed it as "Prepare a six by five meter living room for a sofa delivery. There is a console table in the way.", which reached review as a 6 m room. | `playmode-mic-loopback.xml` |
| XR simulator smoke | 2/2, `clean_success` | `xrsim-*.xml` |
| Config verify | 0 problems, `clean_success` | `verify-config.json` |
| Scene build | Scene regenerated (voice section present); Unity **exit 134 after the work** (`success_with_shutdown_abort`) | `scene-build-p4.status.json` |

**What the speech in these runs was:** macOS `say` synthetic voices (Samantha, Daniel, Karen), either as a file source or replayed acoustically into the real microphone. **No human speech has been tested**; no recordings were available.

## Android builds and the exit-134 investigation

**Previous non-development build (Phase 3, `7bb7c32`), exit 134.** The log is preserved at `artifacts/phase-4/build-134/`. The sequence was:
1. `Build result=Succeeded`, then "Batchmode quit successfully invoked".
2. A Meta analytics `folly::BrokenPromise` warning and "debugger-agent: Unable to listen".
3. "Exiting batchmode successfully … return code 0".
4. Only then, during native teardown, `libc++abi: terminate_handler unexpectedly returned` (SIGABRT, 134).

The APK had been generated before the abort. The build was not clean for a separate reason: its manifest reported a dirty tree because docs were edited during the build.

**Rebuild this phase.** Source: clean revision `bfda97f`, with `git status` empty before, between and after the builds (`build-source-revision.txt`, `tree-after-*.txt`). No tracked file was edited during either build.

| Build | Unity exit / classification | APK | Runtime |
| --- | --- | --- | --- |
| Non-development (`-tryscapeRelease`, debug keystore) | **0 / `clean_success`**; both artifacts fresh | sha256 `b98385be…`, 75,847,040 B, commit `bfda97f`, `working_tree_dirty: false`, not debuggable | **not run** (no Quest) |
| Development | **0 / `clean_success`** | sha256 `68825de4…`, 123,946,629 B, commit `bfda97f`, clean tree, debuggable | **not run** |

Conclusion: the 134 did not reproduce on the release build. It is an intermittent Unity 6000.6.8f1 / Meta XR 207 editor-teardown abort. It recurred this phase on a scene build and on an EditMode mutation run, so it is not specific to release builds. The wrapper still reports it separately (`success_with_shutdown_abort`, Unity exit 134), and nothing was suppressed.

**New finding:** when the abort happens after a failing test run, it replaces Unity's own non-zero test exit code. The test result XML is the only reliable verdict, and `check_test_results.py` must always run after the wrapper; it caught the mutation failures. A 134 must never be read as "tests passed".

**APK inspection** (`apk-scan.txt`, `apk-permissions.txt`):
- DevAgent token: 0 hits in both APKs.
- Non-development: no `player-connection-*` keys, 0 LAN-address hits.
- Development: the LAN address appears only as `boot.config` `player-connection-ip`, Unity's development profiler connection; expected.
- Both declare `RECORD_AUDIO`, which Unity adds for `Microphone` use, plus `INTERNET`, `MODIFY_AUDIO_SETTINGS` and `BLUETOOTH`. The last two arrive with the microphone and have not been reviewed for necessity.

## Security

- **Agent bridge:** not started, and not contacted with any token this phase.
- **Rotation status:** **not rotated**.
  - Nothing listens on port 48735, and EditorPrefs still has no `RemoteServer_AccessToken` key (checked by key name only).
  - The rotation check is therefore **unavailable**. That is *not* evidence that the old token is rejected.
  - The exposed value remains only in the git-ignored local `DevAgentSettings.asset`.
- **Exposure scan:** 0 hits in the working tree, all git objects (including unreachable ones) and both APKs.
- **Manual step:** Unity Editor → Meta XR AI Tools (Agent Bridge) settings → Remote Agent Server → Access Token → **Regenerate** → confirm → **Start**. Then run `python3 scripts/check_devagent_rotation.py`, which prints PASS/FAIL only. It sends the old token once, to confirm it gets 401, so run it only once you've decided that check is wanted.
- **Audio and transcripts:**
  - Audio is held in memory on the client, and the WAV buffer is cleared after upload.
  - On the server, audio lives in a 0700 temporary store and is deleted at every terminal state; the store was empty after the live run.
  - No transcript appears in the server logs.
  - The opt-in live and loopback **test** categories print transcripts to the Unity test log as evidence. Product code never logs them.

## Manual and device checks (outstanding)

| Check | Status | Missing prerequisite |
| --- | --- | --- |
| Human speech accuracy (Mac) | not run | recordings: `scripts/asr_spike.py --source recordings` (docs/progress.md) |
| Phase 1–3b simulator walkthrough, including voice (steps 1–14) | not run | a person at the Editor with the XR Simulator |
| Result-card and voice-card readability | not run | the same |
| Real hand usability | not run | headset or simulator with a person |
| Quest: RECORD_AUDIO prompt, deny/allow, revoke in settings, capture quality at 48 kHz, frame time while recording/uploading, lifecycle | not run | Quest 3/3S, plus `adb reverse tcp:8000 tcp:8000` to the Mac service |
| Quest frame rate, tracking, comfort; IL2CPP hash parity | not run | Quest |

## Deployment dependencies

- **A judge-reachable HTTPS host** for the API, the worker, PostgreSQL and a shared private audio directory, plus whisper.cpp with its model on that host. Needs the provider/hosting/budget decision (B-provider-budget).
- **Release builds have no creation endpoint**; one must be added as an allowlisted HTTPS origin in `CreationServiceConfig` (configuration only).
- **Edge and IP rate limits and a global queue limit.** The current limits are per session only. The local verification server ran with `TRYSCAPE_JOBS_PER_MINUTE_PER_SESSION=20`; the default is 4. One voice attempt uses 2 jobs, so the default allows two spoken attempts per minute, which needs a product decision.
- **A model-assisted `IntentExtractor`** to replace `deterministic_rules`.

## Next smallest task

Record 5–10 human utterances (two speakers) and run `scripts/asr_spike.py --source recordings`. Then install the development APK on a Quest with `adb reverse` and run one spoken creation on device.
