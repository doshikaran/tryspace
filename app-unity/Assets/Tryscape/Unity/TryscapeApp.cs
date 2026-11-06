using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Tryscape.Application.Creation;
using Tryscape.Application.Editing;
using Tryscape.Application.Persistence;
using Tryscape.Application.Rehearsal;
using Tryscape.Content.MovingDay;
using Tryscape.Core.Commands;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Model;
using Tryscape.Infrastructure.Persistence;
using Tryscape.Unity.Creation;
using Tryscape.Unity.Experience;
using Tryscape.Unity.Presentation;
using UnityEngine;

namespace Tryscape.Unity
{
    public enum AppMode
    {
        Welcome,
        CreatingWithHands,

        /// <summary>Typed development transcript entry and in-flight interpretation (not voice recognition).</summary>
        DescribingTyped,

        /// <summary>Voice: consent notice, recording, transcription and interpretation in flight, or a voice failure.</summary>
        RecordingVoice,

        /// <summary>Reviewing a proposal: ready, needs clarification, or unsupported.</summary>
        ReviewingProposal,

        /// <summary>Correcting a ready proposal's values with the hand parameter controls.</summary>
        CorrectingProposal,
        Editing,

        /// <summary>Comparing retained Attempt A and Attempt B; one may be inspected read-only on the tabletop.</summary>
        Comparing,

        /// <summary>Local saved projects: open or delete.</summary>
        Projects,

        /// <summary>Settings, help and recovery: reachable from every mode (S11, D05).</summary>
        Settings,
    }

    /// <summary>
    /// Composition root (S04). Owns the pure-core session and wires it to presentation and input. Holds no domain
    /// rules itself: every committed change goes through <see cref="EditingSession"/> commands.
    /// Scope: hands-only creation from the Moving Day template, select, drag, rotate, route choice, undo/redo/reset,
    /// tabletop zoom/recenter (Phase 1), and local rehearsal: evaluate the active route with the core evaluator,
    /// replay it, highlight the modelled contact, explain it and mark it stale after edits (Phase 2). Phase 3 adds
    /// description-based creation from a typed development transcript: transcript, backend proposal, review,
    /// correction and confirmation with hands, then staged construction (S05.2). Voice capture, comparison and
    /// saving are not implemented yet.
    /// </summary>
    public sealed class TryscapeApp : MonoBehaviour, IDragHost
    {
        [SerializeField] ScenePresenter _presenter;
        [SerializeField] PlacementGhost _ghost;
        [SerializeField] PanelController _panel;
        [SerializeField] TabletopController _tabletop;
        [SerializeField] ResultHighlight _highlight;

        readonly IIdGenerator _ids = new RandomIdGenerator();
        readonly IUtcClock _clock = new SystemUtcClock();
        RehearsalSession _rehearsal;
        PlaybackController _playback;
        bool _showDetails;
        Catalog _catalog;
        CreationCoordinator _creation;
        readonly AttemptBook _attempts = new AttemptBook();
        IProjectRepository _store;
        string _projectCreatedAt;
        AttemptSlot? _inspecting;
        IReadOnlyList<ProjectListing> _projects = Array.Empty<ProjectListing>();
        int _projectIndex;
        bool _deleteArmed;
        AppMode _modeBeforeSettings = AppMode.Welcome;
        long _lastSavedRevision = -1;
        int _attemptsVersion;
        int _savedAttemptsVersion = -1;
        ProjectListing _resumable;
        bool _onboardingVisible;
        const string OnboardingKey = "tryscape.onboarding.v1.seen";
        VoiceRecorder _recorder;
        bool _audioConsentGiven;
        int _shownTenths = -1;
        MovingDayParameters _proposed;
        EditingSession _session;
        DragController _drag;
        Guid _selected;

        public AppMode Mode { get; private set; } = AppMode.Welcome;
        public MovingDayParameters Draft { get; private set; } = MovingDayParameters.Example;
        public EditingSession Session => _session;
        public ScenePresenter Presenter => _presenter;
        public PlacementGhost Ghost => _ghost;
        public TabletopController Tabletop => _tabletop;
        public Guid SelectedEntityId => _selected;
        public string Status { get; private set; } = "";

        /// <summary>Raised whenever anything the panel shows may have changed.</summary>
        public event Action StateChanged;

        /// <summary>Null while an attempt snapshot is being inspected: snapshots are read-only.</summary>
        public DragController Drag => _inspecting == null ? _drag : null;
        public RehearsalSession Rehearsal => _rehearsal;
        public RehearsalRun LatestRun => _rehearsal?.Latest;
        public bool IsResultStale => _rehearsal != null && _rehearsal.IsLatestStale;
        public PlaybackController Playback => _playback;
        public ResultHighlight Highlight => _highlight;
        public bool ShowingDetails => _showDetails;
        public RoomSpec Room => _session?.Current.Room;
        public CreationCoordinator Creation => _creation;

        /// <summary>Typed development transcript (not speech).</summary>
        public string TypedTranscript { get; private set; } = "";

        /// <summary>Values interpreted from the reviewed proposal; <see cref="Draft"/> holds any hand corrections.</summary>
        public MovingDayParameters ProposedParameters => _proposed;

        public bool ProposalEdited => _proposed != null && !MovingDayProposals.Same(_proposed, Draft);

        public VoiceRecorder Recorder => _recorder;
        public AttemptBook Attempts => _attempts;
        public AttemptSlot? Inspecting => _inspecting;
        public IReadOnlyList<ProjectListing> SavedProjects => _projects;
        public int SelectedProjectIndex => _projectIndex;

        /// <summary>Most recently saved project on this device (offered on the welcome panel; never auto-opened).</summary>
        public ProjectListing Resumable => _resumable;

        public bool OnboardingVisible => _onboardingVisible;

        public const string OnboardingText =
            "Tryscape rehearses a sofa delivery in a model room. Results are illustrative: floor footprint only, with " +
            "height, turning and lifting not assessed. Describe the room by voice (needs the creation service) or create " +
            "it with hands, then move the console and rehearse.";
        public bool DeleteArmed => _deleteArmed;

        /// <summary>Replaces local project storage (tests use a temporary directory).</summary>
        public void UseProjectStore(IProjectRepository store) => _store = store ?? throw new ArgumentNullException(nameof(store));

        /// <summary>The user accepted the remote audio-processing notice in this app session (S10.5).</summary>
        public bool AudioConsentGiven => _audioConsentGiven;

        public const string AudioNotice =
            "Your recording (up to 15 s) is sent to the Tryscape creation service to be transcribed, then deleted. " +
            "Nothing is saved on this headset. The text is then interpreted by rule-based development code.";

