using System;
using System.Collections.Generic;
using Tryscape.Application.Creation;
using Tryscape.Content.MovingDay;
using UnityEngine;
using UnityEngine.UI;

namespace Tryscape.Unity.Experience
{
    /// <summary>
    /// Hand-selectable panel (ray and poke via Interaction SDK canvas interaction). Buttons are identified by their
    /// GameObject name; every action is dispatched to <see cref="TryscapeApp"/>. Controls exist only for implemented
    /// capabilities; unavailable features say so plainly rather than pretending to work.
    /// </summary>
    public sealed class PanelController : MonoBehaviour
    {

        /// <summary>Development presets: typed transcripts selectable by hand (no keyboard needed). Not speech.</summary>
        public static readonly string[] PresetTranscripts =
        {
            "Prepare a six by five metre living room for a sofa delivery. There is a console table in the way.",
            "Seven by six metres, sofa delivery, the console is parked south. Take the northern approach.",
            "Prepare my room for a delivery",
            "Scan my floor plan PDF and predict where the movers will go",
        };

        public const string HelpText =
            "Grab the console table with a pinch or ray to move it. Release on a clear spot to place it. " +
            "Rehearse checks the selected route in the model and replays it. " +
            "Results are illustrative: floor footprint only; height, turning and lifting are not assessed.";

        [SerializeField] GameObject _welcomeSection;
        [SerializeField] GameObject _createSection;
        [SerializeField] GameObject _editSection;
        [SerializeField] Text _status;
        [SerializeField] Text _draftSummary;
        [SerializeField] Text _sceneSummary;
        [SerializeField] Text _resultCard;
        [SerializeField] GameObject _describeSection;
        [SerializeField] GameObject _reviewSection;
        [SerializeField] Text _reviewCard;
        [SerializeField] InputField _transcriptInput;
        [SerializeField] GameObject _voiceSection;
        [SerializeField] Text _voiceCard;
        [SerializeField] GameObject _compareSection;
        [SerializeField] Text _compareCard;
        [SerializeField] GameObject _projectsSection;
        [SerializeField] Text _projectsCard;
        [SerializeField] GameObject _settingsSection;
        [SerializeField] Text _settingsCard;
        [SerializeField] List<Button> _buttons = new List<Button>();

        TryscapeApp _app;
        readonly Dictionary<string, Button> _byName = new Dictionary<string, Button>(StringComparer.Ordinal);

        public void Configure(GameObject welcome, GameObject create, GameObject edit, Text status, Text draftSummary,
            Text sceneSummary, IEnumerable<Button> buttons, Text resultCard = null, GameObject describe = null,
            GameObject review = null, Text reviewCard = null, InputField transcriptInput = null, GameObject voice = null,
            Text voiceCard = null, GameObject compare = null, Text compareCard = null, GameObject projects = null,
            Text projectsCard = null, GameObject settings = null, Text settingsCard = null)
        {
            _settingsSection = settings;
            _settingsCard = settingsCard;
            _compareSection = compare;
            _compareCard = compareCard;
            _projectsSection = projects;
            _projectsCard = projectsCard;
            _voiceSection = voice;
            _voiceCard = voiceCard;
            _describeSection = describe;
            _reviewSection = review;
            _reviewCard = reviewCard;
            _transcriptInput = transcriptInput;
            _resultCard = resultCard;
            _welcomeSection = welcome;
            _createSection = create;
            _editSection = edit;
            _status = status;
            _draftSummary = draftSummary;
            _sceneSummary = sceneSummary;
            _buttons = new List<Button>(buttons);
        }

        public void Bind(TryscapeApp app)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _byName.Clear();
            foreach (var button in _buttons)
            {
                if (button == null)
                    continue;
                var id = button.name;
                _byName[id] = button;
                button.onClick.AddListener(() => Press(id));
            }
            _app.StateChanged += Refresh;
            if (_transcriptInput != null)
                _transcriptInput.onValueChanged.AddListener(OnTranscriptEdited);
            Refresh();
        }

