using System;
using System.Collections.Generic;
using System.Linq;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;

namespace Tryscape.Core.Validation
{
    /// <summary>Semantic validation codes; identical to contracts/v1 fixture expectations.</summary>
    public static class ValidationCodes
    {
        public const string ScenarioPackMismatch = "scenario_pack_mismatch";
        public const string CatalogVersionMismatch = "catalog_version_mismatch";
        public const string DuplicateEntityId = "duplicate_entity_id";
        public const string DuplicateRouteId = "duplicate_route_id";
        public const string UnknownActiveRoute = "unknown_active_route";
        public const string UnknownCatalogId = "unknown_catalog_id";
        public const string RoleNotAllowed = "role_not_allowed";
        public const string YawNotAllowed = "yaw_not_allowed";
        public const string EntityOutsideRoom = "entity_outside_room";
        public const string PayloadCountInvalid = "payload_count_invalid";
        public const string PayloadOutsideRoomMargin = "payload_outside_room_margin";
        public const string BlockerOverlapsPayload = "blocker_overlaps_payload";
        public const string BlockerOverlapsBlocker = "blocker_overlaps_blocker";
        public const string RoutePayloadInvalid = "route_payload_invalid";
        public const string RouteStartMismatch = "route_start_mismatch";
        public const string RouteSegmentZeroLength = "route_segment_zero_length";
        public const string RouteSegmentNotAxisAligned = "route_segment_not_axis_aligned";
        public const string RouteDestinationMismatch = "route_destination_mismatch";
        public const string MeasurementSourcesMissing = "measurement_sources_missing";
    }

    public readonly struct ValidationError : IEquatable<ValidationError>
    {
        public ValidationError(string path, string code)
        {
            Path = path;
            Code = code;
        }

        public string Path { get; }
        public string Code { get; }

        public bool Equals(ValidationError other) => Path == other.Path && Code == other.Code;
        public override bool Equals(object obj) => obj is ValidationError other && Equals(other);
        public override int GetHashCode() => (Path ?? "").GetHashCode() * 397 ^ (Code ?? "").GetHashCode();
        public override string ToString() => $"{Path}: {Code}";
    }

    public sealed class ValidationResult
    {
        public static readonly ValidationResult Valid = new ValidationResult(Array.Empty<ValidationError>());

        public ValidationResult(IEnumerable<ValidationError> errors)
        {
            Errors = errors.ToArray();
        }

        public IReadOnlyList<ValidationError> Errors { get; }
        public bool IsValid => Errors.Count == 0;
        public bool Has(string code) => Errors.Any(e => e.Code == code);
    }

    /// <summary>Model parameters shared by validation, placement and (later) evaluation.</summary>
    public static class ModelParameters
    {
        /// <summary>Illustrative per-side payload margin (S08.1); not a safety recommendation.</summary>
        public const int PayloadMarginMm = 25;
    }

    /// <summary>
    /// Semantic SceneSpec validation (S06.3, S07.1). Shape (types, bounds, enums, unknown fields) is the reader's job;
    /// this checks catalog, containment, overlap, route and provenance rules. Pure and deterministic.
    /// </summary>
    public static class SceneValidator
    {
        public static ValidationResult Validate(SceneSpec scene, Catalog catalog)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (catalog == null)
                throw new ArgumentNullException(nameof(catalog));

            var errors = new List<ValidationError>();
            void Add(string path, string code) => errors.Add(new ValidationError(path, code));

            if (scene.ScenarioPackId != catalog.ScenarioPackId)
                Add("scenario_pack_id", ValidationCodes.ScenarioPackMismatch);
            if (scene.CatalogVersion != catalog.CatalogVersion)
                Add("catalog_version", ValidationCodes.CatalogVersionMismatch);

            var room = Rect2.Room(scene.Room);
            var seenIds = new HashSet<Guid>();
            var payloads = new List<(EntitySpec entity, CatalogEntry entry)>();
            var blockers = new List<(EntitySpec entity, Rect2 footprint, int index)>();
            var payloadRoleCount = 0;

