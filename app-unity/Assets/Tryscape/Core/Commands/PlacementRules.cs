using System;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;

namespace Tryscape.Core.Commands
{
    /// <summary>
    /// Committed-placement rules for editable blockers (S07.1): editable blocker only, catalog snapping and yaw,
    /// footprint inside the room, no positive-area overlap with the payload start or another blocker.
    /// Blocking a route is allowed: that is what the evaluator reports, not a placement error.
    /// </summary>
    public static class PlacementRules
    {
        public static CommandErrorCode Check(SceneSpec scene, Catalog catalog, Guid entityId, Pose proposed)
        {
            var entity = scene.FindEntity(entityId);
            if (entity == null)
                return CommandErrorCode.UnknownEntity;
            if (!entity.Editable || entity.Role != EntityRole.Blocker)
                return CommandErrorCode.EntityNotEditable;
            if (!catalog.TryGet(entity.CatalogId, out var entry))
                return CommandErrorCode.InvalidScene;
            if (!Yaw.IsQuarterTurn(proposed.YawDeg) || !Contains(entry.AllowedYawDeg, proposed.YawDeg))
                return CommandErrorCode.UnsupportedYaw;
            if (proposed.XMm % entry.SnapMm != 0 || proposed.ZMm % entry.SnapMm != 0)
                return CommandErrorCode.NotSnapped;

            var footprint = Footprints.Of(entry, proposed);
            if (!Rect2.Room(scene.Room).Contains(footprint))
                return CommandErrorCode.OutOfRoom;

            foreach (var other in scene.Entities)
            {
                if (other.Id == entityId || !catalog.TryGet(other.CatalogId, out var otherEntry))
                    continue;
                var otherFootprint = Footprints.Of(otherEntry, other.Pose);
                if (!footprint.OverlapsArea(otherFootprint))
                    continue;
                if (other.Role == EntityRole.Payload)
                    return CommandErrorCode.OverlapsPayloadStart;
                if (other.Role == EntityRole.Blocker && otherEntry.Collision == CollisionParticipation.FloorObstruction)
                    return CommandErrorCode.OverlapsBlocker;
            }
            return CommandErrorCode.None;
        }

        /// <summary>Round to the nearest catalog grid point (ties away from zero). Pure integer arithmetic.</summary>
        public static int Snap(double valueMm, int snapMm)
        {
            if (snapMm <= 0)
                throw new ArgumentOutOfRangeException(nameof(snapMm));
            if (double.IsNaN(valueMm) || double.IsInfinity(valueMm) || Math.Abs(valueMm) > 1_000_000)
                throw new ArgumentOutOfRangeException(nameof(valueMm), "Coordinate out of supported range");
            var steps = Math.Round(valueMm / snapMm, MidpointRounding.AwayFromZero);
            return checked((int)steps * snapMm);
        }

        static bool Contains(System.Collections.Generic.IReadOnlyList<int> values, int value)
        {
            for (var i = 0; i < values.Count; i++)
                if (values[i] == value)
                    return true;
            return false;
        }
    }
}