        void OnDestroy()
        {
            if (_app != null)
                _app.StateChanged -= Refresh;
            if (_transcriptInput != null)
                _transcriptInput.onValueChanged.RemoveListener(OnTranscriptEdited);
        }

        void OnTranscriptEdited(string text)
        {
            if (_app != null && text != _app.TypedTranscript)
                _app.SetTypedTranscript(text);
        }

        /// <summary>Dispatch by button ID. Public so PlayMode tests exercise the same path as a hand press.</summary>
        public void Press(string id)
        {
            switch (id)
            {
                case "CreateWithHands": _app.BeginHandCreation(); break;
                case "SpeakSituation": _app.BeginVoice(); break;
                case "VoiceAllow": _app.AllowVoice(); break;
                case "VoiceStop": _app.StopRecording(); break;
                case "VoiceCancel": _app.CancelVoice(); break;
                case "VoiceRetry": _app.BeginVoice(); break;
                case "VoiceTypeInstead": _app.BeginTypedDescription(); break;
                case "VoiceHands": _app.BeginHandCreation(); break;
                case "VoiceBack": _app.ShowWelcome(); break;
                case "EditTranscript": _app.EditTranscript(); break;
                case "OpenSaved": _app.OpenProjects(); break;
                case "KeepA": _app.KeepAttempt(Tryscape.Application.Rehearsal.AttemptSlot.A); break;
                case "KeepB": _app.KeepAttempt(Tryscape.Application.Rehearsal.AttemptSlot.B); break;
                case "Compare": _app.Compare(); break;
                case "Save": _app.SaveProject(); break;
                case "InspectA": _app.InspectAttempt(Tryscape.Application.Rehearsal.AttemptSlot.A); break;
                case "InspectB": _app.InspectAttempt(Tryscape.Application.Rehearsal.AttemptSlot.B); break;
                case "InspectLive": _app.InspectLive(); break;
                case "CompareReplay": _app.ReplayInspected(); break;
                case "CompareBack": _app.LeaveComparison(); break;
                case "ProjectPrev": _app.SelectProject(-1); break;
                case "ProjectNext": _app.SelectProject(+1); break;
                case "ProjectOpen": _app.OpenSelectedProject(); break;
                case "ProjectDelete": _app.DeleteSelectedProject(); break;
                case "ProjectsBack": _app.ShowWelcome(); break;
                case "Settings": _app.OpenSettings(); break;
                case "SettingsBack": _app.CloseSettings(); break;
                case "RotateViewLeft": _app.RotateView(-1); break;
                case "RotateViewRight": _app.RotateView(+1); break;
                case "ResetView": _app.ResetView(); break;
                case "PauseAll": _app.TogglePauseAll(); break;
                case "ReloadSaved": _app.ReloadSaved(); break;
                case "SettingsHelp": ShowMessage(HelpText); break;
                case "ResumeLast": _app.ResumeLast(); break;
                case "OnboardingOk": _app.DismissOnboarding(); break;
                case "Help": ShowMessage(HelpText); break;
                case "WidthMinus": _app.AdjustRoomWidth(-500); break;
                case "WidthPlus": _app.AdjustRoomWidth(500); break;
                case "WidthMinusFine": _app.AdjustRoomWidth(-100); break;
                case "WidthPlusFine": _app.AdjustRoomWidth(100); break;
                case "DepthMinus": _app.AdjustRoomDepth(-500); break;
                case "DepthPlus": _app.AdjustRoomDepth(500); break;
                case "DepthMinusFine": _app.AdjustRoomDepth(-100); break;
                case "DepthPlusFine": _app.AdjustRoomDepth(100); break;
                case "ObstructionCenter": _app.SetDraftObstruction(ObstructionState.Center); break;
                case "ObstructionSouth": _app.SetDraftObstruction(ObstructionState.ParkedSouth); break;
                case "RouteDirect": _app.SetDraftRoute(MovingDayPack.DirectRouteId); break;
                case "RouteNorth": _app.SetDraftRoute(MovingDayPack.NorthRouteId); break;
                case "ConfirmCreate": _app.ConfirmCreation(); break;
                case "BackToWelcome":
                    if (_app.Mode == AppMode.CorrectingProposal) _app.BackToProposalReview();
                    else _app.ShowWelcome();
                    break;
                case "DescribeTyped": _app.BeginTypedDescription(); break;
                case "DescribeBack":
                case "ReviewMenu":
                    _app.ShowWelcome();
                    break;
                case "Preset0":
                case "Preset1":
                case "Preset2":
                case "Preset3":
                    _app.SetTypedTranscript(PresetTranscripts[id[id.Length - 1] - '0']);
                    break;
                case "SubmitTranscript": _app.SubmitTranscript(); break;
                case "CancelProposal": _app.CancelProposal(); break;
                case "Choice0":
                case "Choice1":
                case "Choice2":
                case "Choice3":
                    _app.ChooseClarification(id[id.Length - 1] - '0');
                    break;
                case "ConfirmProposal": _app.ConfirmProposal(); break;
                case "AdjustProposal": _app.BeginProposalCorrection(); break;
                case "DescribeAgain": _app.BeginTypedDescription(); break;
                case "ReviewHands": _app.BeginHandCreation(); break;
                case "Rotate": _app.RotateSelected(); break;
                case "ToggleRoute": _app.ToggleRoute(); break;
                case "Undo": _app.Undo(); break;
                case "Redo": _app.Redo(); break;
                case "Reset": _app.ResetScene(); break;
                case "ZoomIn": _app.Tabletop?.Zoom(+1); break;
                case "ZoomOut": _app.Tabletop?.Zoom(-1); break;
                case "Recenter": _app.Tabletop?.Recenter(); break;
                case "Menu": _app.ShowWelcome(); break;
                case "ResumeEditing": _app.ResumeEditing(); break;
                case "Rehearse": _app.Rehearse(); break;
                case "PlayPause": _app.TogglePlayback(); break;
                case "Details": _app.ToggleDetails(); break;
                default: Debug.LogWarning($"[Tryscape] Unknown panel button '{id}'"); break;
            }
        }

