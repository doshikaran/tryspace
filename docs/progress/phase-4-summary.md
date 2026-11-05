# Phase 4 summary: attempt comparison and local persistence

Date: 6 November 2026 · Input commit `5864768` · Code commit `6f402ef` (docs in the following commit) · Status: **automated-verified; phase blocked** on manual and Quest checks.

**Integrated outcome reached in the Editor (PlayMode):**
1. Create.
2. Rehearse (blocked) and keep Attempt A.
3. Drag the console with the real Interaction SDK grab.
4. Rehearse (clear) and keep Attempt B.
5. Compare and inspect both.
6. Save.
7. Destroy and rebuild the app over the same storage folder.
8. Reopen, inspect, compare and reset.

Attribution and prior results stay unchanged. The "restart" is a new app object graph in the same Editor process, not a process kill on a device.

## How to exercise it

1. **Create with hands → Create example room → Rehearse.** The result is "Blocked in this model" (console table).
2. **Keep as A.** The button now reads `A: rev 0`.
3. Grab the console and drag it south (towards you), then **Rehearse**. The card first shows OUT OF DATE, then "Clear in this model".
4. **Keep as B**, then **Compare**. The card lists:
   - both attempts (revision, route, outcome, computed route length, named obstruction);
   - "Changed: Console table moved from (2.50, 2.50) m to (2.50, 0.80) m.";
   - the evidence-based explanation;
   - "Preparation: Before delivery, move the console table out of the direct approach, as in Attempt B.";
   - the scope line.
5. **Inspect A / Inspect B** shows that attempt's own scene snapshot read-only: dragging is disabled, the obstruction is highlighted, and **Replay attempt** replays it. **Live scene** returns to the current scene; **Back to editing** leaves the comparison.
6. **Save** stores the project on this device (status "Saved …").
7. Quit and restart the app, then **Open saved project → Open**. You return to the saved revision with both attempts and the saved tabletop zoom. **Compare** still works, and **Reset** returns to the creation baseline. **Delete** takes two presses.

A route-change comparison works too: Keep A, **Switch route**, Rehearse, Keep B. It gives the Northern approach, 400 mm longer, labelled "a model calculation, not travel time or effort".

## Implemented

| Area | What exists now |
| --- | --- |
| Contract | `contracts/v1/schemas/project_snapshot.schema.json` defines `project_snapshot`, `run_snapshot` and `project_index`. `content_hash` is SHA-256 of the S09.1 canonical bytes without that member. Fixtures in `fixtures/persistence/` are written by the Python serializer in `check_contracts.py`: F01 as Attempt A (blocked), F02 as Attempt B (clear), an index, and newer-major, extra-field and tampered variants. The C# reader verifies them, and the C# writer reproduces them. |
| Core | `SceneSession.TryRestore(confirmed, current)` keeps the saved revision and the creation baseline as the Reset target; both are validated, with the same scene and project, and the current revision is not earlier than the baseline. Undo history is not persisted. `SceneCanonicalizer.ToCanonical(scene, includeAuditTimestamps)` gives the persisted form, which keeps `confirmed_at`; the hash form is unchanged. |
| Application | `AttemptBook`/`SavedAttempt` hold immutable `RehearsalRun` snapshots: scene, route and result with scene hash, revision, catalog and evaluator version. `AttemptComparison` reports outcome, route, computed length and its difference, scene changes (moved, turned, added or missing entities; route; room) and an explanation grounded in the evidence, naming the obstruction. It gives a preparation note only when the evidence supports one. Evaluator mismatch, catalog mismatch, different scenes and invalid or unsupported attempts are reported as **not compared**. Attempts saved with another evaluator version are labelled "not re-evaluated". There is no probability, saving or guarantee wording, and a test checks for it. |
| Infrastructure | `SnapshotJson` is a strict reader and writer: 1 MiB limit, strict UTF-8 and JSON, exact field sets, a major-version gate, and re-serialisation to verify `content_hash`. For runs it also checks the evidence hash, the scene hash, run and scene identity and the project reference. `ProjectStore` sits over the `IStorage` port with the layout `index.json`, `projects/<uuid>/project.json` and `projects/<uuid>/runs/<uuid>.json`; names come only from GUIDs. Writes go temp → flush → read-back verify → (verified old copy to `.bak`) → replace. Loads try primary, then `.tmp`, then `.bak`. An invalid primary is preserved as `.corrupt` and never rotated into the backup. Newer-version files are reported and never overwritten. Limits are 20 projects, 20 runs, 1 MiB each and 100 MiB total, and nothing is ever deleted to make room. Delete removes the directory (backups included) and the index entry. A missing or corrupt index is rebuilt from the project files. The store does not rely on POSIX atomic rename. |
| Unity | Edit panel rows: Menu / Save / Keep as A / Keep as B and Rehearse / Replay / Details / Compare. Compare section: card, Inspect A, Inspect B, Live scene, Replay attempt, Back to editing; `Drag` is null while inspecting. Projects section: card (title, time, attempt count), Previous, Next, Open, Delete (two presses), Menu. Opening validates everything before replacing the scene, so a failure keeps the current project. The tabletop zoom step is saved and restored. |