            for (var i = 0; i < scene.Entities.Count; i++)
            {
                var e = scene.Entities[i];
                var path = $"entities[{i}]";
                if (!seenIds.Add(e.Id))
                    Add(path + ".id", ValidationCodes.DuplicateEntityId);
                if (e.Role == EntityRole.Payload)
                    payloadRoleCount++;
                if (!catalog.TryGet(e.CatalogId, out var entry))
                {
                    Add(path + ".catalog_id", ValidationCodes.UnknownCatalogId);
                    continue;
                }
                if (!entry.AllowedRoles.Contains(e.Role))
                    Add(path + ".role", ValidationCodes.RoleNotAllowed);
                if (!Yaw.IsQuarterTurn(e.Pose.YawDeg) || !entry.AllowedYawDeg.Contains(e.Pose.YawDeg))
                {
                    Add(path + ".pose.yaw_deg", ValidationCodes.YawNotAllowed);
                    continue;
                }
                var footprint = Footprints.Of(entry, e.Pose);
                if (!room.Contains(footprint))
                    Add(path + ".pose", ValidationCodes.EntityOutsideRoom);
                if (e.Role == EntityRole.Payload)
                    payloads.Add((e, entry));
                else if (e.Role == EntityRole.Blocker && entry.Collision == CollisionParticipation.FloorObstruction)
                    blockers.Add((e, footprint, i));
            }

            if (payloadRoleCount != 1)
                Add("entities", ValidationCodes.PayloadCountInvalid);

            foreach (var (payload, entry) in payloads)
            {
                var index = IndexOf(scene, payload.Id);
                if (!Footprints.TryPermittedCenters(scene.Room, entry, payload.Pose.YawDeg, ModelParameters.PayloadMarginMm, out var permitted) ||
                    !permitted.ContainsPoint2(2L * payload.Pose.XMm, 2L * payload.Pose.ZMm))
                    Add($"entities[{index}].pose", ValidationCodes.PayloadOutsideRoomMargin);
                var payloadFootprint = Footprints.Of(entry, payload.Pose);
                foreach (var blocker in blockers)
                    if (blocker.footprint.OverlapsArea(payloadFootprint))
                        Add($"entities[{blocker.index}].pose", ValidationCodes.BlockerOverlapsPayload);
            }

            for (var a = 0; a < blockers.Count; a++)
                for (var b = a + 1; b < blockers.Count; b++)
                    if (blockers[a].footprint.OverlapsArea(blockers[b].footprint))
                        Add($"entities[{blockers[b].index}].pose", ValidationCodes.BlockerOverlapsBlocker);

            ValidateRoutes(scene, Add);

            if (scene.Provenance == Provenance.UserMeasured)
            {
                var confirmed = new HashSet<string>(scene.MeasurementSources.Select(m => m.FieldPath));
                if (!confirmed.Contains(MeasurementSource.RoomWidthPath) || !confirmed.Contains(MeasurementSource.RoomDepthPath))
                    Add("measurement_sources", ValidationCodes.MeasurementSourcesMissing);
            }

            return errors.Count == 0 ? ValidationResult.Valid : new ValidationResult(errors);
        }

        static void ValidateRoutes(SceneSpec scene, Action<string, string> add)
        {
            var routeIds = new HashSet<string>(StringComparer.Ordinal);
            Waypoint? destination = null;
            var destinationMismatch = false;

            for (var r = 0; r < scene.Routes.Count; r++)
            {
                var route = scene.Routes[r];
                var path = $"routes[{r}]";
                if (!routeIds.Add(route.Id))
                    add(path + ".id", ValidationCodes.DuplicateRouteId);

                var payload = scene.FindEntity(route.PayloadEntityId);
                if (payload == null || payload.Role != EntityRole.Payload)
                    add(path + ".payload_entity_id", ValidationCodes.RoutePayloadInvalid);
                if (route.Waypoints.Count == 0)
                    continue;
                var start = route.Waypoints[0];
                if (payload != null && (start.XMm != payload.Pose.XMm || start.ZMm != payload.Pose.ZMm))
                    add(path + ".waypoints[0]", ValidationCodes.RouteStartMismatch);

                for (var w = 1; w < route.Waypoints.Count; w++)
                {
                    var a = route.Waypoints[w - 1];
                    var b = route.Waypoints[w];
                    if (a.Equals(b))
                        add($"{path}.waypoints[{w}]", ValidationCodes.RouteSegmentZeroLength);
                    else if (a.XMm != b.XMm && a.ZMm != b.ZMm)
                        add($"{path}.waypoints[{w}]", ValidationCodes.RouteSegmentNotAxisAligned);
                }

                var end = route.Waypoints[route.Waypoints.Count - 1];
                if (destination == null)
                    destination = end;
                else if (!destination.Value.Equals(end))
                    destinationMismatch = true;
            }

            if (destinationMismatch)
                add("routes", ValidationCodes.RouteDestinationMismatch);
            if (!routeIds.Contains(scene.ActiveRouteId))
                add("active_route_id", ValidationCodes.UnknownActiveRoute);
        }

        static int IndexOf(SceneSpec scene, Guid id)
        {
            for (var i = 0; i < scene.Entities.Count; i++)
                if (scene.Entities[i].Id == id)
                    return i;
            return -1;
        }
    }
}