        /// <summary>
        /// Replaces the proposal and transcription sources, for tests or another adapter. Any in-flight operation is
        /// cancelled and the coordinator is rebuilt.
        /// </summary>
        public void UseProposalProvider(ISceneProposalProvider provider, TimeSpan? budget = null,
            ITranscriptionProvider transcriber = null)
        {
            if (_creation != null)
            {
                _creation.Changed -= OnCreationChanged;
                _creation.Dispose();
            }
            _creation = new CreationCoordinator(provider, new MovingDayProposals(_catalog), budget, transcriber);
            _creation.Changed += OnCreationChanged;
        }

        /// <summary>Replaces the microphone (tests use fakes). Any recording in progress is discarded.</summary>
        public void UseMicrophone(IMicrophone microphone)
        {
            if (_recorder != null)
            {
                _recorder.Cancel();
                _recorder.Changed -= OnRecorderChanged;
                _recorder.Completed -= OnRecordingCompleted;
            }
            _recorder = new VoiceRecorder(microphone);
            _recorder.Changed += OnRecorderChanged;
            _recorder.Completed += OnRecordingCompleted;
        }

        public void Configure(ScenePresenter presenter, PlacementGhost ghost, PanelController panel, TabletopController tabletop,
            ResultHighlight highlight = null)
        {
            _presenter = presenter;
            _ghost = ghost;
            _panel = panel;
            _tabletop = tabletop;
            _highlight = highlight;
        }

        void Awake()
        {
            _catalog = MovingDayPack.CreateCatalog();
            var service = new CreationServiceClient(CreationServiceConfig.ForThisBuild());
            UseProposalProvider(new BackendSceneProposalProvider(service, _catalog.CatalogVersion), null,
                new BackendTranscriptionProvider(service));
            UseMicrophone(new UnityMicrophone());
            _store = new ProjectStore(System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "tryscape"), _catalog);
            if (_ghost != null)
                _ghost.Hide();
            if (_presenter != null)
            {
                _playback = _presenter.GetComponent<PlaybackController>();
                if (_playback == null) // Unity null check; '??' is unsafe for UnityEngine.Object
                    _playback = _presenter.gameObject.AddComponent<PlaybackController>();
                _playback.Finished += OnPlaybackFinished;
                if (_highlight == null)
                {
                    var go = new GameObject("Result Highlight");
                    go.transform.SetParent(_presenter.transform, false);
                    _highlight = go.AddComponent<ResultHighlight>();
                }
            }
        }

        void OnDestroy()
        {
            if (_playback != null)
                _playback.Finished -= OnPlaybackFinished;
            if (_creation != null)
            {
                _creation.Changed -= OnCreationChanged;
                _creation.Dispose();
            }
            if (_recorder != null)
                _recorder.Cancel(); // releases the microphone and drops any audio
        }

        void Update()
        {
            if (_recorder != null && _recorder.State == RecorderState.Recording)
                _recorder.Tick(Time.unscaledDeltaTime);
        }

        void Start()
        {
            _attempts.Changed += () => _attemptsVersion++;
            _onboardingVisible = !SafePrefs.GetBool(OnboardingKey);
            RefreshResumable();
            if (_panel != null)
                _panel.Bind(this);
            SetStatus(_onboardingVisible ? OnboardingText
                : _resumable != null ? $"Welcome back. Resume \"{_resumable.Title}\", or choose how to create a rehearsal."
                : "Welcome. Choose how to create a rehearsal.");
        }

        public void DismissOnboarding()
        {
            _onboardingVisible = false;
            SafePrefs.SetBool(OnboardingKey, true);
            SetStatus(_resumable != null ? $"Resume \"{_resumable.Title}\", or choose how to create a rehearsal." : "Choose how to create a rehearsal.");
        }

        void RefreshResumable()
        {
            var list = _store?.List();
            _resumable = list != null && list.Succeeded && list.Value.Count > 0 ? list.Value[0] : null;
        }

        /// <summary>Opens the most recent saved project in Editing (S05.3): no playback or microphone is resumed.</summary>
        public bool ResumeLast()
        {
            RefreshResumable();
            if (_resumable == null)
            {
                SetStatus("There is no saved project to resume.");
                return false;
            }
            return OpenProject(_resumable.ProjectId);
        }

        // ---- Settings, help and recovery (S11, D05) ----

        public void OpenSettings()
        {
            if (Mode == AppMode.Settings)
                return;
            CancelDrag();
            if (_playback != null) _playback.Pause();
            if (_recorder != null && (_recorder.State == RecorderState.Recording || _recorder.State == RecorderState.RequestingPermission))
                _recorder.Cancel();
            if (_creation != null && _creation.IsBusy)
                _creation.Cancel();
            _modeBeforeSettings = Mode == AppMode.RecordingVoice || Mode == AppMode.DescribingTyped ? AppMode.Welcome : Mode;
            Mode = AppMode.Settings;
            SetStatus("Settings. Playback is paused; any recording or interpretation in progress was stopped.");
        }

        public void CloseSettings()
        {
            if (Mode != AppMode.Settings)
                return;
            Mode = _modeBeforeSettings == AppMode.Editing && _session == null ? AppMode.Welcome : _modeBeforeSettings;
            SetStatus("Back.");
        }

        public void RotateView(int direction)
        {
            if (_tabletop == null)
                return;
            _tabletop.RotateView(direction);
            SetStatus($"Tabletop turned to {_tabletop.YawDeg} degrees (view only; the room model is unchanged).");
        }

        public void ResetView()
        {
            if (_tabletop == null)
                return;
            _tabletop.SetYaw(0);
            _tabletop.SetZoomStep(2);
            _tabletop.Recenter();
            SetStatus("View reset: default size and orientation, centred in front of you.");
        }

        public void TogglePauseAll()
        {
            if (_playback == null || !_playback.HasTimeline)
            {
                SetStatus("Nothing is playing.");
                return;
            }
            if (_playback.IsPlaying)
                _playback.Pause();
            else if (!_playback.IsFinished)
                _playback.Resume();
            SetStatus(_playback.IsPlaying ? "Replay resumed." : "Replay paused.");
        }

        /// <summary>Recovery: reopens the current project's last saved state, discarding unsaved edits.</summary>
        public bool ReloadSaved()
        {
            if (_session == null)
            {
                SetStatus("Nothing to reload.");
                return false;
            }
            var id = _session.Current.ProjectId;
            var list = _store.List();
            if (!list.Succeeded || list.Value.All(p => p.ProjectId != id))
            {
                SetStatus("This rehearsal has not been saved yet, so there is nothing to reload.");
                return false;
            }
            return OpenProject(id);
        }

        public string AboutText =>
            "Tryscape R0 (competition build). Scenario: Moving Day pack 1.0.0 (provisional reference scenario). " +
            "Interpreter: deterministic_rules (rule-based, not an AI model). Evaluator " + RouteEvaluator.Version + ". " +
            "Projects are stored only on this device; uninstalling removes them. " + Explanation.Scope;

        // ---- Creation (hands-only path) ----

