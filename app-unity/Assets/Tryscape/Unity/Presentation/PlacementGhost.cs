using Tryscape.Application.Editing;
using Tryscape.Core.Commands;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;
using UnityEngine;

namespace Tryscape.Unity.Presentation
{
    /// <summary>
    /// Floor projection of the snapped preview pose (D07): an outline of the exact footprint plus a text state.
    /// Invalid placement adds diagonal stripes and an explicit reason so the state is never conveyed by colour alone.
    /// </summary>
    public sealed class PlacementGhost : MonoBehaviour
    {
        const float Height = 0.012f;
        const float Border = 0.03f;

        [SerializeField] Material _validMaterial;
        [SerializeField] Material _invalidMaterial;

        Transform[] _edges;
        Transform[] _stripes;
        TextMesh _text;

        public string CurrentText => _text != null ? _text.text : "";
        public bool CurrentlyInvalid { get; private set; }

        public void Configure(Material valid, Material invalid)
        {
            _validMaterial = valid;
            _invalidMaterial = invalid;
        }

        void EnsureBuilt()
        {
            if (_edges != null)
                return;
            _edges = new Transform[4];
            for (var i = 0; i < 4; i++)
                _edges[i] = Bar($"Edge {i}");
            _stripes = new Transform[3];
            for (var i = 0; i < 3; i++)
                _stripes[i] = Bar($"Stripe {i}");
            var label = new GameObject("State");
            label.transform.SetParent(transform, false);
            _text = label.AddComponent<TextMesh>();
            _text.anchor = TextAnchor.LowerCenter;
            _text.characterSize = 0.035f;
            _text.fontSize = 64;
        }

        public void Show(DragPreview preview, CatalogEntry entry, RoomSpec room)
        {
            EnsureBuilt();
            gameObject.SetActive(true);
            var (ex, ez) = Footprints.Extents(entry, preview.Pose.YawDeg);
            var w = ModelSpaceMapping.ToLocalLength(ex);
            var d = ModelSpaceMapping.ToLocalLength(ez);
            var (x, z) = ModelSpaceMapping.ToLocal(room, preview.Pose.XMm, preview.Pose.ZMm);
            transform.localPosition = new Vector3(x, 0f, z);
            transform.localRotation = Quaternion.identity; // extents already account for yaw

            var material = preview.IsValid ? _validMaterial : _invalidMaterial;
            Place(_edges[0], new Vector3(0, Height, d / 2f), new Vector3(w, Height, Border), material);
            Place(_edges[1], new Vector3(0, Height, -d / 2f), new Vector3(w, Height, Border), material);
            Place(_edges[2], new Vector3(w / 2f, Height, 0), new Vector3(Border, Height, d), material);
            Place(_edges[3], new Vector3(-w / 2f, Height, 0), new Vector3(Border, Height, d), material);

            var diagonal = Mathf.Sqrt(w * w + d * d);
            var angle = Mathf.Atan2(d, w) * Mathf.Rad2Deg;
            for (var i = 0; i < _stripes.Length; i++)
            {
                var stripe = _stripes[i];
                stripe.gameObject.SetActive(!preview.IsValid);
                var offset = (i - 1) * 0.3f;
                stripe.localPosition = new Vector3(offset * w * 0.5f, Height, -offset * d * 0.5f);
                stripe.localRotation = Quaternion.Euler(0, -angle, 0);
                stripe.localScale = new Vector3(diagonal * (1f - Mathf.Abs(offset)), Height, Border);
                stripe.GetComponent<Renderer>().sharedMaterial = _invalidMaterial;
            }

            CurrentlyInvalid = !preview.IsValid;
            _text.text = preview.Frozen ? "|| Hand lost - paused" :
                preview.IsValid ? "OK  Release to place" : "X  " + Reason(preview.PlacementError);
            _text.color = preview.IsValid ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.55f, 0.5f);
            _text.transform.localPosition = new Vector3(0, 0.25f, 0);
        }

        public void Hide()
        {
            gameObject.SetActive(false);
        }

        public static string Reason(CommandErrorCode code)
        {
            switch (code)
            {
                case CommandErrorCode.OverlapsPayloadStart: return "Overlaps the sofa's starting position";
                case CommandErrorCode.OverlapsBlocker: return "Overlaps another object";
                case CommandErrorCode.OutOfRoom: return "Outside the room";
                case CommandErrorCode.UnsupportedYaw: return "Only quarter turns are supported";
                case CommandErrorCode.NotSnapped: return "Not on the 50 mm grid";
                case CommandErrorCode.EntityNotEditable: return "This object cannot be moved";
                case CommandErrorCode.RevisionConflict: return "The scene changed during the move; try again";
                default: return "Placement not allowed";
            }
        }

        Transform Bar(string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            return go.transform;
        }

        static void Place(Transform t, Vector3 position, Vector3 scale, Material material)
        {
            t.localPosition = position;
            t.localScale = scale;
            t.GetComponent<Renderer>().sharedMaterial = material;
        }
    }
}
