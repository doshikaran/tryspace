using System;
using Tryscape.Core.Model;

namespace Tryscape.Core.Geometry
{
    /// <summary>
    /// Axis-aligned rectangle in doubled millimetres (S08.2): a centre at C mm with extent W mm spans
    /// [2C - W, 2C + W], so half-millimetre bounds stay integral. Checked 64-bit arithmetic throughout.
    /// </summary>
    public readonly struct Rect2 : IEquatable<Rect2>
    {
        public Rect2(long minX2, long minZ2, long maxX2, long maxZ2)
        {
            if (maxX2 < minX2 || maxZ2 < minZ2)
                throw new ArgumentException("Inverted rectangle");
            MinX2 = minX2;
            MinZ2 = minZ2;
            MaxX2 = maxX2;
            MaxZ2 = maxZ2;
        }

        public long MinX2 { get; }
        public long MinZ2 { get; }
        public long MaxX2 { get; }
        public long MaxZ2 { get; }

        public static Rect2 FromCenter(int centerXMm, int centerZMm, int extentXMm, int extentZMm)
        {
            checked
            {
                long cx2 = 2L * centerXMm, cz2 = 2L * centerZMm;
                return new Rect2(cx2 - extentXMm, cz2 - extentZMm, cx2 + extentXMm, cz2 + extentZMm);
            }
        }

        public static Rect2 Room(RoomSpec room) =>
            new Rect2(0, 0, checked(2L * room.WidthMm), checked(2L * room.DepthMm));

        /// <summary>Positive-area overlap; touching edges do not overlap.</summary>
        public bool OverlapsArea(Rect2 other) =>
            MinX2 < other.MaxX2 && other.MinX2 < MaxX2 && MinZ2 < other.MaxZ2 && other.MinZ2 < MaxZ2;

        /// <summary>Closed containment: <paramref name="inner"/> may touch this rectangle's boundary.</summary>
        public bool Contains(Rect2 inner) =>
            MinX2 <= inner.MinX2 && inner.MaxX2 <= MaxX2 && MinZ2 <= inner.MinZ2 && inner.MaxZ2 <= MaxZ2;

        public bool ContainsPoint2(long x2, long z2) => MinX2 <= x2 && x2 <= MaxX2 && MinZ2 <= z2 && z2 <= MaxZ2;

        public bool Equals(Rect2 other) =>
            MinX2 == other.MinX2 && MinZ2 == other.MinZ2 && MaxX2 == other.MaxX2 && MaxZ2 == other.MaxZ2;

        public override bool Equals(object obj) => obj is Rect2 other && Equals(other);
        public override int GetHashCode() => ((MinX2.GetHashCode() * 397 ^ MinZ2.GetHashCode()) * 397 ^ MaxX2.GetHashCode()) * 397 ^ MaxZ2.GetHashCode();
        public override string ToString() => $"[{MinX2 / 2.0}..{MaxX2 / 2.0}]x[{MinZ2 / 2.0}..{MaxZ2 / 2.0}]mm";
    }

    public static class Footprints
    {
        /// <summary>Footprint extents (x, z) in mm after applying a quarter-turn yaw.</summary>
        public static (int extentXMm, int extentZMm) Extents(CatalogEntry entry, int yawDeg)
        {
            if (!Yaw.IsQuarterTurn(yawDeg))
                throw new ArgumentOutOfRangeException(nameof(yawDeg), "Only quarter-turn yaw is supported");
            return Yaw.SwapsAxes(yawDeg)
                ? (entry.FootprintDepthMm, entry.FootprintWidthMm)
                : (entry.FootprintWidthMm, entry.FootprintDepthMm);
        }

        public static Rect2 Of(CatalogEntry entry, Pose pose)
        {
            var (ex, ez) = Extents(entry, pose.YawDeg);
            return Rect2.FromCenter(pose.XMm, pose.ZMm, ex, ez);
        }

        /// <summary>
        /// Rectangle of permitted payload centres: the room shrunk by payload half-extents plus margin (S08.2).
        /// Closed: a centre exactly on the boundary keeps exactly the margin from the wall and is accepted.
        /// Returns false when the room is too small to hold the payload at all.
        /// </summary>
        public static bool TryPermittedCenters(RoomSpec room, CatalogEntry payload, int yawDeg, int marginMm, out Rect2 permitted)
        {
            var (ex, ez) = Extents(payload, yawDeg);
            checked
            {
                // In doubled units a centre C2 must satisfy C2 - ex - 2m >= 0 and C2 + ex + 2m <= 2W.
                long minX2 = ex + 2L * marginMm, minZ2 = ez + 2L * marginMm;
                long maxX2 = 2L * room.WidthMm - ex - 2L * marginMm, maxZ2 = 2L * room.DepthMm - ez - 2L * marginMm;
                if (maxX2 < minX2 || maxZ2 < minZ2)
                {
                    permitted = default;
                    return false;
                }
                permitted = new Rect2(minX2, minZ2, maxX2, maxZ2);
                return true;
            }
        }
    }
}
