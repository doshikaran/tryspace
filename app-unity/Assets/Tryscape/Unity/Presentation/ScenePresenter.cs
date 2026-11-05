using System;
using System.Collections.Generic;
using Tryscape.Application.Editing;
using Tryscape.Core.Model;
using UnityEngine;

namespace Tryscape.Unity.Presentation
{
    /// <summary>
    /// Renders an immutable <see cref="SceneSpec"/> under this (tabletop) transform. Positions always come from the
    /// model via <see cref="ModelSpaceMapping"/>; rendered transforms are never read back as authoritative state.
    /// Construction is staged: a new room is built under a staging object and swapped in only when complete (S05.2).
    /// All sizes are model-space metres, so the tabletop root's scale is pure presentation.
    /// </summary>
    public sealed class ScenePresenter : MonoBehaviour
    {
        const float FloorThickness = 0.02f;
        const float WallThickness = 0.08f;
        const float WallHeight = 0.5f;
        const float ActiveRouteWidth = 0.08f;
        const float InactiveRouteWidth = 0.035f;

        [SerializeField] PrefabRegistry _registry;
        [SerializeField] Material _floorMaterial;
        [SerializeField] Material _wallMaterial;
        [SerializeField] Material _routeMaterial;
        [SerializeField] Material _inactiveRouteMaterial;

        readonly Dictionary<Guid, EntityView> _views = new Dictionary<Guid, EntityView>();
        readonly Dictionary<string, List<Renderer>> _routes = new Dictionary<string, List<Renderer>>();
        Transform _roomRoot;
        RoomSpec _room;
        TextMesh _dimensions;

        public RoomSpec Room => _room;
        public Transform RoomRoot => _roomRoot;
        public IReadOnlyDictionary<Guid, EntityView> Views => _views;

        public void Configure(PrefabRegistry registry, Material floor, Material wall, Material route, Material inactiveRoute)
        {
            _registry = registry;
            _floorMaterial = floor;
            _wallMaterial = wall;
            _routeMaterial = route;
            _inactiveRouteMaterial = inactiveRoute;
        }

        /// <summary>Builds the room for a newly confirmed scene. On failure the previous room is left untouched.</summary>
        public void Build(SceneSpec scene, Catalog catalog)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (catalog == null)
                throw new ArgumentNullException(nameof(catalog));

            var staging = new GameObject("Room (staging)").transform;
            staging.SetParent(transform, false);
            var views = new Dictionary<Guid, EntityView>();
            var routes = new Dictionary<string, List<Renderer>>();
            TextMesh dimensions;
            try
            {
                dimensions = BuildRoom(staging, scene.Room);
                foreach (var entity in scene.Entities)
                {
                    if (!catalog.TryGet(entity.CatalogId, out var entry))
                        throw new InvalidOperationException($"Unknown catalog id {entity.CatalogId}");
                    if (_registry == null || !_registry.TryGet(entry.PrefabKey, out var prefab))
                        throw new InvalidOperationException($"No prefab registered for {entry.PrefabKey}");
                    var instance = Instantiate(prefab, staging, false);
                    instance.name = $"{entity.Label} [{entity.Id.ToString().Substring(0, 8)}]";
                    var view = instance.GetComponent<EntityView>();
                    if (view == null)
                        throw new InvalidOperationException($"Prefab {entry.PrefabKey} has no EntityView");
                    view.Bind(entity, entry);
                    views.Add(entity.Id, view);
                }
                foreach (var route in scene.Routes)
                    routes.Add(route.Id, BuildRoute(staging, scene.Room, route));
            }
            catch
            {
                Destroy(staging.gameObject);
                throw;
            }

            // Commit: swap the staged room in, then dispose the previous one.
            if (_roomRoot != null)
                Destroy(_roomRoot.gameObject);
            staging.name = "Room";
            _roomRoot = staging;
            _room = scene.Room;
            _dimensions = dimensions;
            _views.Clear();
            foreach (var pair in views)
                _views.Add(pair.Key, pair.Value);
            _routes.Clear();
            foreach (var pair in routes)
                _routes.Add(pair.Key, pair.Value);
            Render(scene);
        }

