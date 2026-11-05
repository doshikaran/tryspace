using System;
using System.Collections.Generic;
using System.Linq;

namespace Tryscape.Core.Model
{
    // Canonical spatial model (technical-spec S06). Integer millimetres, x east, z north, origin at the
    // south-west floor corner. All types are immutable; "With" methods return new instances.

    public enum EntityRole
    {
        Payload,
        Blocker,
        Decoration,
    }

    public enum Provenance
    {
        Illustrative,
        UserMeasured,
    }

    public enum AssumptionSource
    {
        Template,
        ScenarioModel,
        UserRequest,
        ExampleDefault,
    }

    /// <summary>Quarter-turn yaw only (S06.1). Arbitrary rotation is unsupported in R0.</summary>
    public static class Yaw
    {
        public static bool IsQuarterTurn(int degrees) =>
            degrees == 0 || degrees == 90 || degrees == 180 || degrees == 270;

        public static bool SwapsAxes(int degrees) => degrees == 90 || degrees == 270;
    }

    public readonly struct Pose : IEquatable<Pose>
    {
        public Pose(int xMm, int zMm, int yawDeg)
        {
            XMm = xMm;
            ZMm = zMm;
            YawDeg = yawDeg;
        }

        public int XMm { get; }
        public int ZMm { get; }
        public int YawDeg { get; }

        public Pose WithPosition(int xMm, int zMm) => new Pose(xMm, zMm, YawDeg);
        public Pose WithYaw(int yawDeg) => new Pose(XMm, ZMm, yawDeg);

        public bool Equals(Pose other) => XMm == other.XMm && ZMm == other.ZMm && YawDeg == other.YawDeg;
        public override bool Equals(object obj) => obj is Pose other && Equals(other);
        public override int GetHashCode() => (XMm * 397 ^ ZMm) * 397 ^ YawDeg;
        public override string ToString() => $"({XMm},{ZMm},{YawDeg}°)";
    }

    public readonly struct Waypoint : IEquatable<Waypoint>
    {
        public Waypoint(int xMm, int zMm)
        {
            XMm = xMm;
            ZMm = zMm;
        }

        public int XMm { get; }
        public int ZMm { get; }

        public bool Equals(Waypoint other) => XMm == other.XMm && ZMm == other.ZMm;
        public override bool Equals(object obj) => obj is Waypoint other && Equals(other);
        public override int GetHashCode() => XMm * 397 ^ ZMm;
        public override string ToString() => $"({XMm},{ZMm})";
    }

    public sealed class RoomSpec : IEquatable<RoomSpec>
    {
        public const int MinDimensionMm = 5000;
        public const int MaxDimensionMm = 8000;

        public RoomSpec(int widthMm, int depthMm)
        {
            WidthMm = widthMm;
            DepthMm = depthMm;
        }

        public int WidthMm { get; }
        public int DepthMm { get; }

        public bool Equals(RoomSpec other) => other != null && WidthMm == other.WidthMm && DepthMm == other.DepthMm;
        public override bool Equals(object obj) => Equals(obj as RoomSpec);
        public override int GetHashCode() => WidthMm * 397 ^ DepthMm;
    }

    public sealed class EntitySpec : IEquatable<EntitySpec>
    {
        public EntitySpec(Guid id, string catalogId, EntityRole role, Pose pose, bool editable, string label)
        {
            Id = id;
            CatalogId = catalogId ?? throw new ArgumentNullException(nameof(catalogId));
            Role = role;
            Pose = pose;
            Editable = editable;
            Label = label ?? throw new ArgumentNullException(nameof(label));
        }

        public Guid Id { get; }
        public string CatalogId { get; }
        public EntityRole Role { get; }
        public Pose Pose { get; }
        public bool Editable { get; }
        public string Label { get; }

        public EntitySpec WithPose(Pose pose) => new EntitySpec(Id, CatalogId, Role, pose, Editable, Label);

