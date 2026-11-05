using System;
using System.Text;
using Tryscape.Application.Editing;
using Tryscape.Application.Rehearsal;
using Tryscape.Content.MovingDay;
using Tryscape.Core.Commands;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Model;
using Tryscape.Unity.Experience;
using Tryscape.Unity.Presentation;
using UnityEngine;

namespace Tryscape.Unity
{
    public enum AppMode
    {
        Welcome,
        CreatingWithHands,
        Editing,
    }

    /// <summary>
    /// Composition root (S04). Owns the pure-core session and wires it to presentation and input. Holds no domain
    /// rules itself: every committed change goes through <see cref="EditingSession"/> commands.
    /// Scope: hands-only creation from the Moving Day template, select, drag, rotate, route choice, undo/redo/reset,
    /// tabletop zoom/recenter (Phase 1), and local rehearsal: evaluate the active route with the core evaluator,
    /// replay it, highlight the modelled contact, explain it and mark it stale after edits (Phase 2). Voice creation,
    /// comparison and saving are not implemented yet.
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

        public DragController Drag => _drag;
        public RehearsalSession Rehearsal => _rehearsal;
        public RehearsalRun LatestRun => _rehearsal?.Latest;
        public bool IsResultStale => _rehearsal != null && _rehearsal.IsLatestStale;
        public PlaybackController Playback => _playback;
        public ResultHighlight Highlight => _highlight;
        public bool ShowingDetails => _showDetails;
        public RoomSpec Room => _session?.Current.Room;

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
        }

        void Start()
        {
            if (_panel != null)
                _panel.Bind(this);
            SetStatus("Welcome. Choose how to create a rehearsal.");
        }

        // ---- Creation (hands-only path) ----

        public void BeginHandCreation()
        {
            Mode = AppMode.CreatingWithHands;
            Draft = MovingDayParameters.Example;
            SetStatus("Example values shown. Adjust them or create the example room.");
        }

        public void ShowWelcome()
        {
            CancelDrag();
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

        /// <summary>Confirms the reviewed draft: staged construction, then the session switches atomically.</summary>
        public bool ConfirmCreation()
        {
            var problem = Draft.Problem();
            if (problem != null)
            {
                SetStatus($"Cannot create: {problem}");
                return false;
            }
            var scene = MovingDayTemplate.Assemble(Draft, _ids);
            if (!SceneSession.TryCreate(scene, _catalog, out var core, out var validation))
            {
                SetStatus("The example could not be created (invalid scene). Nothing was changed.");
                Debug.LogError($"[Tryscape] template produced invalid scene: {string.Join(", ", validation.Errors)}");
                return false;
            }
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
            if (_session != null)
            {
                _session.Changed -= OnSessionChanged;
                _session.Rejected -= OnSessionRejected;
            }
            if (_playback != null) _playback.Stop(); // the previous room's views are already gone; nothing to re-render
            if (_highlight != null) _highlight.Hide();
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
            SetStatus($"Created a {scene.Room.WidthMm / 1000.0:0.0#} x {scene.Room.DepthMm / 1000.0:0.0#} m example room. " +
                      "Press Rehearse to check the route, or grab the console table to move it.");
            return true;
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
                CancelDrag();
        }

        void OnApplicationFocus(bool focused)
        {
            if (!focused)
                CancelDrag();
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
}
