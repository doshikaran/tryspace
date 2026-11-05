using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Tryscape.Core.Model;
using Tryscape.Core.Validation;

namespace Tryscape.Infrastructure.Json
{
    /// <summary>Parse outcome: a shape-valid scene, or the shape errors. Semantic validation is separate (Core).</summary>
    public sealed class SceneParseResult
    {
        SceneParseResult(SceneSpec scene, IReadOnlyList<ValidationError> errors)
        {
            Scene = scene;
            Errors = errors;
        }

        public SceneSpec Scene { get; }
        public IReadOnlyList<ValidationError> Errors { get; }
        public bool Succeeded => Scene != null;

        internal static SceneParseResult Ok(SceneSpec scene) => new SceneParseResult(scene, Array.Empty<ValidationError>());
        internal static SceneParseResult Fail(IReadOnlyList<ValidationError> errors) => new SceneParseResult(null, errors);
    }

    /// <summary>
    /// Strict SceneSpec v1.0.0 reader mirroring contracts/v1/schemas/scene_spec.schema.json. No polymorphic
    /// deserialization; every field is read explicitly. Bounded input size (S12: 1 MiB snapshots).
    /// </summary>
    public static class SceneSpecJsonReader
    {
        public const int MaxChars = 1024 * 1024;

        static readonly string[] TopLevel =
        {
            "schema_version", "scene_id", "project_id", "scenario_pack_id", "scenario_pack_version", "catalog_version",
            "revision", "room", "entities", "routes", "active_route_id", "assumptions", "provenance", "measurement_sources",
        };

        public static SceneParseResult Read(string json)
        {
            if (!StrictJson.TryParse(json, MaxChars, out var token, out var error))
                return SceneParseResult.Fail(new[] { error });
            var reader = new ShapeReader();
            var scene = ReadScene(token, "", reader);
            return reader.HasErrors || scene == null ? SceneParseResult.Fail(reader.Errors) : SceneParseResult.Ok(scene);
        }

        /// <summary>Reads an already-parsed SceneSpec node (e.g. embedded in a proposal or command).</summary>
        public static SceneSpec ReadScene(JToken token, string path, ShapeReader r)
        {
            var o = r.Object(token, path, TopLevel);
            if (o == null)
                return null;

            r.Exact(o, "schema_version", path, SceneSpec.SchemaVersion, ShapeCodes.UnsupportedVersion);
            var sceneId = r.Uuid(o, "scene_id", path);
            var projectId = r.Uuid(o, "project_id", path);
            var pack = r.Enum(o, "scenario_pack_id", path, "moving_day");
            var packVersion = r.Semver(o, "scenario_pack_version", path);
            var catalogVersion = r.Semver(o, "catalog_version", path);
            var revision = r.Long(o, "revision", path, 0, int.MaxValue);
            var room = ReadRoom(o["room"], ShapeReader.Join(path, "room"), r);
            var entities = ReadList(r.Array(o, "entities", path, 1, 24), ShapeReader.Join(path, "entities"), r, ReadEntity);
            var routes = ReadList(r.Array(o, "routes", path, 1, 3), ShapeReader.Join(path, "routes"), r, ReadRoute);
            var activeRoute = r.Slug(o, "active_route_id", path);
            var assumptions = ReadList(r.Array(o, "assumptions", path, 1, 16), ShapeReader.Join(path, "assumptions"), r, ReadAssumption);
            var provenance = r.Enum(o, "provenance", path, "illustrative", "user_measured");
            var sources = ReadList(r.Array(o, "measurement_sources", path, 0, 8), ShapeReader.Join(path, "measurement_sources"), r, ReadMeasurementSource);

            if (r.HasErrors)
                return null;
            return new SceneSpec(sceneId, projectId, pack, packVersion, catalogVersion, revision, room, entities, routes,
                activeRoute, assumptions, provenance == "user_measured" ? Provenance.UserMeasured : Provenance.Illustrative,
                sources);
        }

        static RoomSpec ReadRoom(JToken token, string path, ShapeReader r)
        {
            var o = r.Object(token, path, "width_mm", "depth_mm");
            if (o == null)
                return null;
            return new RoomSpec(
                r.Int(o, "width_mm", path, RoomSpec.MinDimensionMm, RoomSpec.MaxDimensionMm),
                r.Int(o, "depth_mm", path, RoomSpec.MinDimensionMm, RoomSpec.MaxDimensionMm));
        }