        public bool Equals(EntitySpec other) =>
            other != null && Id == other.Id && CatalogId == other.CatalogId && Role == other.Role &&
            Pose.Equals(other.Pose) && Editable == other.Editable && Label == other.Label;

        public override bool Equals(object obj) => Equals(obj as EntitySpec);
        public override int GetHashCode() => Id.GetHashCode();
    }

    public sealed class RouteSpec : IEquatable<RouteSpec>
    {
        public RouteSpec(string id, string label, Guid payloadEntityId, int speedMmPerS, IEnumerable<Waypoint> waypoints)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Label = label ?? throw new ArgumentNullException(nameof(label));
            PayloadEntityId = payloadEntityId;
            SpeedMmPerS = speedMmPerS;
            Waypoints = (waypoints ?? throw new ArgumentNullException(nameof(waypoints))).ToArray();
        }

        public string Id { get; }
        public string Label { get; }
        public Guid PayloadEntityId { get; }
        public int SpeedMmPerS { get; }
        public IReadOnlyList<Waypoint> Waypoints { get; }

        public bool Equals(RouteSpec other) =>
            other != null && Id == other.Id && Label == other.Label && PayloadEntityId == other.PayloadEntityId &&
            SpeedMmPerS == other.SpeedMmPerS && Waypoints.SequenceEqual(other.Waypoints);

