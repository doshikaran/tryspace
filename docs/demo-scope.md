# Tryscape — Competition Demo Scope

Version: 2.2 · Prepared: 4 November 2026 · Revised: 6 November 2026

Status: buildable reference scope with explicit assumptions; **Moving Day is a provisional implementation default**, not a claim that the user approved it after every later idea discussion.

Related engineering authority: [Technical specification](technical-spec.md). Agent workflow: [CLAUDE.md](../CLAUDE.md).

## D01. Product identity and demo decision

**Tryscape turns a described situation into an interactive 3D rehearsal so a person can try actions, understand possible consequences and prepare before acting.**

Product line: **Understand the situation. Rehearse your options. Act prepared.**

The demo is a demonstration of that product, not a separate furniture-planning application. Its value is discovering a spatial problem, changing a supported condition and seeing an explained change in outcome. It must demonstrate a real dependency between the user's edit and the evaluator's result.

The user explored Moving Day, PC upgrades, a café, film blocking, accessibility visits, robot tasks and other possibilities. No final selection after the expanded discussion is assumed here. To let implementation start without inventing ten scenario packs, this package chooses Moving Day as the reference pack. If the owner chooses One Take or another demo, retain reusable application, scene, input and persistence boundaries but replace this pack's assets, contracts where necessary and acceptance tests. Do not build both in the competition window by default.

### D01.1 Proposed submission identity

| Item | Proposed value |
| --- | --- |
| Product | Tryscape |
| Demonstration | Moving Day — rehearse the route before delivery |
| Track | Productivity |
| Division | New Experience only if conceived/built inside the official competition window; confirm actual project history. |
| Primary audience | A person preparing a room for a furniture delivery. |
| Immediate outcome | Identify a modelled obstruction, clear it or choose a supported alternative route, and understand the difference. |
| Duration | A satisfying interaction in ≤5 minutes; complete supported session in ≤10 minutes. |
| Reference platform | Native Unity APK for Quest 3/3S, passthrough mixed reality by default; desktop simulator for development. |

Proposed form tagline, within 140 characters: **Turn a situation into a spatial rehearsal. Try your options, understand the consequences, and prepare before acting.**

## D02. The exact promise

The user describes a supported room/delivery situation. Tryscape constructs an illustrative room and objects, explains which measurements are defaults, and presents a proposed delivery route. The user watches that route encounter an obstruction, changes the room or chooses another route with their hands, and compares the next attempt.

R0 calculates continuous **floor-footprint clearance for a fixed-orientation payload**. It does not calculate full 3D lifting/turning, doorway height, lifting loads, stair navigation, human strength, furniture disassembly or a particular mover's future choices. Use the wording "clear in this model" rather than "guaranteed to fit". Movers are visual participants following assigned paths; the geometry result is not a prediction of their intentions.

The first reference rehearsal begins with the payload inside the room near an illustrative entrance. We are not claiming to simulate the route from a delivery truck through an entire building. Door art is context, not an evaluated external threshold. This narrower boundary must be visible enough that judges do not infer unsupported engineering capability.

## D03. Required experience sequence

### D03.1 Entry and permissions

Cold start opens in passthrough with a concise welcome panel offering `Describe with voice`, `Create with hands`, `Open saved` and `Help`. No registration, server, network or external device is required. Both creation routes reach the same editor and evaluator.

The tabletop is placed on a real horizontal surface (see D03.3) before creation. Permissions are requested only when their feature is entered: spatial data when surface placement starts, microphone when voice starts, eye tracking when gaze is enabled. Declining any of them leaves the hand journey usable.

Choosing Speak explains that speech is processed on the headset, then requests microphone permission only if needed. Recording shows a visible indicator, elapsed time and hand-selectable Stop/Cancel. Maximum recording is 15 seconds. Losing focus stops recording. Declining permission immediately offers hand creation.

### D03.2 Voice-created environment (on device)

Suggested supported request: "Prepare a six by five metre living room for a sofa delivery. There is a console table in the way."

