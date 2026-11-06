using System.Linq;
using NUnit.Framework;
using Tryscape.Application.Placement;

namespace Tryscape.Tests.EditMode.Core
{
    /// <summary>
    /// Passthrough surface selection (technical-spec S11, AC34). Expected values are worked out by hand from the
    /// stated rules: 0.4-1.2 m above the floor, facing up (normal y >= 0.9), within 1.5 m horizontally of the head,
    /// at least 0.3 m on its shorter side, nearest first.
    /// </summary>
    public sealed class SurfacePlacementTests
    {
        const double FloorY = -1.10; // tracking origin at seated head height: floor 1.1 m below it
        const double HeadX = 0.0, HeadZ = 0.0;

        static SurfaceCandidate Surface(string id, string label = "TABLE", double x = 0, double heightAboveFloor = 0.74,
            double z = 0.8, double normalY = 1.0, double width = 1.2, double depth = 0.6) =>
            new SurfaceCandidate(id, label, x, FloorY + heightAboveFloor, z, normalY, width, depth);

        [Test]
        public void ADeskInFrontOfTheUserQualifies()
        {
            var desk = Surface("desk");
            var ranked = SurfacePlacement.Rank(new[] { desk }, FloorY, HeadX, HeadZ);
            Assert.AreEqual(new[] { "desk" }, ranked.Select(c => c.Id).ToArray());
        }

        [Test]
        public void SurfacesOutsideSeatedHeightReachOrSizeAreRejected()
        {
            var candidates = new[]
            {
                Surface("coffee-table-too-low", heightAboveFloor: 0.35),
                Surface("shelf-too-high", "STORAGE", heightAboveFloor: 1.25),
                Surface("far-table", z: 1.6), // 1.6 m away > 1.5 m
                Surface("narrow-ledge", width: 1.0, depth: 0.25),
                Surface("tilted", normalY: 0.85),
                Surface("wall-art", "WALL_ART"),
                Surface("ok-low-edge", heightAboveFloor: 0.40),
                Surface("ok-high-edge", heightAboveFloor: 1.20, z: 0.9),
            };
            var ranked = SurfacePlacement.Rank(candidates, FloorY, HeadX, HeadZ).Select(c => c.Id).ToArray();
            Assert.AreEqual(new[] { "ok-low-edge", "ok-high-edge" }, ranked, "boundary values are inclusive");
        }

        [Test]
        public void NearestSurfaceComesFirstAndTiesAreDeterministic()
        {
            // Distances: side 1.0 m (x 0.6, z 0.8), front 0.8 m, behind 1.2 m; twinA/twinB both 0.5 m.
            var ranked = SurfacePlacement.Rank(new[]
            {
                Surface("side", x: 0.6, z: 0.8),
                Surface("behind", z: -1.2, label: "COUCH"),
                Surface("front", z: 0.8),
                Surface("twinB", x: 0.3, z: 0.4),
                Surface("twinA", x: -0.3, z: 0.4),
            }, FloorY, HeadX, HeadZ).Select(c => c.Id).ToArray();
            Assert.AreEqual(new[] { "twinA", "twinB", "front", "side", "behind" }, ranked);
        }

        [Test]
        public void PoseSitsJustAboveTheSurfaceAndFacesAwayFromTheUser()
        {
            var front = SurfacePlacement.PoseOn(Surface("front", z: 0.8), HeadX, HeadZ);
            Assert.AreEqual(0.0, front.X, 1e-9);
            Assert.AreEqual(FloorY + 0.74 + 0.02, front.Y, 1e-9);
            Assert.AreEqual(0.8, front.Z, 1e-9);
            Assert.AreEqual(0.0, front.YawDeg, 1e-9);

            var right = SurfacePlacement.PoseOn(Surface("right", x: 1.0, z: 0.0), HeadX, HeadZ);
            Assert.AreEqual(90.0, right.YawDeg, 1e-9);
            var left = SurfacePlacement.PoseOn(Surface("left", x: -1.0, z: 0.0), HeadX, HeadZ);
            Assert.AreEqual(270.0, left.YawDeg, 1e-9, "yaw is normalised to [0, 360)");
            var behind = SurfacePlacement.PoseOn(Surface("behind", z: -1.0), HeadX, HeadZ);
            Assert.AreEqual(180.0, behind.YawDeg, 1e-9);
        }

        [Test]
        public void EmptyOrUnsuitableRoomsYieldNoCandidatesAndEveryStatusHasAPlainMessage()
        {
            Assert.IsEmpty(SurfacePlacement.Rank(new SurfaceCandidate[0], FloorY, HeadX, HeadZ));
            Assert.IsEmpty(SurfacePlacement.Rank(new[] { Surface("floor-like", heightAboveFloor: 0.0) }, FloorY, HeadX, HeadZ));
            foreach (SurfaceScanStatus status in System.Enum.GetValues(typeof(SurfaceScanStatus)))
                Assert.IsFalse(string.IsNullOrWhiteSpace(SurfacePlacement.Describe(status)), status.ToString());
        }
    }
}