        public void BeginHandCreation()
        {
            _recorder.Cancel();
            _creation.Cancel();
            _proposed = null;
            Mode = AppMode.CreatingWithHands;
            Draft = MovingDayParameters.Example;
            SetStatus("Example values shown. Adjust them or create the example room.");
        }

        public void ShowWelcome()
        {
            InspectLive();
            CancelDrag();
            _recorder.Cancel();
            _creation.Cancel();
            _proposed = null;
            if (_playback != null) _playback.Pause();
            Mode = AppMode.Welcome;
            SetStatus(_session == null
                ? "Welcome. Choose how to create a rehearsal."
                : "Menu. Resume editing, or create a new example (replaces the current scene).");
        }

        public void ResumeEditing()
        {
            if (_session != null)
            {
                Mode = AppMode.Editing;
                Changed();
            }
        }

        public void AdjustRoomWidth(int deltaMm) => SetDraft(Draft.With(widthMm: Clamp(Draft.RoomWidthMm + deltaMm)));
        public void AdjustRoomDepth(int deltaMm) => SetDraft(Draft.With(depthMm: Clamp(Draft.RoomDepthMm + deltaMm)));
        public void SetDraftObstruction(ObstructionState state) => SetDraft(Draft.With(obstruction: state));
        public void SetDraftRoute(string routeId) => SetDraft(Draft.With(routeId: routeId));

        /// <summary>
        /// Confirms the hand-built draft, or the corrected proposal in <see cref="AppMode.CorrectingProposal"/>. Construction
        /// is staged, then the session switches atomically.
        /// </summary>
        public bool ConfirmCreation()
        {
            if (Mode == AppMode.CorrectingProposal)
                return ConfirmProposal();
            var problem = Draft.Problem();
            if (problem != null)
            {
                SetStatus($"Cannot create: {problem}");
                return false;
            }
            var scene = MovingDayTemplate.Assemble(Draft, _ids);
            if (!CommitScene(scene))
                return false;
            SetStatus($"Created a {scene.Room.WidthMm / 1000.0:0.0#} x {scene.Room.DepthMm / 1000.0:0.0#} m example room. " +
                      "Press Rehearse to check the route, or grab the console table to move it.");
            return true;
        }

        // ---- Creation from a description (typed development transcript; S05.2, S10) ----

        public void BeginTypedDescription()
        {
            CancelDrag();
            if (_playback != null) _playback.Pause();
            _recorder.Cancel();
            _creation.Cancel();
            _proposed = null;
            Mode = AppMode.DescribingTyped;
            SetStatus("DEVELOPMENT INPUT: type or pick a description. This is not voice recognition.");
        }

        public void SetTypedTranscript(string text)
        {
            TypedTranscript = text ?? "";
            Changed();
        }

        /// <summary>Sends the typed transcript for interpretation. The current scene is untouched until confirmation.</summary>
        public void SubmitTranscript()
        {
            if (Mode != AppMode.DescribingTyped)
                return;
            _ = _creation.Submit(TypedTranscript);
        }

        // ---- Creation from speech (S10.1): consent, record, transcribe, interpret, review ----

        /// <summary>"Describe with voice": shows the processing notice first, then records.</summary>
        public void BeginVoice()
        {
            CancelDrag();
            if (_playback != null) _playback.Pause();
            _recorder.Cancel();
            _creation.Cancel();
            _proposed = null;
            Mode = AppMode.RecordingVoice;
            if (!_audioConsentGiven)
            {
                SetStatus("Before recording: " + AudioNotice);
                return;
            }
            _recorder.Begin();
        }

        public void AllowVoice()
        {
            if (Mode != AppMode.RecordingVoice)
                return;
            _audioConsentGiven = true;
            _recorder.Begin();
        }

        public void StopRecording() => _recorder.Stop();

        /// <summary>Cancels recording, transcription or interpretation. Nothing is uploaded or changed after this.</summary>
        public void CancelVoice()
        {
            var wasRecording = _recorder.State == RecorderState.Recording || _recorder.State == RecorderState.RequestingPermission;
            _recorder.Cancel();
            if (_creation.IsBusy)
                _creation.Cancel();
            SetStatus(wasRecording ? "Recording cancelled; nothing was sent. Nothing was changed." : "Cancelled. Nothing was changed.");
        }

        /// <summary>Review correction: open the typed editor with the transcript, to fix words or pick a preset.</summary>
        public void EditTranscript()
        {
            // What the user actually said or typed (local), without clarification answer lines.
            var transcript = string.Join("\n", (_creation.Transcript ?? "").Split('\n')
                .Where(line => !line.TrimStart().StartsWith(ClarificationAnswers.Prefix, StringComparison.OrdinalIgnoreCase))).Trim();
            BeginTypedDescription();
            TypedTranscript = transcript;
            SetStatus("Correct the transcript or pick an example, then Interpret. Nothing is built until you confirm.");
        }

        /// <summary>Recording/transcription status line for the voice section.</summary>
        public string VoiceText
        {
            get
            {
                if (Mode != AppMode.RecordingVoice || _recorder == null)
                    return "";
                if (!_audioConsentGiven)
                    return AudioNotice;
                if (_recorder.State == RecorderState.Recording)
                    return $"RECORDING  {_recorder.ElapsedSeconds:0.0} s of {_recorder.MaxSeconds} s\nDescribe the room, the sofa delivery and the console table. Press Stop when done.";
                if (_recorder.State == RecorderState.RequestingPermission)
                    return "Waiting for microphone permission...";
                if (_creation.State == CreationState.Transcribing)
                    return "Transcribing your recording...";
                if (_creation.State == CreationState.Proposing)
                    return $"You said: \"{Clip(_creation.Transcript)}\"\nInterpreting...";
                return "";
            }
        }

        static string Clip(string text) => text == null ? "" : text.Length > 160 ? text.Substring(0, 160) + "..." : text;

        void OnRecorderChanged()
        {
            switch (_recorder.State)
            {
                case RecorderState.Recording:
                    var tenths = (int)(_recorder.ElapsedSeconds * 10);
                    if (tenths == _shownTenths)
                        return; // refresh the panel at most ten times per second
                    _shownTenths = tenths;
                    SetStatus("Recording. Press Stop when you have finished, or Cancel.");
                    break;
                case RecorderState.RequestingPermission:
                    SetStatus("Microphone permission is needed to record. Choose Allow in the system prompt.");
                    break;
                case RecorderState.Failed:
                    SetStatus(RecorderFailureText(_recorder.Failure) + (_session != null ? " Your current scene is unchanged." : ""));
                    break;
                default:
                    _shownTenths = -1;
                    Changed();
                    break;
            }
        }

        void OnRecordingCompleted(AudioClipData clip)
        {
            _shownTenths = -1;
            if (Mode != AppMode.RecordingVoice)
                return;
            _ = _creation.SubmitAudio(clip);
        }

