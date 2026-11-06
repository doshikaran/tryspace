using System;
using Tryscape.Application.Editing;
using Tryscape.Application.Rehearsal;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;
using UnityEngine;

namespace Tryscape.Unity.Presentation
{
    /// <summary>
    /// Evaluated-obstruction highlight (demo-scope D07): the payload footprint outline at the modelled contact
    /// position, a marker at the contact point and an outline around the named blocker. Positions come only from the
    /// run's evidence and scene snapshot; nothing here is read back as model state. Lives under the tabletop root, so
    /// tabletop scale is presentation only.
    /// </summary>
    public sealed class ResultHighlight : MonoBehaviour
    {
        const float LineWidth = 0.03f;
        const float Lift = 0.012f;

        [SerializeField] Material _material;

        Transform _payloadOutline;
        Transform _blockerOutline;
        Transform _marker;
        TextMesh _label;

        public bool IsShown { get; private set; }

        /// <summary>Model-space contact centre of the shown violation (for tests and diagnostics).</summary>
        public (Rational xMm, Rational zMm)? ShownContact { get; private set; }

        public Guid ShownBlocker { get; private set; }

        public void Configure(Material material) => _material = material;

        void Awake() => Build();

        public void Show(RehearsalRun run, Catalog catalog)
        {
            if (run == null)
                throw new ArgumentNullException(nameof(run));
            var violation = run.Evidence.FirstViolation;
            if (violation == null || run.Route == null)
            {
                Hide();
                return;
            }
            Build();
            var room = run.Scene.Room;
            var payload = run.Scene.FindEntity(run.Route.PayloadEntityId);
            catalog.TryGet(payload.CatalogId, out var payloadEntry);
            var (pex, pez) = Footprints.Extents(payloadEntry, payload.Pose.YawDeg);
            var cx = violation.ContactXMm.ToDouble();
            var cz = violation.ContactZMm.ToDouble();
            Outline(_payloadOutline, room, cx, cz, pex, pez);

            var (mx, mz) = ModelSpaceMapping.ToLocal(room, cx, cz);
            _marker.localPosition = new Vector3(mx, 0.15f, mz); // pin centred 0.15 m above the contact point
            _marker.gameObject.SetActive(true);

            ShownBlocker = Guid.Empty;
            _blockerOutline.gameObject.SetActive(false);
            if (violation.EntityIds.Count > 0)
            {
                var blocker = run.Scene.FindEntity(violation.EntityIds[0]);
                if (blocker != null && catalog.TryGet(blocker.CatalogId, out var entry))
                {
                    var (bex, bez) = Footprints.Extents(entry, blocker.Pose.YawDeg);
                    Outline(_blockerOutline, room, blocker.Pose.XMm, blocker.Pose.ZMm, bex, bez);
                    ShownBlocker = blocker.Id;
                }
            }
            _label.text = violation.RuleCode == RuleCodes.RoomBoundary ? "Model margin to wall" : "Modelled contact";
            ShownContact = (violation.ContactXMm, violation.ContactZMm);
            IsShown = true;
        }

        readonly System.Collections.Generic.List<Transform> _ghosts = new System.Collections.Generic.List<Transform>();

        /// <summary>Number of ghost outlines currently shown (tests and diagnostics).</summary>
        public int GhostCount => _ghosts.Count;

        /// <summary>
        /// Comparison ghosts (D07): for every entity whose pose differs between the shown snapshot and the other attempt,
        /// a floor outline at its position in the other attempt, labelled with that attempt's name. Presentation only.
        /// </summary>
        public void ShowGhosts(SceneSpec shown, SceneSpec other, Catalog catalog, string otherLabel)
        {
            HideGhosts();
            Build();
            foreach (var entity in other.Entities)
            {
                var here = shown.FindEntity(entity.Id);
                if (here != null && here.Pose.Equals(entity.Pose))
                    continue;
                if (!catalog.TryGet(entity.CatalogId, out var entry))
                    continue;
                var ghost = OutlineRoot($"Ghost {entity.Label}");
                var (ex, ez) = Footprints.Extents(entry, entity.Pose.YawDeg);
                Outline(ghost, shown.Room, entity.Pose.XMm, entity.Pose.ZMm, ex, ez);
                var label = new GameObject("Label").AddComponent<TextMesh>();
                label.transform.SetParent(ghost, false);
                label.transform.localPosition = new Vector3(0, 0.05f, 0);
                label.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                label.text = $"{entity.Label} in {otherLabel}";
                label.anchor = TextAnchor.MiddleCenter;
                label.characterSize = 0.03f;
                label.fontSize = 48;
                label.color = new Color(0.7f, 0.85f, 1f);
                _ghosts.Add(ghost);
            }
        }

        public void HideGhosts()
        {
            foreach (var g in _ghosts)
                if (g != null)
                    Destroy(g.gameObject);
            _ghosts.Clear();
        }

        public void Hide()
        {
            Build();
            _payloadOutline.gameObject.SetActive(false);
            _blockerOutline.gameObject.SetActive(false);
            _marker.gameObject.SetActive(false);
            ShownContact = null;
            ShownBlocker = Guid.Empty;
            IsShown = false;
        }

        void Outline(Transform outline, RoomSpec room, double cxMm, double czMm, int extentXMm, int extentZMm)
        {
            var (x, z) = ModelSpaceMapping.ToLocal(room, cxMm, czMm);
            outline.localPosition = new Vector3(x, Lift, z);
            var w = ModelSpaceMapping.ToLocalLength(extentXMm);
            var d = ModelSpaceMapping.ToLocalLength(extentZMm);
            outline.GetChild(0).localPosition = new Vector3(0, 0, d / 2f);
            outline.GetChild(0).localScale = new Vector3(w + LineWidth, 0.004f, LineWidth);
            outline.GetChild(1).localPosition = new Vector3(0, 0, -d / 2f);
            outline.GetChild(1).localScale = new Vector3(w + LineWidth, 0.004f, LineWidth);
            outline.GetChild(2).localPosition = new Vector3(w / 2f, 0, 0);
            outline.GetChild(2).localScale = new Vector3(LineWidth, 0.004f, d + LineWidth);
            outline.GetChild(3).localPosition = new Vector3(-w / 2f, 0, 0);
            outline.GetChild(3).localScale = new Vector3(LineWidth, 0.004f, d + LineWidth);
            outline.gameObject.SetActive(true);
        }

        void Build()
        {
            if (_payloadOutline != null)
                return;
            _payloadOutline = OutlineRoot("Contact footprint");
            _blockerOutline = OutlineRoot("Obstruction outline");
            _marker = new GameObject("Contact marker").transform;
            _marker.SetParent(transform, false);
            Bar("Pin", _marker, new Vector3(0.04f, 0.3f, 0.04f));
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(_marker, false);
            labelGo.transform.localPosition = new Vector3(0, 0.2f, 0);
            _label = labelGo.AddComponent<TextMesh>();
            _label.anchor = TextAnchor.LowerCenter;
            _label.characterSize = 0.04f;
            _label.fontSize = 48;
            _label.color = new Color(1f, 0.4f, 0.3f);
            Hide();
        }

        Transform OutlineRoot(string name)
        {
            var root = new GameObject(name).transform;
            root.SetParent(transform, false);
            for (var i = 0; i < 4; i++)
                Bar($"Edge {i}", root, Vector3.one * 0.01f);
            return root;
        }

        GameObject Bar(string name, Transform parent, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localScale = scale;
            if (_material != null)
                go.GetComponent<Renderer>().sharedMaterial = _material;
            return go;
        }
    }
}