        public bool IsInteractable(string id) => _byName.TryGetValue(id, out var b) && b.interactable && b.gameObject.activeInHierarchy;

        void ShowMessage(string message)
        {
            if (_status != null)
                _status.text = message;
        }

        void Refresh()
        {
            if (_app == null)
                return;
            var mode = _app.Mode;
            SetActive(_welcomeSection, mode == AppMode.Welcome);
            SetActive(_createSection, mode == AppMode.CreatingWithHands || mode == AppMode.CorrectingProposal);
            SetActive(_describeSection, mode == AppMode.DescribingTyped);
            SetActive(_reviewSection, mode == AppMode.ReviewingProposal);
            SetActive(_voiceSection, mode == AppMode.RecordingVoice);
            SetActive(_compareSection, mode == AppMode.Comparing);
            SetActive(_projectsSection, mode == AppMode.Projects);
            SetActive(_settingsSection, mode == AppMode.Settings);
            if (_settingsCard != null)
                _settingsCard.text = mode == AppMode.Settings ? _app.AboutText : "";
            SetVisible("Settings", mode != AppMode.Settings);
            SetVisible("ResumeLast", _app.Resumable != null);
            if (_app.Resumable != null)
                SetLabel("ResumeLast", "Resume " + _app.Resumable.Title);
            SetVisible("OnboardingOk", _app.OnboardingVisible);
            SetInteractable("ReloadSaved", _app.Session != null);
            SetLabel("PauseAll", _app.Playback != null && _app.Playback.IsPlaying ? "Pause replay" : "Resume replay");
            if (_compareCard != null)
                _compareCard.text = mode == AppMode.Comparing ? _app.ComparisonText : "";
            if (_projectsCard != null)
                _projectsCard.text = _app.ProjectsText;
            var attempts = _app.Attempts;
            SetLabel("KeepA", attempts.A == null ? "Keep as A" : "A: rev " + attempts.A.Run.Evidence.SceneRevision);
            SetLabel("KeepB", attempts.B == null ? "Keep as B" : "B: rev " + attempts.B.Run.Evidence.SceneRevision);
            SetInteractable("KeepA", _app.LatestRun != null);
            SetInteractable("KeepB", _app.LatestRun != null);
            SetInteractable("Compare", attempts.BothRetained);
            SetInteractable("InspectA", attempts.A != null && _app.Inspecting != Tryscape.Application.Rehearsal.AttemptSlot.A);
            SetInteractable("InspectB", attempts.B != null && _app.Inspecting != Tryscape.Application.Rehearsal.AttemptSlot.B);
            SetInteractable("InspectLive", _app.Inspecting != null);
            SetInteractable("CompareReplay", _app.Inspecting != null);
            var hasProjects = _app.SavedProjects.Count > 0;
            SetInteractable("ProjectOpen", hasProjects);
            SetInteractable("ProjectDelete", hasProjects);
            SetInteractable("ProjectPrev", _app.SavedProjects.Count > 1);
            SetInteractable("ProjectNext", _app.SavedProjects.Count > 1);
            SetLabel("ProjectDelete", _app.DeleteArmed ? "Confirm delete" : "Delete");
            SetActive(_editSection, mode == AppMode.Editing);
            if (_status != null)
                _status.text = _app.Status;

            var d = _app.Draft;
            if (_draftSummary != null)
                _draftSummary.text =
                    $"Room {d.RoomWidthMm / 1000.0:0.0} m wide x {d.RoomDepthMm / 1000.0:0.0} m deep (5-8 m)\n" +
                    $"Console: {(d.Obstruction == ObstructionState.Center ? "in the way" : "parked south")}  |  " +
                    $"Route: {(d.RouteId == MovingDayPack.DirectRouteId ? "Direct" : "Northern")}\n" +
                    (mode == AppMode.CorrectingProposal
                        ? "From your description, corrected by hand - illustrative, not measurements."
                        : "Example environment - not measurements of your home.");
            SetLabel("ConfirmCreate", mode == AppMode.CorrectingProposal ? "Confirm and build" : "Create example room");
            SetLabel("BackToWelcome", mode == AppMode.CorrectingProposal ? "Review" : "Back");
            RefreshCreation(mode);

            var session = _app.Session;
            if (_sceneSummary != null)
                _sceneSummary.text = session == null ? "" :
                    $"Revision {session.Current.Revision}  |  Route: {session.Current.FindRoute(session.Current.ActiveRouteId)?.Label}";
            if (_resultCard != null)
                _resultCard.text = _app.ResultText;
            SetLabel("Rehearse", _app.LatestRun != null && _app.IsResultStale ? "Rehearse again" : "Rehearse");
            var playback = _app.Playback;
            SetLabel("PlayPause", playback != null && playback.IsPlaying ? "Pause" : playback != null && playback.HasTimeline && !playback.IsFinished ? "Resume" : "Replay");
            SetInteractable("PlayPause", _app.LatestRun != null && !_app.IsResultStale && _app.LatestRun.CanReplay);
            SetLabel("Details", _app.ShowingDetails ? "Less" : "Details");
            SetInteractable("Undo", session != null && session.CanUndo);
            SetInteractable("Redo", session != null && session.CanRedo);
            SetInteractable("ResumeEditing", session != null);
            SetInteractable("WidthMinus", d.RoomWidthMm > 5000);
            SetInteractable("WidthMinusFine", d.RoomWidthMm > 5000);
            SetInteractable("WidthPlus", d.RoomWidthMm < 8000);
            SetInteractable("WidthPlusFine", d.RoomWidthMm < 8000);
            SetInteractable("DepthMinus", d.RoomDepthMm > 5000);
            SetInteractable("DepthMinusFine", d.RoomDepthMm > 5000);
            SetInteractable("DepthPlus", d.RoomDepthMm < 8000);
            SetInteractable("DepthPlusFine", d.RoomDepthMm < 8000);
        }