        /// <summary>Updates committed poses and route emphasis from the model.</summary>
        public void Render(SceneSpec scene)
        {
            if (_roomRoot == null)
                return;
            foreach (var entity in scene.Entities)
                if (_views.TryGetValue(entity.Id, out var view))
                    view.ShowCommitted(entity, scene.Room);
            foreach (var pair in _routes)
            {
                var active = pair.Key == scene.ActiveRouteId;
                foreach (var segment in pair.Value)
                {
                    segment.sharedMaterial = active ? _routeMaterial : _inactiveRouteMaterial;
                    var s = segment.transform.localScale;
                    var width = active ? ActiveRouteWidth : InactiveRouteWidth;
                    segment.transform.localScale = s.x > s.z ? new Vector3(s.x, s.y, width) : new Vector3(width, s.y, s.z);
                }
            }
            if (_dimensions != null)
                _dimensions.text = $"{scene.Room.WidthMm / 1000.0:0.0#} m x {scene.Room.DepthMm / 1000.0:0.0#} m - Example environment";
        }

        TextMesh BuildRoom(Transform parent, RoomSpec room)
        {
            var w = ModelSpaceMapping.ToLocalLength(room.WidthMm);
            var d = ModelSpaceMapping.ToLocalLength(room.DepthMm);
            Cube("Floor", parent, new Vector3(0, -FloorThickness / 2f, 0), new Vector3(w, FloorThickness, d), _floorMaterial);
            // Walls sit outside the interior so they never change the modelled usable space.
            var t = WallThickness;
            Cube("Wall North", parent, new Vector3(0, WallHeight / 2f, d / 2f + t / 2f), new Vector3(w + 2 * t, WallHeight, t), _wallMaterial);
            Cube("Wall South", parent, new Vector3(0, WallHeight / 4f, -d / 2f - t / 2f), new Vector3(w + 2 * t, WallHeight / 2f, t), _wallMaterial);
            Cube("Wall East", parent, new Vector3(w / 2f + t / 2f, WallHeight / 2f, 0), new Vector3(t, WallHeight, d), _wallMaterial);
            Cube("Wall West", parent, new Vector3(-w / 2f - t / 2f, WallHeight / 2f, 0), new Vector3(t, WallHeight, d), _wallMaterial);

            var label = new GameObject("Dimensions");
            label.transform.SetParent(parent, false);
            label.transform.localPosition = new Vector3(0, WallHeight + 0.1f, d / 2f + t);
            var text = label.AddComponent<TextMesh>();
            text.anchor = TextAnchor.LowerCenter;
            text.characterSize = 0.05f;
            text.fontSize = 64;
            text.color = Color.white;
            return text;
        }

        List<Renderer> BuildRoute(Transform parent, RoomSpec room, RouteSpec route)
        {
            var root = new GameObject($"Route {route.Id}").transform;
            root.SetParent(parent, false);
            var segments = new List<Renderer>();
            for (var i = 1; i < route.Waypoints.Count; i++)
            {
                var (ax, az) = ModelSpaceMapping.ToLocal(room, route.Waypoints[i - 1].XMm, route.Waypoints[i - 1].ZMm);
                var (bx, bz) = ModelSpaceMapping.ToLocal(room, route.Waypoints[i].XMm, route.Waypoints[i].ZMm);
                var alongX = Mathf.Abs(bx - ax) > Mathf.Abs(bz - az);
                var length = alongX ? Mathf.Abs(bx - ax) : Mathf.Abs(bz - az);
                var segment = Cube($"Segment {i - 1}", root, new Vector3((ax + bx) / 2f, 0.004f, (az + bz) / 2f),
                    alongX ? new Vector3(length, 0.006f, ActiveRouteWidth) : new Vector3(ActiveRouteWidth, 0.006f, length),
                    _routeMaterial);
                segments.Add(segment.GetComponent<Renderer>());
            }
            return segments;
        }

        static GameObject Cube(string name, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>()); // decoration only: no physics, no accidental interaction targets
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            if (material != null)
                go.GetComponent<Renderer>().sharedMaterial = material;
            return go;
        }
    }
}