        static EntitySpec ReadEntity(JToken token, string path, ShapeReader r)
        {
            var o = r.Object(token, path, "id", "catalog_id", "role", "pose", "editable", "label");
            if (o == null)
                return null;
            var id = r.Uuid(o, "id", path);
            var catalogId = r.Slug(o, "catalog_id", path);
            var role = r.Enum(o, "role", path, "payload", "blocker", "decoration");
            var posePath = ShapeReader.Join(path, "pose");
            var p = r.Object(o["pose"], posePath, "x_mm", "z_mm", "yaw_deg");
            var pose = p == null
                ? default
                : new Pose(r.Int(p, "x_mm", posePath, 0, 8000), r.Int(p, "z_mm", posePath, 0, 8000),
                    r.IntEnum(p, "yaw_deg", posePath, 0, 90, 180, 270));
            var editable = r.Bool(o, "editable", path);
            var label = r.PlainText(o, "label", path, 1, 80);
            if (catalogId == null || role == null || label == null || p == null)
                return null;
            return new EntitySpec(id, catalogId, ParseRole(role), pose, editable, label);
        }

        static RouteSpec ReadRoute(JToken token, string path, ShapeReader r)
        {
            var o = r.Object(token, path, "id", "label", "payload_entity_id", "speed_mm_per_s", "waypoints");
            if (o == null)
                return null;
            var id = r.Slug(o, "id", path);
            var label = r.PlainText(o, "label", path, 1, 80);
            var payload = r.Uuid(o, "payload_entity_id", path);
            var speed = r.Int(o, "speed_mm_per_s", path, 100, 500);
            var waypoints = ReadList(r.Array(o, "waypoints", path, 2, 12), ShapeReader.Join(path, "waypoints"), r,
                (t, wp, rr) =>
                {
                    var w = rr.Object(t, wp, "x_mm", "z_mm");
                    return w == null ? (Waypoint?)null : new Waypoint(rr.Int(w, "x_mm", wp, 0, 8000), rr.Int(w, "z_mm", wp, 0, 8000));
                });
            if (id == null || label == null)
                return null;
            var points = new List<Waypoint>();
            foreach (var w in waypoints)
                points.Add(w.Value);
            return new RouteSpec(id, label, payload, speed, points);
        }

        static Assumption ReadAssumption(JToken token, string path, ShapeReader r)
        {
            var o = r.Object(token, path, "code", "text", "source", "requires_confirmation");
            if (o == null)
                return null;
            var code = r.Slug(o, "code", path);
            var text = r.PlainText(o, "text", path, 1, 240);
            var source = r.Enum(o, "source", path, "template", "scenario_model", "user_request", "example_default");
            var confirm = r.Bool(o, "requires_confirmation", path);
            if (code == null || text == null || source == null)
                return null;
            return new Assumption(code, text, ParseSource(source), confirm);
        }

        static MeasurementSource ReadMeasurementSource(JToken token, string path, ShapeReader r)
        {
            var o = r.Object(token, path, "field_path", "source", "confirmed_at");
            if (o == null)
                return null;
            var field = r.Enum(o, "field_path", path, MeasurementSource.RoomWidthPath, MeasurementSource.RoomDepthPath);
            r.Exact(o, "source", path, "user_confirmed", ShapeCodes.InvalidEnum);
            var at = r.Timestamp(o, "confirmed_at", path);
            return field == null || at == null ? null : new MeasurementSource(field, at);
        }

        static List<T> ReadList<T>(JArray array, string path, ShapeReader r, Func<JToken, string, ShapeReader, T> readItem)
        {
            var items = new List<T>();
            if (array == null)
                return items;
            for (var i = 0; i < array.Count; i++)
            {
                var item = readItem(array[i], ShapeReader.Index(path, i), r);
                if (item != null)
                    items.Add(item);
            }
            return items;
        }

        static EntityRole ParseRole(string role) =>
            role == "payload" ? EntityRole.Payload : role == "blocker" ? EntityRole.Blocker : EntityRole.Decoration;

        static AssumptionSource ParseSource(string source)
        {
            switch (source)
            {
                case "template": return AssumptionSource.Template;
                case "scenario_model": return AssumptionSource.ScenarioModel;
                case "user_request": return AssumptionSource.UserRequest;
                default: return AssumptionSource.ExampleDefault;
            }
        }
    }
}