        static string RecorderFailureText(RecorderFailure failure)
        {
            switch (failure)
            {
                case RecorderFailure.Unavailable:
                    return "No microphone is available. Type the description instead, or create with hands.";
                case RecorderFailure.PermissionDenied:
                    return "Microphone permission was not granted, so nothing was recorded. Type instead or create with hands; you can allow the microphone in system settings.";
                case RecorderFailure.DeviceLost:
                    return "The microphone stopped (it was disconnected or permission was revoked). The recording was discarded.";
                case RecorderFailure.TooShort:
                    return "That recording was too short. Record again and describe the room.";
                case RecorderFailure.Silence:
                    return "No sound was picked up, so nothing was sent. Check the microphone and record again.";
                case RecorderFailure.Interrupted:
                    return "Recording stopped because the app lost focus; the audio was discarded.";
                default:
                    return "Recording failed.";
            }
        }

        public void CancelProposal()
        {
            if (_creation.State == CreationState.Proposing)
            {
                _creation.Cancel();
                SetStatus("Cancelled. Nothing was changed.");
            }
        }

        /// <summary>Picks a clarification choice: resubmits it as an answer, or opens the hand editor for dimensions.</summary>
        public void ChooseClarification(int index)
        {
            var proposal = _creation.Proposal;
            if (Mode != AppMode.ReviewingProposal || proposal == null || proposal.Status != ProposalStatus.NeedsClarification)
                return;
            var question = proposal.Questions[0];
            if (index < 0 || index >= question.Choices.Count)
                return;
            var choice = question.Choices[index];
            var next = choice == ClarificationAnswers.EnterDimensions ? null : ClarificationAnswers.Append(proposal.Transcript, choice);
            if (next == null)
            {
                BeginHandCreation();
                SetStatus("Enter the room size with the controls. Values stay illustrative, not measurements.");
                return;
            }
            Mode = _creation.TranscriptFromVoice ? AppMode.RecordingVoice : AppMode.DescribingTyped;
            TypedTranscript = next;
            _ = _creation.SubmitAnswer(next);
        }

        public void BeginProposalCorrection()
        {
            if (Mode == AppMode.ReviewingProposal && _proposed != null)
            {
                Mode = AppMode.CorrectingProposal;
                SetStatus("Adjust the interpreted values with hand controls, then confirm.");
            }
        }

        public void BackToProposalReview()
        {
            if (Mode == AppMode.CorrectingProposal)
            {
                Mode = AppMode.ReviewingProposal;
                Changed();
            }
        }

        /// <summary>
        /// Hand confirmation of a ready proposal (with any corrections). The proposal must still be current: a newer
        /// scene, cancellation or superseding operation makes it unconfirmable.
        /// </summary>
        public bool ConfirmProposal()
        {
            if (!_creation.TryGetConfirmable(out var proposal, out var generation) || _proposed == null)
            {
                SetStatus("There is no current proposal to confirm.");
                return false;
            }
            var problem = Draft.Problem();
            if (problem != null)
            {
                SetStatus($"Cannot create: {problem}");
                return false;
            }
            var edited = ProposalEdited;
            var scene = MovingDayProposals.SceneFor(proposal, Draft, _ids);
            if (!_creation.IsCurrentGeneration(generation) || !CommitScene(scene))
                return false;
            _proposed = null;
            SetStatus($"Built a {scene.Room.WidthMm / 1000.0:0.0#} x {scene.Room.DepthMm / 1000.0:0.0#} m room from your description" +
                      (edited ? " with your corrections" : "") + ". Illustrative, not measured. Press Rehearse to check the route.");
            return true;
        }

        /// <summary>Review card text: transcript (plain text), interpretation, assumptions, questions or reasons.</summary>
        public string ReviewText
        {
            get
            {
                var proposal = _creation?.Proposal;
                if (proposal == null)
                    return "";
                var text = new StringBuilder();
                if (_creation.TranscriptFromVoice && _creation.TranscriptionProviderId != null)
                    text.Append("Spoken input, transcribed by ").Append(_creation.TranscriptionProviderId).Append(". Interpreter: ");
                else
                    text.Append("Typed development input (not voice). Interpreter: ");
                text.Append(proposal.ProviderId).Append(' ').Append(proposal.ProviderVersion)
                    .Append(proposal.ProviderId == "deterministic_rules" ? " (rule-based, not an AI model)" : "").Append('\n');
                var said = proposal.Transcript.Split('\n')[0];
                text.Append(_creation.TranscriptFromVoice ? "You said: \"" : "You wrote: \"").Append(said.Length > 160 ? said.Substring(0, 160) + "..." : said).Append("\"\n");
                switch (proposal.Status)
                {
                    case ProposalStatus.Ready:
                        var d = Draft;
                        text.Append($"Room {d.RoomWidthMm / 1000.0:0.0##} m x {d.RoomDepthMm / 1000.0:0.0##} m  |  Standard sofa  |  ")
                            .Append(d.Obstruction == ObstructionState.Center ? "Console in the way" : "Console parked south").Append("  |  ")
                            .Append(d.RouteId == MovingDayPack.DirectRouteId ? "Direct approach" : "Northern approach").Append('\n');
                        if (ProposalEdited)
                            text.Append("Edited with hand controls.\n");
                        text.Append("Example environment: illustrative, not measurements of your home.\n");
                        foreach (var a in proposal.Assumptions.Where(a => a.RequiresConfirmation))
                            text.Append("Check: ").Append(a.Text).Append('\n');
                        break;
                    case ProposalStatus.NeedsClarification:
                        text.Append(proposal.Questions[0].Prompt);
                        if (proposal.Questions.Count > 1)
                            text.Append($" ({proposal.Questions.Count - 1} more question{(proposal.Questions.Count > 2 ? "s" : "")} after this.)");
                        break;
                    default:
                        text.Append("Not supported in this build.\n");
                        foreach (var a in proposal.Assumptions)
                            text.Append(a.Text).Append('\n');
                        text.Append("Supported: one standard sofa and one console table in a rectangular room, 5 to 8 m per side, Direct or Northern approach.");
                        break;
                }
                return text.ToString().TrimEnd();
            }
        }

        void OnCreationChanged()
        {
            switch (_creation.State)
            {
                case CreationState.Transcribing:
                    SetStatus("Transcribing your recording... (Cancel to stop)");
                    break;
                case CreationState.Proposing:
                    SetStatus("Interpreting your description... (Cancel to stop)");
                    break;
                case CreationState.Reviewing:
                    var proposal = _creation.Proposal;
                    _proposed = null;
                    if (proposal.Status == ProposalStatus.Ready && MovingDayProposals.TryParameters(proposal.Intent, out var parameters))
                    {
                        _proposed = parameters;
                        Draft = parameters;
                    }
                    Mode = AppMode.ReviewingProposal;
                    SetStatus(proposal.Status == ProposalStatus.Ready
                        ? "Review the proposed room. Confirm to build it, or adjust it with hand controls."
                        : proposal.Status == ProposalStatus.NeedsClarification
                            ? "One more detail is needed. Choose an option."
                            : "That request is outside what this build supports. Nothing was changed.");
                    break;
                case CreationState.Failed:
                    var voice = _creation.TranscriptFromVoice;
                    Mode = voice ? AppMode.RecordingVoice : AppMode.DescribingTyped;
                    SetStatus((voice ? VoiceFailureText(_creation.Failure) : FailureText(_creation.Failure)) +
                              (_session != null ? " Your current scene is unchanged." : ""));
                    break;
                default:
                    Changed();
                    break;
            }
        }

