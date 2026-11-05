using Tryscape.Core.Model;

namespace Tryscape.Application.Editing
{
    /// <summary>
    /// The single model-to-presentation mapping (S06.1). Model millimetres map to metres in the tabletop root's
    /// local space with the room centred on the root origin; x east -> +X, z north -> +Z. The root's transform (its
    /// scale, position and rotation) is presentation only, so rescaling or recentring the tabletop can never change
    /// model coordinates or results.
    /// </summary>
    public static class ModelSpaceMapping
    {
        public const double MetresPerMillimetre = 0.001;

        public static (float x, float z) ToLocal(RoomSpec room, double xMm, double zMm) =>
            ((float)((xMm - room.WidthMm / 2.0) * MetresPerMillimetre), (float)((zMm - room.DepthMm / 2.0) * MetresPerMillimetre));

        public static (double xMm, double zMm) ToModel(RoomSpec room, float localX, float localZ) =>
            (localX / MetresPerMillimetre + room.WidthMm / 2.0, localZ / MetresPerMillimetre + room.DepthMm / 2.0);

        public static float ToLocalLength(int mm) => (float)(mm * MetresPerMillimetre);

        /// <summary>Unity Y rotation (degrees, clockwise seen from above) for a model yaw.</summary>
        public static float ToLocalYaw(int yawDeg) => yawDeg;
    }
}