        void RefreshCreation(AppMode mode)
        {
            var creation = _app.Creation;
            var proposing = creation != null && creation.State == CreationState.Proposing;
            if (_transcriptInput != null && _transcriptInput.text != _app.TypedTranscript)
                _transcriptInput.SetTextWithoutNotify(_app.TypedTranscript);
            SetInteractable("SubmitTranscript", !proposing && !string.IsNullOrWhiteSpace(_app.TypedTranscript));
            SetInteractable("CancelProposal", proposing);
            for (var i = 0; i < PresetTranscripts.Length; i++)
                SetInteractable("Preset" + i, !proposing);

            if (_reviewCard != null)
                _reviewCard.text = mode == AppMode.ReviewingProposal ? _app.ReviewText : "";
            var proposal = mode == AppMode.ReviewingProposal ? creation?.Proposal : null;
            var ready = proposal != null && proposal.Status == ProposalStatus.Ready;
            var question = proposal != null && proposal.Status == ProposalStatus.NeedsClarification ? proposal.Questions[0] : null;
            for (var i = 0; i < 4; i++)
            {
                var id = "Choice" + i;
                var visible = question != null && i < question.Choices.Count;
                SetVisible(id, visible);
                if (visible)
                    SetLabel(id, question.Choices[i]);
            }
            SetVisible("EditTranscript", proposal != null);
            RefreshVoice(mode, creation);
            SetVisible("ConfirmProposal", ready);
            SetVisible("AdjustProposal", ready);
            SetVisible("ReviewHands", proposal != null && !ready);
        }