**Revision 2.2: voice creation runs entirely on the headset.** The competition requires an APK and no hosted service, so R0 has no server. Speech recognition (whisper.cpp with an English model bundled in the APK) and the rule-based interpreter (`deterministic_rules`, ported to C#) run on the Quest. Audio never leaves the device. The Python creation service remains an optional development component; a hosted service and a model-assisted interpreter are R1.

The app transcribes the bounded recording on the headset, shows the transcript, interprets supported intent with the rule-based interpreter, validates the result and assembles a bounded proposal, then shows a review card. The card displays interpreted dimensions, selected sofa, the obstruction state, the fixed-orientation model and `Example environment`. The user confirms or adjusts values with hand controls. No scene becomes authoritative merely because the interpreter returned it.

The review must make defaults obvious. If the user says only "prepare my room", ask whether to use the example or enter dimensions. Selecting example values is not measurement confirmation. Audio stays in memory on the headset and is released after transcription; no recording is saved. No network is needed.

### D03.3 Watch

The room appears as a stable miniature on the user's real table or desk, seen through passthrough. On first entry the app uses Meta MR Utility Kit scene data to find horizontal table, desk or other surfaces near the user and places the tabletop on the nearest suitable one at seated reach; the user can move it to another detected surface. If no scene data exists, permission is denied or no surface is found, the app offers manual placement at a comfortable height in front of the user and says why. The real surface changes where and at what height the rehearsal sits, not the model geometry or the result. A route line indicates the selected approach. Two stylized movers accompany the sofa at an illustrative speed. On evaluation, the route is blocked by the console footprint. Playback stops near the computed violation. A clear outline connects the named console to the result card.

Explanation: "The console table obstructs the sofa's modelled route. Move it aside or compare another approach." The model margin and unassessed aspects are available through a compact details control. The application must not imply that the sofa physically collided before the conservative clearance boundary.

### D03.4 Intervene

The user grabs the console, sees a floor-projected placement preview and releases it in a clear region. A valid release commits one edit. An invalid release returns the object to its prior position with a readable reason. The prior result is now stale for the live scene; the saved attempt remains available.

Alternatively, the user selects the Northern approach. This changes the route, not the room. R0 does not expose arbitrary route editing or sofa turning during transport. Controls should reflect only implemented capabilities.

### D03.5 Replay and understand

The user selects Rehearse again. The evaluator checks the current scene. In the reference corrected arrangement, the route is clear in the model and playback reaches its destination. A result card links the changed console position to the different result. It discloses height/turning/lifting exclusions without replacing the explanation with a wall of text.

### D03.6 Compare and retain

The comparison panel displays Attempt A and Attempt B, their scene revisions, route labels, outcome and route length. The judge can switch between ghosted room snapshots or select a run for replay. Saved runs do not change when the live scene is edited.

The preparation summary is evidence-based: e.g., "Move the console out of the selected route before delivery." No invented cost savings or success probabilities. Save project, restart and reopen must preserve committed changes and both saved attempts within storage limits.

## D04. Supported creation inputs

| Input | R0 support and behavior |
| --- | --- |
| Spoken English | Supported initial creation, transcribed and interpreted on the headset; not a prerecorded response. The interpreter is rule-based (`deterministic_rules`), not an AI model. |
| Hand-selectable template | Same reference pack and parameter controls, without microphone. |
| Room width/depth | Integer-mm representation; user-facing m/cm controls; each dimension 5–8 m in this pack. |
| Payload | One supported standard sofa; additional catalog entries only after acceptance tests. |
| Initial obstruction | Console centered on route or parked south; selected through supported intent or hand controls. |
| Route preference | Direct or Northern approach. |
| Missing dimensions | Ask or clearly use illustrative defaults with confirmation. |
| Photos, PDFs, scans, arbitrary meshes | Not supported in R0; honest unsupported response. |
| General future behavior prediction | Not supported; participants follow declared scenario behavior. |

Voice or hands create the scene; editing, evaluation, playback, comparison and recovery are hand operated. Everything runs on the headset without any server. Do not present the `deterministic_rules` interpreter as AI.

## D05. Supported actions and controls

| Action | Expected behavior | Exclusion |
| --- | --- | --- |
| Select an object | Highlight and display its label/role. | Selecting a noneditable item does not imply it can move. |
| Move console | Floor-plane preview; 50 mm grid; commit only valid pose. | No passing through another committed object on release. |
| Rotate console | Hand-selectable quarter turns; placement revalidated. | No free continuous physics rotation. |
| Choose route | Switch among authored valid route definitions. | No unconstrained path synthesis. |
| Rehearse / replay | Evaluate selected revision then play the attributed result. | No model call needed per frame or for a rerun. |
| Pause / resume | Deterministic replay position; no unexpected movement after backgrounding. | No concurrent editing of an actively moving payload. |
| Compare | Two immutable attributed attempt snapshots. | No silent comparison between mismatched evaluator versions. |
| Undo / redo | New revisions; consistent restoration and result invalidation. | No revision-number rewind. |
| Reset | Restore confirmed creation snapshot; preserve explicitly saved runs. | No silent deletion of prior projects. |
| Recenter / tabletop zoom | Change presentation position/scale only. | Does not resize the domain geometry. |
| Save / open / delete | Versioned local project persistence and recoverable failures. | No cloud account requirement. |
| Place on a surface | Tabletop snaps to a detected real horizontal surface; reposition to another; manual placement fallback. | Placement is presentation only; it never measures the real room or changes geometry. |
| Passthrough / virtual background | Passthrough is the default; Settings can switch to a neutral virtual background and back. | Switching never changes the scene or results. |
| Gaze (look and pinch) | Look at a panel button or the console and pinch to select it; gazed items highlight. Uses eye tracking where the headset provides it, otherwise the hand ray stays the pointer. | Gaze never commits an edit by dwell alone; drags still use the hand. |

All controls must be reachable with hand rays, gaze-and-pinch or direct interaction from a stationary seated position. Do not require actual walking around the furniture. A Settings/Help/Pause route must remain available without hunting behind the user.

## D06. Reference scene and model constraints

The canonical SceneSpec and catalog are defined in technical-spec S06. Use their field names, IDs and units rather than maintaining a parallel scene format here.

| Property | Reference value |
| --- | --- |
| Room | 6000 mm wide × 5000 mm deep. Floor x increases east, z increases north. |
| Sofa | 1800 × 800 mm footprint; center `(1000,2500)`; yaw 0; fixed throughout each route. |
| Console | 600 × 1000 mm footprint; initially center `(2500,2500)`; yaw 0; editable. |
| Direct route | `(1000,2500) → (4000,2500) → (4000,3800)`. |
| Northern approach | `(1000,2500) → (1000,4000) → (4000,4000) → (4000,3800)`. |
| Corrective console position | `(2500,800)`. |
| Demonstration counterexample | Console center `(2500,4000)` blocks Northern approach. |
| Model margin | 25 mm per payload side; illustrative parameter, not lifting guidance. |
| Animation speed | 250 mm/s model-space, explicitly illustrative. |
| Presentation | Tabletop scale independent of model dimensions. |

### D06.1 Fixture F01 — blocked baseline

With the console at `(2500,2500)`, Direct is `blocked_in_model` on segment 0. The expanded console x bounds are 1275–3725 mm and z bounds are 1575–3425 mm. The earliest center intersection is `(1275,2500)`, fraction `11/120` of the 3000 mm first segment. The nominal payload-to-console gap at this point is the model's 25 mm margin. The explanation references the console, not the wall or destination.

This expected value is independently derived from console half-width 300 mm, sofa half-width 900 mm, center 2500 mm and 25 mm margin: `2500 − 300 − 900 − 25 = 1275`. The result must not depend on frame sampling. Playback stops before visually penetrating the console.

### D06.2 Fixture F02 — corrected layout

Move console to `(2500,800)`. Direct becomes `clear_in_model`. Its modeled length is 4300 mm. It remains a fixed-orientation transport check; the application does not suddenly claim height or mover-body assessment.

### D06.3 Fixture F03 — alternative plan

Keep the original console at `(2500,2500)` and select Northern approach. It is `clear_in_model`, length 4700 mm. Display the 400 mm route-length difference as a model calculation. Do not claim this proves a 400 mm reduction in actual travel or a meaningful reduction in human effort/time.

### D06.4 Fixture F04 — intervention can worsen the plan

Move console to `(2500,4000)` and select Northern approach. That route becomes `blocked_in_model`; Direct remains clear in the footprint model. This guards against a rigged demonstration that reports success after every edit. Undo must restore the exact prior layout semantics while using a new revision.

### D06.5 Parametric variant fixture

A spoken or hand-created 7000 × 6000 mm room produces correspondingly different room bounds, wall art and dimension labels. Keep reference route/object coordinates valid or deterministically remap through the pack assembler; the implementation must choose one documented rule. **R0 chooses fixed internal route/object coordinates for the 5–8 m room range**, preserving the fixtures above while changing the enclosing environment. Do not imply a globally optimized new route was inferred. Future variants may introduce true parametric routes under a new pack version.

## D07. Visual and explanatory design

Use stylized, coherent furniture rather than expensive photorealistic assets. Realism of the causal interaction matters more than decorative detail. Start with greybox geometry whose collision proxies match dimensions; replace art only after the result is correct.

The essential visual vocabulary is consistent:

- Selected object: outline plus label.
- Proposed placement: translucent preview and floor projection.
- Invalid placement: icon/text plus pattern/color, never color alone.
- Route: readable floor line with direction markers.
- Evaluated obstruction: highlighted blocker, payload footprint and location of the violation.
- Stale result: revision indicator and a clear Rehearse again action.
- Comparison: explicitly labelled Attempt A/B and ghosted changed-object positions.

Two movers provide narrative context, with a small set of looped carry/idle animations. Do not spend the initial schedule on general animation synthesis, detailed facial expressions, locomotion planning or believable physical lifting. Their paths/poses must not introduce visual collisions so obvious that they contradict the scoped footprint explanation; adjust presentation or label them as schematic.

Every displayed claim has a source: confirmed input, template assumption, evaluator evidence or illustrative animation. The result screen should make this legible without forcing users to read technical implementation details.

## D08. Proposed actual-footage video

Target duration: 2 minutes 30 seconds, leaving margin under the official three-minute limit. This is a planned shot sequence, not evidence that footage exists.

| Time | Footage | Message |
| --- | --- | --- |
| 0:00–0:15 | Passthrough: the tabletop snaps onto the user's real desk. | Your rehearsal sits in your own space. |
| 0:15–0:35 | Spoken request, transcript and review card, then the scene builds on the desk. | Describe the situation; it is built in your space. |
| 0:35–1:00 | Direct rehearsal stops at highlighted console. | Discover the modelled problem before acting. |
| 1:00–1:25 | Judge-style hand movement parks console and reruns. | Your intervention changes the result. |
| 1:25–1:50 | Alternative route and attempt comparison. | Explore options and see why outcomes differ. |
| 1:50–2:10 | Preparation summary and saved project reopened. | Leave with an understandable preparation plan. |
| 2:10–2:30 | Brief product identity and supported-scope statement. | Tryscape is a spatial rehearsal product, demonstrated through Moving Day. |

Capture actual app footage on Quest or an accepted simulator. Label simulator footage accurately. Editing may shorten waits if disclosed, but do not present prerecorded animation as interactive inference or fake a successful run. Include actual hand interaction and the computed state change. Music must be licensed and speech must remain clear. Avoid third-party logos and recognizable branded products in the scene.

## D09. Acceptance matrix

Each case needs a recorded outcome, commit, environment and evidence. `Not run` is not `Pass`. Cases marked device require headset validation. Automated checks support, but do not replace, real interaction and comfort checks.

| ID | Scenario / test | Required result |
| --- | --- | --- |
| AC01 | Clean first launch | Reach usable hand controls without registration/controller/laptop. |
| AC02 | Supported voice request | Actual audio → on-device ASR → rule-based interpreter → validated proposal → reviewed scene, with no network; ASR model and interpreter versions recorded; interpreter labelled honestly. Device required. |
| AC03 | Different supported room dimensions | Room bounds/labels reflect the request; persisted SceneSpec differs. |
| AC04 | Missing dimensions | Clarification or explicit example choice; no invented measured provenance. |
| AC05 | Mic denied, silent or cancelled | Clear recovery to hand creation; no stuck capture or infinite spinner. |
| AC06 | Hand-only complete journey | Create, edit, rehearse, compare, save, reload, reset and recover without controllers. |
| AC07 | F01 | Direct blocked at the independently derived first violation. |
| AC08 | F02 | Corrective move yields clear result; committed pose and revision recorded. |
| AC09 | F03 | Northern route clear; lengths 4300/4700 correctly attributed. |
| AC10 | F04 | A meaningful edit can introduce a new obstruction; explanation identifies it. |
| AC11 | Invalid placement | Preview rejected; canonical scene/revision unchanged. |
| AC12 | Hand tracking lost during drag | No surprise commit or thrown object; preview cancelled/recovered. Device required. |
| AC13 | Undo/redo and reset | Geometry restored correctly with monotonic revisions; prior run snapshots retained. |
| AC14 | Stale run after edit | Current result visibly invalidated; comparison still uses saved immutable scenes. |
| AC15 | Save, kill/restart, reopen | Last committed state and saved runs recover, within documented storage semantics. |
| AC16 | Corrupt/interrupted save | Backup/sample recovery works and does not erase unrelated projects. |
| AC17 | Offline | With networking off, voice and hand creation, editing, evaluation, replay and local save all work; the release build makes no network call. |
| AC18 | Late provider reply after cancel | It cannot replace the current scene or create a surprise save. |
| AC19 | Unsupported request | Honest bounded response; no arbitrary assets, code or fabricated capability. |
| AC20 | Tabletop rescale/recenter | Scene hash, dimensions and evaluator result unchanged. |
| AC21 | Pause/resume/background | Microphone stops; playback pauses; return is stable. Device required. |
| AC22 | Performance | Supported journey meets the official hardware frame-rate criterion; separate Quest evidence. |
| AC23 | On-device voice benchmark | End-of-recording to review latency, memory and frame-rate impact on Quest, with the exact ASR model; at least 18 of 20 supported quiet-room utterances give the right parameters after at most one confirmation. Device required. |
| AC24 | Thirty-minute repeated use | No crash, unbounded resource growth or worsening rendering that breaks the gate. |
| AC25 | Novice usability | Record uncoached completion and explanation comprehension; fix critical confusion. |
| AC26 | Judge install/access | Correct channel invite opens playable APK for a separate tester. |
| AC27 | Complete submission | Actual-footage video <3 min, form, team details and working build link. |
| AC28 | Freeze/availability | Submitted client/catalog remain unchanged and accessible through judging. |
| AC33 | Passthrough by default | App starts in passthrough on Quest; Settings toggles a virtual background and back; scene hash and results unchanged. Device required. |
| AC34 | Surface placement | Tabletop lands on a detected table/desk in at least three rooms the developer did not set up; reposition works; no scene data or denied permission gives manual placement with an explanation. Device required. |
| AC35 | Gaze selection | On an eye-tracking headset, look-and-pinch activates panel buttons and selects the console; without eye tracking the hand ray works unchanged; gaze alone commits nothing. Device required for eye tracking. |

Backend acceptance cases. **Revision 2.2: these apply to the optional development service and to R1; none is an R0 release gate.** On-device audio handling is covered by AC05, AC17 and AC23.

| ID | Scenario / test | Required result |
| --- | --- | --- |
| AC29 | Judge-facing backend access | HTTPS API, worker and model providers work from the headset without a developer laptop, embedded secret or the judge's own paid model account. |
| AC30 | Sessions, budgets and isolation | Another anonymous session cannot read/cancel jobs; invalid model output is rejected; rate and daily spend controls function. |
| AC31 | Remote-processing consent / deletion | Explain audio/transcript transfer, honor cancellation, delete temporary audio within policy, and enforce transcript expiry. |
| AC32 | Dependency after creation | Disconnect the backend after confirming a scene; editing, evaluation, replay, comparison, local save and reload still work. |

## D10. Competition fit and boundaries

Productivity is the proposed track because the intended outcome is practical preparation. One track is sufficient. Hands-first completion is mandatory; seated stationary use and short sessions are design guidelines that this demo deliberately supports. A polished tabletop environment should not require large physical movement. Official judging weights innovation, experience, implementation and presentation equally. [SRC-01, SRC-02]

Passthrough is required in R0 (revision 2.2) because the judging criteria ask for purposeful passthrough and correct MRUK use. Its purpose here: the rehearsal sits on the user's real table at seated reach, found from scene data, so it fits their space without manual setup. The core geometry still runs in a neutral virtual space when room data is unavailable. Placing an illustrative model on a real table does not measure the real room, and the app must not suggest it does. Gaze (Interaction SDK v207) is also required, as a look-and-pinch alternative to hand rays.

A voice-created scene plus generic advice is insufficient for our intended differentiation. The editable model, local evaluation, replay and evidence-based explanation are the entrant-built experience. R0 has no server dependency at all, voice included, which also answers the competition's wrapper guidance: remove every third-party service and the whole project still works. [SRC-01]

## D11. Priorities, cuts and out-of-scope work

### Required before candidate release

Passthrough by default with MRUK surface placement and a manual fallback; gaze-and-pinch selection with hand-ray fallback; on-device voice creation within supported bounds; hand-only alternative; truthful example provenance; one complete reference pack; exact evaluator; reliable hand editing; two route choices; replay/pause; attributed explanation; two-attempt comparison; local save/reopen; cancellation/recovery; APK/device validation and submission access.

### Optional only after required cases pass

More attractive mover animations, better sound design, a short guided tour, more catalog art, small onboarding refinements and extra accessibility settings validated on device. Quality improvements must not invalidate performance evidence or consume final packaging time.

### Deferred

Another demo pack, arbitrary film/robot/café generation, scans/PDFs, physics-based carrying and lifting, rotating the payload through a doorway, multiple floors, crowds, multiplayer, cloud accounts, commercial billing and foundation-model training. The broader Tryscape roadmap can include these; the submitted build must not advertise them as operational.

If schedule slips, reduce art variety and presentation extras. If on-device voice cannot meet its device gate, record that and take an explicit owner decision; do not quietly record the preferred prompt and pretend the feature works. If core geometry is wrong, block release until corrected. Do not replace validation with a cinematic prerecorded sequence.

## D12. Ownership, handoff and change record

| Owner | Initial deliverable |
| --- | --- |
| XR/application lead | Reproducible project, hand control, presentation root, APK pipeline. |
| Core lead | Contracts, fixtures F01–F04, commands/evaluator, result evidence and persistence semantics. |
| Voice/service lead | On-device ASR (whisper.cpp Android plugin) and the C# port of the rule-based interpreter; the optional FastAPI/PostgreSQL development service stays tested; hosting and a model-assisted interpreter are R1. |
| Experience/QA lead | Onboarding, art/audio, acceptance case tracking, novice sessions and actual-footage submission. |

Before changing demo theme, record the chosen audience, exact supported task, visible failure, allowed intervention, independently verifiable outcome, required assets and implementation estimate. Update technical evaluator capabilities only if the new task truly needs them. Keep product identity and voice-create/hand-operate interaction consistent.

Initial change entry: 4 November 2026 — implementation package created; reference pack narrowed to fixed-orientation Moving Day, with explicit illustrative provenance and no claim of final scenario approval. The prior hotel scenario and newer cinematic candidates are not silently added to required scope.

Revision 2.1 — 4 November 2026: primary creation now requires the Python backend and real model-assisted interpretation. Added AC29–AC32, cloud-processing disclosure and explicit offline fallback boundaries. Reference scene geometry is unchanged.

Revision 2.2 — 6 November 2026 (owner decision after reading the official rules): the rules require an APK in the Competition channel and no hosted service. R0 ships without a server. Voice stays: speech recognition and the rule-based interpreter run on the headset (AC02 and AC23 rewritten); AC29–AC31 apply to the optional service and R1. Passthrough with MRUK surface placement (AC33, AC34) and gaze interaction (AC35) become R0 requirements. Reference scene geometry is unchanged.

## D13. Official references

Checked 4 November 2026; official site controls if requirements change. These references describe the event, not validation of Tryscape.

- **SRC-01:** https://start-developer-competition-26.devpost.com/ and https://start-developer-competition-26.devpost.com/rules
- **SRC-02:** https://start-developer-competition-26.devpost.com/details/judging
- **SRC-03:** https://start-developer-competition-26.devpost.com/details/faqs

Submission planning deadline: 18 November 2026 at noon PST, equivalent to 19 November at 01:30 IST. Verify team eligibility, Start membership/developer access, project-history division, licenses, policy compliance and the one-entry rules before final submission. Those are not established simply by implementing this demo.
