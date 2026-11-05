using System;
using System.Linq;
using Tryscape.Core.Model;

namespace Tryscape.Core.Canonical
{
    /// <summary>
    /// Canonical SceneSpec bytes and <c>scene_hash</c> (S09.1). Entities sorted by lowercase UUID, routes by route ID,
    /// measurement sources by field path (all ordinal); waypoints and assumptions keep their semantic order.
    /// The hash includes revision and active route. Audit timestamps (<c>measurement_sources[].confirmed_at</c>) are
    /// excluded. Route waypoints are hashed exactly as stored: no collinear-point normalisation is applied, in any
    /// runtime. Call only with a semantically valid scene.
    /// </summary>
    public static class SceneCanonicalizer
    {
        public static byte[] CanonicalBytes(SceneSpec scene) => CanonicalJson.ToBytes(ToCanonical(scene));

        public static string SceneHash(SceneSpec scene) => CanonicalJson.Sha256Hex(CanonicalBytes(scene));

        public static string Uuid(Guid id) => id.ToString("D"); // lowercase, hyphenated

        public static CanonicalObject ToCanonical(SceneSpec scene)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));

            var entities = CanonicalJson.Array();
            foreach (var e in scene.Entities.OrderBy(e => Uuid(e.Id), StringComparer.Ordinal))
                entities.Add(CanonicalJson.Object()
                    .Set("id", Uuid(e.Id))
                    .Set("catalog_id", e.CatalogId)
                    .Set("role", Role(e.Role))
                    .Set("pose", CanonicalJson.Object()
                        .Set("x_mm", e.Pose.XMm)
                        .Set("z_mm", e.Pose.ZMm)
                        .Set("yaw_deg", e.Pose.YawDeg))
                    .Set("editable", e.Editable)
                    .Set("label", e.Label));

            var routes = CanonicalJson.Array();
            foreach (var r in scene.Routes.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                var waypoints = CanonicalJson.Array();
                foreach (var w in r.Waypoints)
                    waypoints.Add(CanonicalJson.Object().Set("x_mm", w.XMm).Set("z_mm", w.ZMm));
                routes.Add(CanonicalJson.Object()
                    .Set("id", r.Id)
                    .Set("label", r.Label)
                    .Set("payload_entity_id", Uuid(r.PayloadEntityId))
                    .Set("speed_mm_per_s", r.SpeedMmPerS)
                    .Set("waypoints", waypoints));
            }

            var sources = CanonicalJson.Array();
            foreach (var m in scene.MeasurementSources.OrderBy(m => m.FieldPath, StringComparer.Ordinal))
                sources.Add(CanonicalJson.Object().Set("field_path", m.FieldPath).Set("source", "user_confirmed"));

            return CanonicalJson.Object()
                .Set("schema_version", SceneSpec.SchemaVersion)
                .Set("scene_id", Uuid(scene.SceneId))
                .Set("project_id", Uuid(scene.ProjectId))
                .Set("scenario_pack_id", scene.ScenarioPackId)
                .Set("scenario_pack_version", scene.ScenarioPackVersion)
                .Set("catalog_version", scene.CatalogVersion)
                .Set("revision", scene.Revision)
                .Set("room", CanonicalJson.Object()
                    .Set("width_mm", scene.Room.WidthMm)
                    .Set("depth_mm", scene.Room.DepthMm))
                .Set("entities", entities)
                .Set("routes", routes)
                .Set("active_route_id", scene.ActiveRouteId)
                .Set("assumptions", Assumptions(scene))
                .Set("provenance", scene.Provenance == Provenance.UserMeasured ? "user_measured" : "illustrative")
                .Set("measurement_sources", sources);
        }

        public static CanonicalArray Assumptions(SceneSpec scene)
        {
            var array = CanonicalJson.Array();
            foreach (var a in scene.Assumptions)
                array.Add(CanonicalJson.Object()
                    .Set("code", a.Code)
                    .Set("text", a.Text)
                    .Set("source", Source(a.Source))
                    .Set("requires_confirmation", a.RequiresConfirmation));
            return array;
        }

        public static string Role(EntityRole role)
        {
            switch (role)
            {
                case EntityRole.Payload: return "payload";
                case EntityRole.Blocker: return "blocker";
                case EntityRole.Decoration: return "decoration";
                default: throw new ArgumentOutOfRangeException(nameof(role));
            }
        }

        public static string Source(AssumptionSource source)
        {
            switch (source)
            {
                case AssumptionSource.Template: return "template";
                case AssumptionSource.ScenarioModel: return "scenario_model";
                case AssumptionSource.UserRequest: return "user_request";
                case AssumptionSource.ExampleDefault: return "example_default";
                default: throw new ArgumentOutOfRangeException(nameof(source));
            }
        }
    }
}
