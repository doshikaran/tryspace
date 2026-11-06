using System;
using System.Collections.Generic;
using System.Linq;

namespace Tryscape.Application.Placement
{
    /// <summary>
    /// A horizontal real-world surface reported by the headset's scene model (technical-spec S11, demo-scope AC34).
    /// World space, metres, y up. Labels are the scene-model semantic labels (for example <c>TABLE</c>).
    /// </summary>
    public readonly struct SurfaceCandidate
    {
        public SurfaceCandidate(string id, string label, double x, double y, double z, double normalY, double width, double depth)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Label = label ?? throw new ArgumentNullException(nameof(label));
            X = x;
            Y = y;
            Z = z;
            NormalY = normalY;
            Width = width;
            Depth = depth;
        }

        public string Id { get; }
        public string Label { get; }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        /// <summary>Vertical component of the unit surface normal; 1 means facing straight up.</summary>
        public double NormalY { get; }

        public double Width { get; }
        public double Depth { get; }
    }

    /// <summary>Why a scan produced no usable surface; shown to the user in plain words.</summary>
    public enum SurfaceScanStatus
    {
        Found,

        /// <summary>The user declined the spatial-data permission.</summary>
        NoPermission,

        /// <summary>The headset has no room set up (Space Setup not run).</summary>
        NoRoom,

        /// <summary>A room exists but no surface meets the placement rules.</summary>
        NoSuitableSurface,

        /// <summary>Scene data is not available on this platform (Editor, simulator without a room) or the load failed.</summary>
        Unavailable,
    }

    /// <summary>Where the tabletop sits. Presentation only: it never changes model geometry or results.</summary>
    public readonly struct PlacementPose
    {
        public PlacementPose(double x, double y, double z, double yawDeg)
        {
            X = x;
            Y = y;
            Z = z;
            YawDeg = yawDeg;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        /// <summary>Rotation about world up, degrees, 0 = facing +z.</summary>
        public double YawDeg { get; }
    }

    /// <summary>
    /// Pure surface selection rules for seated tabletop placement. A surface qualifies when it faces up, sits at a
    /// reachable seated height above the floor, is close enough to reach and is large enough to hold the miniature.
    /// Qualifying surfaces are ordered nearest first (horizontal distance from the head); ties break on ID so the order
    /// is deterministic.
    /// </summary>
    public static class SurfacePlacement
    {
        public const double MinNormalY = 0.9;
        public const double MinHeightAboveFloor = 0.4;
        public const double MaxHeightAboveFloor = 1.2;
        public const double MaxHorizontalDistance = 1.5;
        public const double MinSurfaceSide = 0.3;

        /// <summary>Gap between the real surface and the miniature's floor so it never z-fights or sinks into it.</summary>
        public const double SurfaceGap = 0.02;

        static readonly HashSet<string> AcceptedLabels = new HashSet<string>(StringComparer.Ordinal)
        {
            "TABLE", "COUCH", "STORAGE", "BED", "OTHER",
        };

        public static bool Qualifies(in SurfaceCandidate c, double floorY, double headX, double headZ) =>
            AcceptedLabels.Contains(c.Label) &&
            c.NormalY >= MinNormalY &&
            c.Y - floorY >= MinHeightAboveFloor &&
            c.Y - floorY <= MaxHeightAboveFloor &&
            Math.Min(c.Width, c.Depth) >= MinSurfaceSide &&
            HorizontalDistance(c, headX, headZ) <= MaxHorizontalDistance;

        public static IReadOnlyList<SurfaceCandidate> Rank(IEnumerable<SurfaceCandidate> candidates, double floorY,
            double headX, double headZ)
        {
            if (candidates == null)
                throw new ArgumentNullException(nameof(candidates));
            return candidates
                .Where(c => Qualifies(c, floorY, headX, headZ))
                .OrderBy(c => HorizontalDistance(c, headX, headZ))
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Pose for the miniature on a surface: centred on it, raised by <see cref="SurfaceGap"/>, turned so that its
        /// front faces the user (yaw along the direction from the head to the surface).
        /// </summary>
        public static PlacementPose PoseOn(in SurfaceCandidate c, double headX, double headZ)
        {
            var dx = c.X - headX;
            var dz = c.Z - headZ;
            var yaw = dx * dx + dz * dz < 1e-8 ? 0.0 : Math.Atan2(dx, dz) * 180.0 / Math.PI;
            return new PlacementPose(c.X, c.Y + SurfaceGap, c.Z, NormalizeYaw(yaw));
        }

        public static string Describe(SurfaceScanStatus status) => status switch
        {
            SurfaceScanStatus.Found => "Placed on a surface in your room.",
            SurfaceScanStatus.NoPermission =>
                "Spatial data permission was declined, so the rehearsal floats in front of you. You can allow it later in Settings.",
            SurfaceScanStatus.NoRoom =>
                "This headset has no room set up yet. Choose \"Set up my space\" in Settings, or keep the floating placement.",
            SurfaceScanStatus.NoSuitableSurface =>
                "No table or desk within reach was found, so the rehearsal floats in front of you.",
            _ => "Room data is not available here, so the rehearsal floats in front of you.",
        };

        static double HorizontalDistance(in SurfaceCandidate c, double headX, double headZ)
        {
            var dx = c.X - headX;
            var dz = c.Z - headZ;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        static double NormalizeYaw(double yaw)
        {
            yaw %= 360.0;
            return yaw < 0 ? yaw + 360.0 : yaw;
        }
    }
}
