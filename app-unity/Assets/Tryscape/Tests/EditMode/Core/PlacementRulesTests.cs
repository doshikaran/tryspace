using NUnit.Framework;
using Tryscape.Core.Commands;
using Tryscape.Core.Model;

namespace Tryscape.Tests.EditMode.Core
{
    /// <summary>
    /// Placement rules on the F01 room (6000 x 5000). Sofa footprint: x 100..1900, z 2100..2900 (1800 x 800 at
    /// (1000,2500)). Console 600 x 1000 at yaw 0, 1000 x 600 at yaw 90. Expected values are derived by hand below.
    /// </summary>
    public sealed class PlacementRulesTests
    {
        static CommandErrorCode Check(int x, int z, int yaw = 0) =>
            PlacementRules.Check(Fixtures.F01, Fixtures.Catalog, Fixtures.ConsoleId, new Pose(x, z, yaw));

        [Test] public void ParkedSouthIsValid() => Assert.AreEqual(CommandErrorCode.None, Check(2500, 800));

        // Console x half-width 300: centre 1500 spans 1200..1800, overlapping the sofa's 100..1900.
        [Test] public void OverlappingPayloadStartIsRejected() => Assert.AreEqual(CommandErrorCode.OverlapsPayloadStart, Check(1500, 2500));

        // Centre 2200 spans 1900..2500: touches the sofa edge at x = 1900 without area overlap.
        [Test] public void TouchingPayloadEdgeIsAllowed() => Assert.AreEqual(CommandErrorCode.None, Check(2200, 2500));

        // Centre 2150 spans 1850..2450: 50 mm area overlap with the sofa.
        [Test] public void FiftyMillimetreOverlapIsRejected() => Assert.AreEqual(CommandErrorCode.OverlapsPayloadStart, Check(2150, 2500));

        // 5700 spans 5400..6000 (touches east wall, allowed); 5750 spans 5450..6050 (outside).
        [Test] public void TouchingWallIsAllowed() => Assert.AreEqual(CommandErrorCode.None, Check(5700, 2500));
        [Test] public void CrossingWallIsRejected() => Assert.AreEqual(CommandErrorCode.OutOfRoom, Check(5750, 2500));

        // z half-depth 500 at yaw 0: centre 450 spans -50..950 -> outside the south wall.
        [Test] public void CrossingSouthWallIsRejected() => Assert.AreEqual(CommandErrorCode.OutOfRoom, Check(2500, 450));

        // Quarter turn swaps extents: at x = 5550, yaw 0 spans 5250..5850 (inside) but yaw 90 spans 5050..6050 (outside).
        [Test] public void QuarterTurnSwapsFootprintAxes()
        {
            Assert.AreEqual(CommandErrorCode.None, Check(5550, 2500, 0));
            Assert.AreEqual(CommandErrorCode.OutOfRoom, Check(5550, 2500, 90));
            Assert.AreEqual(CommandErrorCode.OutOfRoom, Check(5550, 2500, 270));
            Assert.AreEqual(CommandErrorCode.None, Check(5550, 2500, 180));
        }

        [Test] public void ArbitraryYawIsRejected() => Assert.AreEqual(CommandErrorCode.UnsupportedYaw, Check(2500, 800, 45));
        [Test] public void OffGridPositionIsRejected() => Assert.AreEqual(CommandErrorCode.NotSnapped, Check(2510, 800));

        [Test]
        public void PayloadIsNotEditable()
        {
            var error = PlacementRules.Check(Fixtures.F01, Fixtures.Catalog, Fixtures.SofaId, new Pose(1000, 3000, 0));
            Assert.AreEqual(CommandErrorCode.EntityNotEditable, error);
        }

        [Test]
        public void UnknownEntityIsRejected()
        {
            var error = PlacementRules.Check(Fixtures.F01, Fixtures.Catalog, System.Guid.NewGuid(), new Pose(2500, 800, 0));
            Assert.AreEqual(CommandErrorCode.UnknownEntity, error);
        }

        [Test]
        public void BlockingTheRouteIsAPlacementTheEvaluatorJudges()
        {
            // F04 position (2500,4000) blocks the northern route but is a legal placement.
            Assert.AreEqual(CommandErrorCode.None, Check(2500, 4000));
        }

        [TestCase(2512.4, 50, 2500)]
        [TestCase(2525.0, 50, 2550)]
        [TestCase(2524.9, 50, 2500)]
        [TestCase(-24.9, 50, 0)]
        [TestCase(-25.0, 50, -50)]
        [TestCase(812.0, 50, 800)]
        public void SnapRoundsToNearestGridPoint(double value, int snap, int expected) =>
            Assert.AreEqual(expected, PlacementRules.Snap(value, snap));

        [Test]
        public void SnapRejectsNonFiniteInput()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => PlacementRules.Snap(double.NaN, 50));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => PlacementRules.Snap(double.PositiveInfinity, 50));
        }
    }
}