        public override bool Equals(object obj) => Equals(obj as RouteSpec);
        public override int GetHashCode() => Id.GetHashCode();
    }

    public sealed class Assumption : IEquatable<Assumption>
    {
        public Assumption(string code, string text, AssumptionSource source, bool requiresConfirmation)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Text = text ?? throw new ArgumentNullException(nameof(text));
            Source = source;
            RequiresConfirmation = requiresConfirmation;
        }

        public string Code { get; }
        public string Text { get; }
        public AssumptionSource Source { get; }
        public bool RequiresConfirmation { get; }

        public bool Equals(Assumption other) =>
            other != null && Code == other.Code && Text == other.Text && Source == other.Source &&
            RequiresConfirmation == other.RequiresConfirmation;

        public override bool Equals(object obj) => Equals(obj as Assumption);
        public override int GetHashCode() => Code.GetHashCode();
    }

    public sealed class MeasurementSource : IEquatable<MeasurementSource>
    {
        public const string RoomWidthPath = "room.width_mm";
        public const string RoomDepthPath = "room.depth_mm";

        /// <param name="confirmedAtUtc">Audit metadata only (ISO-8601 UTC string); never used for ordering.</param>
        public MeasurementSource(string fieldPath, string confirmedAtUtc)
        {
            FieldPath = fieldPath ?? throw new ArgumentNullException(nameof(fieldPath));
            ConfirmedAtUtc = confirmedAtUtc ?? throw new ArgumentNullException(nameof(confirmedAtUtc));
        }

        public string FieldPath { get; }
        public string ConfirmedAtUtc { get; }

        public bool Equals(MeasurementSource other) =>
            other != null && FieldPath == other.FieldPath && ConfirmedAtUtc == other.ConfirmedAtUtc;

        public override bool Equals(object obj) => Equals(obj as MeasurementSource);
        public override int GetHashCode() => FieldPath.GetHashCode();
    }

    /// <summary>SceneSpec v1.0.0 (S06.2). Shape-valid by construction only if built by a validating reader.</summary>
    public sealed class SceneSpec
    {
        public const string SchemaVersion = "1.0.0";

        public SceneSpec(
            Guid sceneId,
            Guid projectId,
            string scenarioPackId,
            string scenarioPackVersion,
            string catalogVersion,
            long revision,
            RoomSpec room,
            IEnumerable<EntitySpec> entities,
            IEnumerable<RouteSpec> routes,
            string activeRouteId,
            IEnumerable<Assumption> assumptions,
            Provenance provenance,
            IEnumerable<MeasurementSource> measurementSources)
        {
            SceneId = sceneId;
            ProjectId = projectId;
            ScenarioPackId = scenarioPackId ?? throw new ArgumentNullException(nameof(scenarioPackId));
            ScenarioPackVersion = scenarioPackVersion ?? throw new ArgumentNullException(nameof(scenarioPackVersion));
            CatalogVersion = catalogVersion ?? throw new ArgumentNullException(nameof(catalogVersion));
            if (revision < 0)
                throw new ArgumentOutOfRangeException(nameof(revision));
            Revision = revision;
            Room = room ?? throw new ArgumentNullException(nameof(room));
            Entities = (entities ?? throw new ArgumentNullException(nameof(entities))).ToArray();
            Routes = (routes ?? throw new ArgumentNullException(nameof(routes))).ToArray();
            ActiveRouteId = activeRouteId ?? throw new ArgumentNullException(nameof(activeRouteId));
            Assumptions = (assumptions ?? throw new ArgumentNullException(nameof(assumptions))).ToArray();
            Provenance = provenance;
            MeasurementSources = (measurementSources ?? throw new ArgumentNullException(nameof(measurementSources))).ToArray();
        }

        public Guid SceneId { get; }
        public Guid ProjectId { get; }
        public string ScenarioPackId { get; }
        public string ScenarioPackVersion { get; }
        public string CatalogVersion { get; }
        public long Revision { get; }
        public RoomSpec Room { get; }
        public IReadOnlyList<EntitySpec> Entities { get; }
        public IReadOnlyList<RouteSpec> Routes { get; }
        public string ActiveRouteId { get; }
        public IReadOnlyList<Assumption> Assumptions { get; }
        public Provenance Provenance { get; }
        public IReadOnlyList<MeasurementSource> MeasurementSources { get; }

        public EntitySpec FindEntity(Guid id)
        {
            for (var i = 0; i < Entities.Count; i++)
                if (Entities[i].Id == id)
                    return Entities[i];
            return null;
        }

        public RouteSpec FindRoute(string id)
        {
            for (var i = 0; i < Routes.Count; i++)
                if (Routes[i].Id == id)
                    return Routes[i];
            return null;
        }

        public SceneSpec WithRevision(long revision) => Copy(revision: revision);
        public SceneSpec WithActiveRoute(string routeId) => Copy(activeRouteId: routeId);

        public SceneSpec WithEntityPose(Guid entityId, Pose pose)
        {
            var found = false;
            var updated = Entities.Select(e =>
            {
                if (e.Id != entityId)
                    return e;
                found = true;
                return e.WithPose(pose);
            }).ToArray();
            if (!found)
                throw new ArgumentException($"Unknown entity {entityId}", nameof(entityId));
            return Copy(entities: updated);
        }

        /// <summary>Same editable content (entities, active route, room...) ignoring revision.</summary>
        public bool ContentEquals(SceneSpec other) =>
            other != null && SceneId == other.SceneId && ProjectId == other.ProjectId &&
            ScenarioPackId == other.ScenarioPackId && ScenarioPackVersion == other.ScenarioPackVersion &&
            CatalogVersion == other.CatalogVersion && Room.Equals(other.Room) &&
            Entities.SequenceEqual(other.Entities) && Routes.SequenceEqual(other.Routes) &&
            ActiveRouteId == other.ActiveRouteId && Assumptions.SequenceEqual(other.Assumptions) &&
            Provenance == other.Provenance && MeasurementSources.SequenceEqual(other.MeasurementSources);

        /// <summary>Copies editable content from <paramref name="source"/> while keeping this scene's identity.</summary>
        public SceneSpec WithContentOf(SceneSpec source, long revision) =>
            Copy(revision: revision, entities: source.Entities, activeRouteId: source.ActiveRouteId);

        SceneSpec Copy(long? revision = null, IEnumerable<EntitySpec> entities = null, string activeRouteId = null) =>
            new SceneSpec(SceneId, ProjectId, ScenarioPackId, ScenarioPackVersion, CatalogVersion,
                revision ?? Revision, Room, entities ?? Entities, Routes, activeRouteId ?? ActiveRouteId,
                Assumptions, Provenance, MeasurementSources);
    }
}