**Persisted:** confirmed baseline, current committed scene, tabletop zoom step, Attempt A/B run snapshots and timestamps.

**Never persisted:** audio, transcripts, session tokens, credentials, drag previews, undo history, playback progress. A test scans the written files.

## Verification (this Mac; code `6f402ef`)

| Check | Result | Evidence (`artifacts/phase-5/`) |
| --- | --- | --- |
| Contracts | **315 passed** (300 before, plus 7 persistence schema checks, 7 fixture-identity checks and 1 tamper check); exit 0 | `check_contracts.txt` |
| EditMode | **186/186**, 0 skipped (170 before, 16 new); unity exit 0, `clean_success` | `editmode.xml` |
| — comparison | A unchanged after edits and B; F01 vs F02 (blocked 11/120 by the console table, clear 4300 mm, 0 mm difference, one "moved" change, preparation text); F01 vs F03 (4700 mm, +400 mm, route change); F04 (B blocked); evaluator, catalog and invalid mismatches not compared | |
| — store | Save and reopen with a fresh store; Python fixtures load, re-evaluate to the same evidence hash and re-serialise identically; tampered, newer and extra-field files rejected; **every write stage of a save interrupted, both throwing and half-writing**: the project still loads the committed state and the next save succeeds; truncated primary recovers from `.bak` and is preserved as `.corrupt`; edited coordinate fails the hash; missing or misfiled run rejected; newer version protected from overwrite; 20-project limit enforced without deletion, and delete removes backups; oversized file and incompatible catalog rejected; index rebuilt; no audio or token fields; zoom does not change scene or evidence hashes; mismatched baseline rejected | |
| PlayMode Experience | **43/43** (41 before, 2 new: the full compare → save → restart → reopen → delete journey through the real Grabbable, and failed open preserving the current project); `clean_success` | `playmode.xml` |
| Mutation: old primary deleted instead of backed up | **Caught** by the corrupt-primary recovery test. The interruption test alone did **not** catch it, because recovery from the verified `.tmp` covers those stages. | `editmode-mutation-backup.xml` |
| Mutation: project content hash not checked | **Caught** (edited-coordinate assertion); restored, 0 markers | `editmode-mutation-hash.xml` |
| Backend (unchanged) | 169 passed; ruff and mypy clean | `backend-checks.txt` |
| XR simulator smoke | 2/2, `clean_success` | `xrsim-*.xml` |
| Config verify | 0 problems, `clean_success` | `verify-config.json` |
| Scene build | `clean_success` | `scene-build-p5.status.json` |
| Android development APK | clean `6f402ef`, unity exit 0 `clean_success`, sha256 `ea044a24…`, 149,063,705 B (124 MB in Phase 3b; the growth has not been investigated) | `apk-dev-manifest.json` |
| Android non-development APK | clean `6f402ef`, `working_tree_dirty: false`, sha256 `669323a9…`, 75,934,432 B. `Build result=Succeeded` (log line 8898), then **unity exit 134** in teardown (`terminate_handler`, line 8946): `success_with_shutdown_abort`, reported separately, not as clean. | `apk-release-manifest.json`, `android-release-p5.log` |
| APK secret scan | DevAgent token 0 hits in both. Non-development: no player-connection keys. Development: LAN address only as `player-connection-ip` (expected). | `apk-scan.txt` |

The Phase 2/3 results remain covered by the same suites: evaluator, voice and typed creation, and the hand-only path.

## Not verified (manual / device)

| Check | Status | Missing prerequisite |
| --- | --- | --- |
| Simulator walkthrough including steps 15–17 (compare, save, restart, reopen) | not run | a person at the Editor with the XR Simulator |
| Real kill/restart on device (AC15) and on-device storage behaviour (persistentDataPath on Quest, uninstall clears data) | not run | Quest |
| Background/resume during save or open on device (AC21) | not run | Quest |
| Comparison-card readability (17 px, about 10 lines) and hand usability of the new buttons (120 px wide) | not run | a person; the label "Rehearse again" may wrap |
| Human speech, Quest performance, tracking, comfort | not run (unchanged) | recordings, Quest |

## Known limitations

- **No schema migrations exist** because there is only v1.0.0. Newer major versions are read-protected; no upgrade function or fixture exists yet.
- **Undo/redo history is not saved.** After reopening it starts fresh, while Reset still returns to the creation baseline.
- **Attempt slots:** keeping a new run as A or B replaces that slot. There is no history browser of the 20 in-memory runs.
- **Snapshot inspection** re-renders the snapshot into the live room views. Ghosted side-by-side rooms are not implemented.
- **Different-room comparisons** are refused (`different_scene`); a new creation starts a new project with empty attempts.
- **The 100 MiB check** counts every file under the root, including `.bak` and `.corrupt` files, and doubles incoming bytes for the backup it will keep.
- **Corrupt-file handling:** there is no UI to export or inspect `.corrupt` files, and no bundled-sample button. Recovery messages point to "create the example with hands".
- **Unity teardown abort:** this phase it occurred on the non-development Android build. It remains intermittent.

## Next smallest task

Run the simulator walkthrough steps 15–17 (the compare, save and reopen journey) and record readability of the comparison card. Agent: an on-device kill/restart check once a Quest is connected; otherwise the Phase 5 device-quality preparation.