        static string VoiceFailureText(ProposalOutcome failure)
        {
            switch (failure?.DetailCode)
            {
                case "no_speech":
                    return "No words were recognised. Record again, or type the description instead.";
                case "transcription_not_configured":
                case "service_not_configured":
                    return "Voice needs the creation service, which is not configured in this build. Type instead or create with hands.";
                case "payload_too_large":
                case "audio_too_long":
                    return "That recording was too long to send. Record again (15 s maximum).";
            }
            return failure?.Failure == ProposalFailure.Timeout
                ? "Transcription and interpretation took too long and were stopped. Record again, type instead, or create with hands."
                : FailureText(failure);
        }

        static string FailureText(ProposalOutcome failure)
        {
            switch (failure?.Failure)
            {
                case ProposalFailure.Unavailable:
                    return "The creation service is not reachable. Use 'Create with hands' for the same rehearsal.";
                case ProposalFailure.Timeout:
                    return "Interpretation took too long and was stopped. Try again, or create with hands.";
                case ProposalFailure.Rejected:
                    return failure.DetailCode == "transcript_empty" ? "Type or pick a description first."
                        : failure.DetailCode == "transcript_too_long" ? "That description is too long (2000 characters maximum)."
                        : failure.DetailCode == "rate_limited" ? "Too many requests just now. Wait a moment, or create with hands."
                        : "The creation service refused the request. Create with hands instead.";
                case ProposalFailure.InvalidResponse:
                    return "The service returned a scene this app cannot accept, so it was discarded. Create with hands instead.";
                default:
                    return "Interpretation failed. Create with hands instead.";
            }
        }

        /// <summary>
        /// Staged replacement (S05.2): validate, build the new room under staging (the presenter keeps the old room until
        /// that succeeds), then switch the session. On failure nothing changes.
        /// </summary>
        bool CommitScene(SceneSpec scene)
        {
            if (!SceneSession.TryCreate(scene, _catalog, out var core, out var validation))
            {
                SetStatus("The room could not be created (invalid scene). Nothing was changed.");
                Debug.LogError($"[Tryscape] scene failed validation before construction: {string.Join(", ", validation.Errors)}");
                return false;
            }
            if (!Activate(core))
                return false;
            _attempts.Clear(); // a newly created room is a new project
            _projectCreatedAt = null;
            _lastSavedRevision = -1;
            return true;
        }

        /// <summary>Builds the session's current scene under staging, then switches state. On failure nothing changes.</summary>
        bool Activate(SceneSession core)
        {
            var scene = core.Current;
            try
            {
                _presenter.Build(scene, _catalog);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Tryscape] scene construction failed: {e.GetType().Name}");
                SetStatus("The room could not be built. Your previous scene is unchanged.");
                return false;
            }

