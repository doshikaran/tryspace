using System;
using System.Collections.Generic;
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
        public const string VoiceUnavailable =
            "Voice creation needs the Tryscape creation service, which is not configured in this build. " +
            "Use 'Create with hands' - it builds the same rehearsal.";

        public const string SavedUnavailable = "Saved projects are not available in this build yet.";

        public const string HelpText =
            "Grab the console table with a pinch or ray to move it. Release on a clear spot to place it. " +
            "Results are illustrative: floor footprint only; height, turning and lifting are not assessed.";

        [SerializeField] GameObject _welcomeSection;
        [SerializeField] GameObject _createSection;
        [SerializeField] GameObject _editSection;
        [SerializeField] Text _status;
        [SerializeField] Text _draftSummary;
        [SerializeField] Text _sceneSummary;
        [SerializeField] List<Button> _buttons = new List<Button>();

        TryscapeApp _app;
        readonly Dictionary<string, Button> _byName = new Dictionary<string, Button>(StringComparer.Ordinal);

        public void Configure(GameObject welcome, GameObject create, GameObject edit, Text status, Text draftSummary,
            Text sceneSummary, IEnumerable<Button> buttons)
        {
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
            Refresh();
        }

        void OnDestroy()
        {
            if (_app != null)
                _app.StateChanged -= Refresh;
        }

        /// <summary>Dispatch by button ID. Public so PlayMode tests exercise the same path as a hand press.</summary>
        public void Press(string id)
        {
            switch (id)
            {
                case "CreateWithHands": _app.BeginHandCreation(); break;
                case "SpeakSituation": ShowMessage(VoiceUnavailable); break;
                case "OpenSaved": ShowMessage(SavedUnavailable); break;
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
                case "BackToWelcome": _app.ShowWelcome(); break;
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
            SetActive(_createSection, mode == AppMode.CreatingWithHands);
            SetActive(_editSection, mode == AppMode.Editing);
            if (_status != null)
                _status.text = _app.Status;

            var d = _app.Draft;
            if (_draftSummary != null)
                _draftSummary.text =
                    $"Room {d.RoomWidthMm / 1000.0:0.0} m wide x {d.RoomDepthMm / 1000.0:0.0} m deep (5-8 m)\n" +
                    $"Console: {(d.Obstruction == ObstructionState.Center ? "in the way" : "parked south")}  |  " +
                    $"Route: {(d.RouteId == MovingDayPack.DirectRouteId ? "Direct" : "Northern")}\n" +
                    "Example environment - not measurements of your home.";

            var session = _app.Session;
            if (_sceneSummary != null)
                _sceneSummary.text = session == null ? "" :
                    $"Revision {session.Current.Revision}  |  Route: {session.Current.FindRoute(session.Current.ActiveRouteId)?.Label}";
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