        void RefreshVoice(AppMode mode, CreationCoordinator creation)
        {
            var recorder = _app.Recorder;
            if (_voiceCard != null)
                _voiceCard.text = _app.VoiceText;
            var inVoice = mode == AppMode.RecordingVoice && recorder != null;
            var consent = inVoice && !_app.AudioConsentGiven;
            var recording = inVoice && (recorder.State == RecorderState.Recording || recorder.State == RecorderState.RequestingPermission);
            var busy = inVoice && creation != null && creation.IsBusy;
            var settled = inVoice && !consent && !recording && !busy;
            SetVisible("VoiceAllow", consent);
            SetVisible("VoiceStop", recording);
            SetInteractable("VoiceStop", recorder != null && recorder.State == RecorderState.Recording);
            SetVisible("VoiceCancel", recording || busy);
            SetVisible("VoiceRetry", settled);
            SetVisible("VoiceTypeInstead", consent || settled);
            SetVisible("VoiceHands", settled);
            SetVisible("VoiceBack", consent || settled);
        }

        void SetVisible(string id, bool visible)
        {
            if (_byName.TryGetValue(id, out var button) && button.gameObject.activeSelf != visible)
                button.gameObject.SetActive(visible);
        }

        /// <summary>Current visible label of a button (tests read what a user would read).</summary>
        public string LabelOf(string id) =>
            _byName.TryGetValue(id, out var b) && b.GetComponentInChildren<Text>(true) is Text t && t != null ? t.text : null;

        void SetLabel(string id, string label)
        {
            if (_byName.TryGetValue(id, out var button))
            {
                var text = button.GetComponentInChildren<Text>(true);
                if (text != null && text.text != label)
                    text.text = label;
            }
        }

        void SetInteractable(string id, bool value)
        {
            if (_byName.TryGetValue(id, out var button))
                button.interactable = value;
        }

        static void SetActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active)
                go.SetActive(active);
        }
    }
}