            CancelDrag();
            _creation.SceneReplaced(); // proposals started or reviewed before this scene can no longer be confirmed
            if (_session != null)
            {
                _session.Changed -= OnSessionChanged;
                _session.Rejected -= OnSessionRejected;
            }
            if (_playback != null) _playback.Stop(); // the previous room's views are already gone; nothing to re-render
            if (_highlight != null) _highlight.Hide();
            _inspecting = null;
            _session = new EditingSession(core, _ids);
            _session.Changed += OnSessionChanged;
            _session.Rejected += OnSessionRejected;
            _rehearsal = new RehearsalSession(_session, _ids, _clock);
            _showDetails = false;
            _drag = new DragController(_session);
            _selected = Guid.Empty;
            foreach (var view in _presenter.Views.Values)
            {
                var adapter = view.GetComponent<BlockerDragAdapter>();
                if (adapter != null)
                    adapter.Attach(this);
            }
            Mode = AppMode.Editing;
            return true;
        }

        // ---- Attempts and comparison (D03.6, S09) ----

        /// <summary>Retains the latest run as Attempt A or B. The snapshot is immutable; later edits cannot change it.</summary>
        public SavedAttempt KeepAttempt(AttemptSlot slot)
        {
            if (!EnsureEditing())
                return null;
            var run = LatestRun;
            if (run == null)
            {
                SetStatus("Rehearse first, then keep the result as an attempt.");
                return null;
            }
            var kept = _attempts.Keep(slot, run);
            SetStatus($"Kept as {kept.Label}: revision {run.Evidence.SceneRevision}, {run.Route?.Label ?? run.Evidence.RouteId}, " +
                      $"{AttemptComparison.OutcomeWords(run.Evidence.Outcome).ToLowerInvariant()}" +
                      (IsResultStale ? " (this result is out of date for the current scene)." : "."));
            return kept;
        }

        public ComparisonReport Comparison =>
            _attempts.BothRetained ? AttemptComparison.Compare(_attempts.A, _attempts.B) : null;

        public void Compare()
        {
            if (_session == null)
            {
                SetStatus("Create or open a rehearsal first.");
                return;
            }
            if (!_attempts.BothRetained)
            {
                SetStatus(_attempts.A == null
                    ? "Rehearse, then press Keep as A. Change the arrangement, rehearse again and press Keep as B."
                    : "Change the arrangement, rehearse again, then press Keep as B.");
                return;
            }
            CancelDrag();
            StopReplay();
            Mode = AppMode.Comparing;
            SetStatus("Comparing Attempt A and Attempt B. Inspect either one on the tabletop; the live scene is unchanged.");
        }

        /// <summary>Shows an attempt's own scene snapshot (read-only) with its evaluated highlight.</summary>
        public void InspectAttempt(AttemptSlot slot)
        {
            var attempt = _attempts.Get(slot);
            if (Mode != AppMode.Comparing || attempt == null)
                return;
            if (_playback != null) _playback.Stop();
            _inspecting = slot;
            _presenter.Render(attempt.Run.Scene);
            if (_highlight != null)
            {
                if (attempt.Run.Evidence.FirstViolation != null)
                    _highlight.Show(attempt.Run, _catalog);
                else
                    _highlight.Hide();
                var other = _attempts.Get(slot == AttemptSlot.A ? AttemptSlot.B : AttemptSlot.A);
                if (other != null && other.Run.Scene.SceneId == attempt.Run.Scene.SceneId)
                    _highlight.ShowGhosts(attempt.Run.Scene, other.Run.Scene, _catalog, other.Label);
            }
            SetStatus($"Inspecting {attempt.Label} (revision {attempt.Run.Evidence.SceneRevision}, read-only). Replay plays this attempt.");
        }

        /// <summary>Replays the inspected attempt from its own timeline (the existing playback controller).</summary>
        public void ReplayInspected()
        {
            if (_inspecting == null)
            {
                SetStatus("Inspect Attempt A or Attempt B first.");
                return;
            }
            var run = _attempts.Get(_inspecting.Value).Run;
            if (!run.CanReplay || !_presenter.Views.TryGetValue(run.Route.PayloadEntityId, out var view))
            {
                SetStatus("This attempt cannot be replayed.");
                return;
            }
            if (_highlight != null) _highlight.Hide();
            _playback.Begin(run.CreateTimeline(), view.transform, run.Scene.Room);
            Changed();
        }

        public void InspectLive()
        {
            if (_inspecting == null)
                return;
            _inspecting = null;
            if (_playback != null) _playback.Stop();
            if (_highlight != null)
            {
                _highlight.Hide();
                _highlight.HideGhosts();
            }
            if (_session != null)
                _presenter.Render(_session.Current);
            Changed();
        }

        public void LeaveComparison()
        {
            InspectLive();
            if (Mode == AppMode.Comparing)
            {
                Mode = AppMode.Editing;
                SetStatus("Back to the live scene.");
            }
        }

        /// <summary>Comparison card: both attempts, then changes, explanation and notes (model wording only).</summary>
        public string ComparisonText
        {
            get
            {
                var report = Comparison;
                if (report == null)
                    return "";
                var t = new StringBuilder();
                foreach (var s in new[] { report.A, report.B })
                    t.Append(s.Label).Append(": rev ").Append(s.Revision).Append(", ").Append(s.RouteLabel).Append(", ")
                        .Append(s.OutcomeText).Append(s.RouteLengthMm.HasValue ? $", {s.RouteLengthMm.Value / 1000.0:0.00} m route" : "")
                        .Append(s.ObstructionLabel != null && s.Outcome == EvaluationOutcome.BlockedInModel ? $" (obstruction: {s.ObstructionLabel})" : "")
                        .Append('\n');
                if (!report.Comparable)
                    t.Append(report.IncompatibleText).Append('\n');
                else
                {
                    foreach (var c in report.Changes)
                        t.Append("Changed: ").Append(c).Append('\n');
                    t.Append(report.Explanation).Append('\n');
                    if (report.LengthDifferenceMm.HasValue && report.LengthDifferenceMm.Value != 0)
                        t.Append($"Route length differs by {Math.Abs(report.LengthDifferenceMm.Value) / 1000.0:0.00} m (a model calculation, not travel time or effort).\n");
                    if (report.Preparation != null)
                        t.Append("Preparation: ").Append(report.Preparation).Append('\n');
                }
                foreach (var n in report.Notes)
                    t.Append(n).Append('\n');
                t.Append(Explanation.Scope);
                return t.ToString();
            }
        }

        // ---- Local projects (S12) ----

        public string ProjectTitle =>
            _session == null ? "" : $"Moving Day {_session.Current.Room.WidthMm / 1000.0:0.0} x {_session.Current.Room.DepthMm / 1000.0:0.0} m";

        string Now() => _clock.UtcNow.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        /// <summary>Saves committed state, the creation baseline, the retained attempts and the tabletop zoom.</summary>
        public StoreResult<ProjectListing> SaveProject()
        {
            if (_session == null)
            {
                SetStatus("Create or open a rehearsal before saving.");
                return null;
            }
            var now = Now();
            var snapshot = new ProjectSnapshot(_session.Current.ProjectId, ProjectTitle, _projectCreatedAt ?? now, now,
                _session.Confirmed, _session.Current, _tabletop != null ? _tabletop.ZoomStep : 2,
                _attempts.A?.Run, _attempts.B?.Run, _tabletop != null ? _tabletop.YawDeg : 0);
            var result = _store.Save(snapshot);
            if (result.Succeeded)
            {
                _projectCreatedAt = snapshot.CreatedAt;
                _lastSavedRevision = _session.Current.Revision;
                _savedAttemptsVersion = _attemptsVersion;
                _resumable = result.Value;
                SetStatus($"Saved \"{snapshot.Title}\" on this device (revision {_session.Current.Revision}, " +
                          $"{snapshot.Runs.Count()} attempt{(snapshot.Runs.Count() == 1 ? "" : "s")}).");
            }
            else
                SetStatus(StoreText(result.Error, result.Detail) + " Your scene is unchanged.");
            return result;
        }

        public void OpenProjects()
        {
            CancelDrag();
            if (_playback != null) _playback.Pause();
            _recorder?.Cancel();
            _creation?.Cancel();
            InspectLive();
            Mode = AppMode.Projects;
            _deleteArmed = false;
            var list = _store.List();
            _projects = list.Succeeded ? list.Value : Array.Empty<ProjectListing>();
            _projectIndex = 0;
            SetStatus(!list.Succeeded ? StoreText(list.Error, list.Detail)
                : _projects.Count == 0 ? "No saved projects on this device yet."
                : $"{_projects.Count} saved project{(_projects.Count == 1 ? "" : "s")}. Choose one to open or delete.");
        }

        public void SelectProject(int delta)
        {
            if (Mode != AppMode.Projects || _projects.Count == 0)
                return;
            _projectIndex = (_projectIndex + delta + _projects.Count) % _projects.Count;
            _deleteArmed = false;
            Changed();
        }

        public string ProjectsText
        {
            get
            {
                if (Mode != AppMode.Projects)
                    return "";
                if (_projects.Count == 0)
                    return "No saved projects. Create a rehearsal and press Save.";
                var p = _projects[_projectIndex];
                return $"{_projectIndex + 1} of {_projects.Count}\n{p.Title}\nSaved {p.UpdatedAt.Replace('T', ' ').TrimEnd('Z')} UTC\n" +
                       $"{p.RunCount} saved attempt{(p.RunCount == 1 ? "" : "s")}" +
                       (_deleteArmed ? "\nPress Delete again to remove this project and its attempts from this device." : "");
            }
        }

        /// <summary>
        /// Opens the selected project. Everything is validated before the current scene is replaced; on any failure the
        /// current project stays exactly as it was.
        /// </summary>
        public bool OpenSelectedProject()
        {
            if (Mode != AppMode.Projects || _projects.Count == 0)
                return false;
            return OpenProject(_projects[_projectIndex].ProjectId);
        }

        bool OpenProject(Guid projectId)
        {
            var loaded = _store.Load(projectId);
            if (!loaded.Succeeded)
            {
                SetStatus(StoreText(loaded.Error, loaded.Detail) + (_session != null ? " Your current scene is unchanged." : "") +
                          " You can still create the example with hands.");
                return false;
            }
            var p = loaded.Value;
            if (!SceneSession.TryRestore(p.ConfirmedScene, p.CurrentScene, _catalog, out var core, out _))
            {
                SetStatus("That project could not be restored (its scenes did not validate). Nothing was changed.");
                return false;
            }
            _creation.SceneReplaced();
            if (!Activate(core))
                return false;
            _attempts.Restore(p.AttemptA, p.AttemptB);
            _projectCreatedAt = p.CreatedAt;
            if (_tabletop != null)
            {
                _tabletop.SetZoomStep(p.TabletopZoomStep);
                _tabletop.SetYaw(p.TabletopYawDeg);
            }
            _lastSavedRevision = p.CurrentScene.Revision;
            _savedAttemptsVersion = _attemptsVersion;
            SetStatus($"Opened \"{p.Title}\" at revision {p.CurrentScene.Revision}" +
                      (loaded.Recovered ? " from a verified backup copy (the newest file was damaged)" : "") +
                      (loaded.Detail != null && loaded.Detail.StartsWith("migrated", StringComparison.Ordinal) ? " (upgraded from an older save format; the original is kept until you save)" : "") +
                      $". {p.Runs.Count()} saved attempt{(p.Runs.Count() == 1 ? "" : "s")}. Undo history starts fresh.");
            return true;
        }

        /// <summary>Two presses: the first arms, the second deletes the project and all its local copies.</summary>
        public void DeleteSelectedProject()
        {
            if (Mode != AppMode.Projects || _projects.Count == 0)
                return;
            if (!_deleteArmed)
            {
                _deleteArmed = true;
                SetStatus("Press Delete again to confirm. This removes the project and its saved attempts from this device.");
                return;
            }
            var target = _projects[_projectIndex];
            var result = _store.Delete(target.ProjectId);
            _deleteArmed = false;
            var list = _store.List();
            _projects = list.Succeeded ? list.Value : Array.Empty<ProjectListing>();
            _projectIndex = Math.Min(_projectIndex, Math.Max(0, _projects.Count - 1));
            SetStatus(result.Succeeded ? $"Deleted \"{target.Title}\" from this device." : StoreText(result.Error, result.Detail));
        }

        static string StoreText(StoreError error, string detail)
        {
            switch (error)
            {
                case StoreError.NotFound: return "That project is no longer on this device.";
                case StoreError.Corrupt: return "That project's files are damaged and no valid backup was found, so it was not opened.";
                case StoreError.UnsupportedVersion: return "That project was saved by a newer version of Tryscape; it is kept unchanged and cannot be opened here.";
                case StoreError.IncompatibleContent: return "That project uses a scenario or catalog version this app does not have.";
                case StoreError.QuotaExceeded:
                    return detail == "too_many_projects"
                        ? "Storage limit reached (20 projects). Delete a project to save a new one; nothing was deleted automatically."
                        : "Local storage limit reached. Delete a project to make room; nothing was deleted automatically.";
                case StoreError.TooLarge: return "This project is too large to save (1 MiB limit per file).";
                default: return "The device refused to read or write the project files.";
            }
        }

        // ---- Editing (hands) ----

        public void Select(Guid entityId)
        {
            if (_session == null)
                return;
            var entity = _session.Current.FindEntity(entityId);
            if (entity == null)
                return;
            SetSelectedVisual(_selected, false);
            _selected = entityId;
            SetSelectedVisual(_selected, true);
            SetStatus(entity.Editable
                ? $"Selected: {entity.Label}. Grab to move, or rotate a quarter turn."
                : $"Selected: {entity.Label} ({RoleText(entity.Role)}; not movable).");
        }

        public CommandResult RotateSelected()
        {
            if (!EnsureEditing())
                return null;
            if (_selected == Guid.Empty || !IsEditable(_selected))
            {
                var single = SingleEditable();
                if (single == Guid.Empty)
                {
                    SetStatus("Select a movable object first.");
                    return null;
                }
                Select(single);
            }
            return _session.RotateQuarterTurn(_selected);
        }

        bool IsEditable(Guid id) => _session.Current.FindEntity(id)?.Editable == true;

        Guid SingleEditable()
        {
            var found = Guid.Empty;
            foreach (var e in _session.Current.Entities)
                if (e.Editable)
                {
                    if (found != Guid.Empty)
                        return Guid.Empty;
                    found = e.Id;
                }
            return found;
        }

        public CommandResult ToggleRoute()
        {
            if (!EnsureEditing())
                return null;
            var next = _session.Current.ActiveRouteId == MovingDayPack.DirectRouteId
                ? MovingDayPack.NorthRouteId
                : MovingDayPack.DirectRouteId;
            return _session.SelectRoute(next);
        }

        // ---- Rehearsal (local evaluation; no network or provider) ----

        /// <summary>Evaluates the live scene's active route, then replays it. The result is final before playback.</summary>
        public RehearsalRun Rehearse()
        {
            if (!EnsureEditing())
                return null;
            CancelDrag();
            StopReplay();
            var run = _rehearsal.Rehearse();
            if (run.CanReplay && _presenter.Views.TryGetValue(run.Route.PayloadEntityId, out var payloadView))
                _playback.Begin(run.CreateTimeline(), payloadView.transform, run.Scene.Room);
            var explanation = Explanation.For(run);
            SetStatus($"{explanation.Headline}. {(run.CanReplay ? "Replaying at an illustrative speed." : explanation.Action)}");
            return run;
        }

        /// <summary>Pause, resume, or replay the latest current run from the start.</summary>
        public void TogglePlayback()
        {
            if (!EnsureEditing())
                return;
            var run = LatestRun;
            if (run == null || IsResultStale || !run.CanReplay)
            {
                SetStatus(run == null ? "Rehearse first." : IsResultStale ? "The scene changed. Rehearse again to replay." : "This result cannot be replayed.");
                return;
            }
            if (!_playback.HasTimeline && _presenter.Views.TryGetValue(run.Route.PayloadEntityId, out var view))
            {
                _playback.Begin(run.CreateTimeline(), view.transform, run.Scene.Room);
                if (_highlight != null) _highlight.Hide();
            }
            else if (_playback.IsPlaying)
                _playback.Pause();
            else
            {
                if (_playback.IsFinished)
                    if (_highlight != null) _highlight.Hide();
                _playback.Resume();
            }
            Changed();
        }

        public void ToggleDetails()
        {
            _showDetails = !_showDetails;
            Changed();
        }

        /// <summary>Result card text: explanation layers, stale indicator and (optionally) model details.</summary>
        public string ResultText
        {
            get
            {
                var run = LatestRun;
                if (run == null)
                    return _session == null ? "" : "Not rehearsed yet. Press Rehearse to check the selected route.";
                var e = Explanation.For(run);
                var text = new StringBuilder();
                text.Append(run.Scene.Provenance == Provenance.UserMeasured ? "User-confirmed dimensions. " : "Example environment. ");
                if (IsResultStale)
                    text.Append($"OUT OF DATE - result for revision {run.Evidence.SceneRevision}; scene is now revision {_session.Current.Revision}. Rehearse again.\n");
                text.Append(e.Headline).Append(" - ").Append(run.Route?.Label ?? run.Evidence.RouteId).Append('\n');
                text.Append(e.Observed).Append(' ').Append(e.Reason).Append('\n');
                text.Append(e.Action).Append('\n');
                if (_showDetails)
                    text.Append($"Model margin {Tryscape.Core.Validation.ModelParameters.PayloadMarginMm} mm per side. {Explanation.Scope} " +
                                $"Playback speed is illustrative. Evaluator {run.Evidence.EvaluatorVersion}, revision {run.Evidence.SceneRevision}, " +
                                $"scene {run.Evidence.SceneHash?.Substring(0, 8) ?? "n/a"}.");
                else
                    text.Append(Explanation.Scope);
                return text.ToString();
            }
        }

        void OnPlaybackFinished()
        {
            var run = LatestRun;
            if (run != null && !IsResultStale && run.Evidence.FirstViolation != null)
                if (_highlight != null) _highlight.Show(run, _catalog);
            Changed();
        }

        void StopReplay()
        {
            var hadTimeline = _playback != null && _playback.HasTimeline;
            if (_playback != null) _playback.Stop();
            if (_highlight != null) _highlight.Hide();
            if (hadTimeline && _session != null)
                _presenter.Render(_session.Current); // put the payload back at its committed start
        }

        public CommandResult Undo() => EnsureEditing() ? _session.Undo() : null;
        public CommandResult Redo() => EnsureEditing() ? _session.Redo() : null;
        public CommandResult ResetScene() => EnsureEditing() ? _session.Reset() : null;

        // ---- IDragHost ----

        public void OnDragStarted(EntityView view)
        {
            StopReplay();
            Select(view.EntityId);
        }

        public void OnDragPreview(EntityView view, DragPreview preview)
        {
            if (_ghost != null && _session != null)
                _ghost.Show(preview, view.Entry, _session.Current.Room);
        }

        public void OnDragFinished(EntityView view, DragOutcome outcome)
        {
            if (_ghost != null)
                _ghost.Hide();
            if (_session != null)
                _presenter.Render(_session.Current); // snap the preview back to the committed model pose
            switch (outcome.Kind)
            {
                case DragOutcomeKind.Committed:
                    SetStatus($"Moved {view.Label}. Revision {_session.Current.Revision}.");
                    break;
                case DragOutcomeKind.Rejected:
                    SetStatus($"Not placed: {PlacementGhost.Reason(outcome.Error)}. Returned to its previous position.");
                    break;
                case DragOutcomeKind.Cancelled:
                    SetStatus($"Move cancelled; {view.Label} is unchanged.");
                    break;
            }
        }

        // ---- Lifecycle ----

        void OnApplicationPause(bool paused)
        {
            if (paused)
                Suspend();
        }

        void OnApplicationFocus(bool focused)
        {
            if (!focused)
                Suspend();
        }

        void OnApplicationQuit() => AutosaveCommitted();

        /// <summary>
        /// S05.3: persist committed state before suspension where possible. Only committed scene state and retained
        /// attempts are saved, never a drag preview, and only when something changed since the last save. A failure, such
        /// as quota or I/O, is reported and changes nothing.
        /// </summary>
        public bool AutosaveCommitted()
        {
            if (_session == null || _store == null || Mode == AppMode.Comparing && _inspecting != null)
                return false;
            if (_session.Current.Revision == _lastSavedRevision && _attemptsVersion == _savedAttemptsVersion)
                return false;
            var result = SaveProject();
            return result != null && result.Succeeded;
        }

        /// <summary>Focus loss or pause (S05.3): discard drag previews and cancel in-flight interpretation.</summary>
        public void Suspend()
        {
            CancelDrag();
            if (_playback != null) _playback.Pause();
            AutosaveCommitted();
            if (_recorder != null)
                _recorder.Suspend(); // stops the microphone immediately and drops the audio (S05.3)
            if (_creation != null && _creation.IsBusy)
            {
                _creation.Suspend();
                SetStatus("Transcription/interpretation was cancelled because the app lost focus. Nothing was changed.");
            }
        }

        void CancelDrag()
        {
            if (_drag != null && _drag.State != DragState.Idle)
            {
                _drag.Cancel();
                if (_ghost != null)
                    _ghost.Hide();
                if (_session != null)
                    _presenter.Render(_session.Current);
            }
        }

        void OnSessionChanged(SceneSpec scene, CommandResult result)
        {
            StopReplay();
            _presenter.Render(scene);
            SetStatus(DescribeAccepted(scene) + (LatestRun != null ? " The last result is out of date." : ""));
        }

        void OnSessionRejected(SceneCommand command, CommandResult result)
        {
            SetStatus(command is UndoCommand ? "Nothing to undo."
                : command is RedoCommand ? "Nothing to redo."
                : $"Not applied: {PlacementGhost.Reason(result.ErrorCode)}.");
        }

        string DescribeAccepted(SceneSpec scene)
        {
            var route = scene.FindRoute(scene.ActiveRouteId);
            return $"Revision {scene.Revision}. Route: {route?.Label ?? scene.ActiveRouteId}.";
        }

        bool EnsureEditing()
        {
            if (_session != null && Mode == AppMode.Editing)
                return true;
            SetStatus("Create a rehearsal first.");
            return false;
        }

        void SetDraft(MovingDayParameters draft)
        {
            Draft = draft;
            Changed();
        }

        void SetStatus(string status)
        {
            Status = status;
            Changed();
        }

        void Changed() => StateChanged?.Invoke();

        void SetSelectedVisual(Guid id, bool selected)
        {
            if (id != Guid.Empty && _presenter.Views.TryGetValue(id, out var view))
                view.SetSelected(selected);
        }

        static int Clamp(int mm) => Math.Max(RoomSpec.MinDimensionMm, Math.Min(RoomSpec.MaxDimensionMm, mm));

        static string RoleText(EntityRole role) =>
            role == EntityRole.Payload ? "delivery item" : role == EntityRole.Blocker ? "obstacle" : "decoration";
    }

    /// <summary>PlayerPrefs wrapper for per-device UI flags only (never project data); failures fall back to defaults.</summary>
    static class SafePrefs
    {
        public static bool GetBool(string key)
        {
            try
            {
                return PlayerPrefs.GetInt(key, 0) == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void SetBool(string key, bool value)
        {
            try
            {
                PlayerPrefs.SetInt(key, value ? 1 : 0);
                PlayerPrefs.Save();
            }
            catch (Exception)
            {
                // A UI preference that cannot be stored simply shows again next time.
            }
        }
    }
}
