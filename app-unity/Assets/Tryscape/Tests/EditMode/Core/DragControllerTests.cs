using NUnit.Framework;
using Tryscape.Application.Editing;
using Tryscape.Core.Commands;
using Tryscape.Core.Model;

namespace Tryscape.Tests.EditMode.Core
{
    /// <summary>Drag transaction semantics (S07.2): preview only, one owner, freeze/cancel on lost tracking.</summary>
    public sealed class DragControllerTests
    {
        const int Left = 1;
        const int Right = 2;

        EditingSession _session;
        DragController _drag;

        [SetUp]
        public void SetUp()
        {
            _session = new EditingSession(Fixtures.Session(), new SequenceIds());
            _drag = new DragController(_session);
        }

        Pose Console => _session.Current.FindEntity(Fixtures.ConsoleId).Pose;

        [Test]
        public void ValidReleaseCommitsExactlyOneSnappedMove()
        {
            Assert.IsTrue(_drag.TryBegin(Fixtures.ConsoleId, Right));
            var preview = _drag.UpdatePosition(Right, 2512.4, 812.0);
            Assert.AreEqual(new Pose(2500, 800, 0), preview.Pose);
            Assert.IsTrue(preview.IsValid);
            Assert.AreEqual(0, _session.Current.Revision, "previewing must not change committed state");
            Assert.AreEqual(new Pose(2500, 2500, 0), Console);

            var outcome = _drag.Release(Right);
            Assert.AreEqual(DragOutcomeKind.Committed, outcome.Kind);
            Assert.AreEqual(1, _session.Current.Revision);
            Assert.AreEqual(new Pose(2500, 800, 0), Console);
            Assert.AreEqual(DragState.Idle, _drag.State);
        }

        [Test]
        public void InvalidReleaseRejectsWithReasonAndKeepsCommittedState()
        {
            _drag.TryBegin(Fixtures.ConsoleId, Right);
            var preview = _drag.UpdatePosition(Right, 1500, 2500);
            Assert.IsFalse(preview.IsValid);
            Assert.AreEqual(CommandErrorCode.OverlapsPayloadStart, preview.PlacementError);
            var outcome = _drag.Release(Right);
            Assert.AreEqual(DragOutcomeKind.Rejected, outcome.Kind);
            Assert.AreEqual(CommandErrorCode.OverlapsPayloadStart, outcome.Error);
            Assert.AreEqual(0, _session.Current.Revision);
            Assert.AreEqual(new Pose(2500, 2500, 0), Console);
        }

        [Test]
        public void SecondHandCannotStartOrCommit()
        {
            Assert.IsTrue(_drag.TryBegin(Fixtures.ConsoleId, Right));
            Assert.IsFalse(_drag.TryBegin(Fixtures.ConsoleId, Left));
            _drag.UpdatePosition(Right, 2500, 800);
            _drag.UpdatePosition(Left, 2500, 4000); // ignored
            Assert.AreEqual(new Pose(2500, 800, 0), _drag.Preview.Pose);
            Assert.AreEqual(DragOutcomeKind.Ignored, _drag.Release(Left).Kind);
            Assert.AreEqual(0, _session.Current.Revision);
            Assert.AreEqual(DragOutcomeKind.Committed, _drag.Release(Right).Kind);
        }

        [Test]
        public void PayloadCannotBeDragged()
        {
            Assert.IsFalse(_drag.TryBegin(Fixtures.SofaId, Right));
            Assert.AreEqual(DragState.Idle, _drag.State);
        }

        [Test]
        public void ShortTrackingLossDoesNotFreeze()
        {
            _drag.TryBegin(Fixtures.ConsoleId, Right);
            _drag.UpdatePosition(Right, 2500, 800);
            _drag.TrackingLost(Right, 10.0);
            _drag.Tick(10.19);
            Assert.AreEqual(DragState.Dragging, _drag.State);
            _drag.TrackingRestored(Right);
            Assert.AreEqual(DragOutcomeKind.Committed, _drag.Release(Right).Kind);
        }

        [Test]
        public void TrackingLossFreezesAt200msAndReleaseWhileFrozenNeverCommits()
        {
            _drag.TryBegin(Fixtures.ConsoleId, Right);
            _drag.UpdatePosition(Right, 2500, 800);
            _drag.TrackingLost(Right, 10.0);
            _drag.Tick(10.2);
            Assert.AreEqual(DragState.Frozen, _drag.State);
            Assert.IsTrue(_drag.Preview.Frozen);
            _drag.UpdatePosition(Right, 2500, 1200); // ignored while frozen
            Assert.AreEqual(new Pose(2500, 800, 0), _drag.Preview.Pose);
            Assert.AreEqual(DragOutcomeKind.Cancelled, _drag.Release(Right).Kind);
            Assert.AreEqual(0, _session.Current.Revision);
        }

        [Test]
        public void ReacquisitionResumesPreviewButDoesNotCommit()
        {
            _drag.TryBegin(Fixtures.ConsoleId, Right);
            _drag.UpdatePosition(Right, 2500, 800);
            _drag.TrackingLost(Right, 10.0);
            _drag.Tick(10.5);
            _drag.TrackingRestored(Right);
            Assert.AreEqual(DragState.Dragging, _drag.State);
            Assert.AreEqual(0, _session.Current.Revision, "reacquisition must not commit");
            Assert.AreEqual(DragOutcomeKind.Ignored, _drag.Tick(12.0).Kind, "restored tracking stops the cancel timer");
        }

        [Test]
        public void TrackingLossCancelsAfterOneSecond()
        {
            _drag.TryBegin(Fixtures.ConsoleId, Right);
            _drag.UpdatePosition(Right, 2500, 800);
            _drag.TrackingLost(Right, 10.0);
            Assert.AreEqual(DragOutcomeKind.Ignored, _drag.Tick(10.99).Kind);
            Assert.AreEqual(DragOutcomeKind.Cancelled, _drag.Tick(11.0).Kind);
            Assert.AreEqual(DragState.Idle, _drag.State);
            Assert.AreEqual(0, _session.Current.Revision);
        }

        [Test]
        public void ReleaseOnCommittedPoseIsANoOp()
        {
            _drag.TryBegin(Fixtures.ConsoleId, Right);
            _drag.UpdatePosition(Right, 2510, 2490);
            Assert.AreEqual(DragOutcomeKind.Cancelled, _drag.Release(Right).Kind);
            Assert.AreEqual(0, _session.Current.Revision);
        }

        [Test]
        public void EditDuringDragMakesReleaseAStaleCommand()
        {
            _drag.TryBegin(Fixtures.ConsoleId, Right);
            _drag.UpdatePosition(Right, 2500, 800);
            Assert.IsTrue(_session.SelectRoute("north_detour").Accepted); // another control changed state mid-drag
            var outcome = _drag.Release(Right);
            Assert.AreEqual(DragOutcomeKind.Rejected, outcome.Kind);
            Assert.AreEqual(CommandErrorCode.RevisionConflict, outcome.Error);
            Assert.AreEqual(new Pose(2500, 2500, 0), Console);
        }

        [Test]
        public void ExplicitCancelEndsDragWithoutCommand()
        {
            _drag.TryBegin(Fixtures.ConsoleId, Right);
            _drag.UpdatePosition(Right, 2500, 800);
            Assert.AreEqual(DragOutcomeKind.Cancelled, _drag.Cancel().Kind);
            Assert.AreEqual(DragOutcomeKind.Ignored, _drag.Release(Right).Kind);
            Assert.AreEqual(0, _session.Current.Revision);
        }
    }

    public sealed class ModelSpaceMappingTests
    {
        [Test]
        public void RoomCentreMapsToLocalOriginAndRoundTrips()
        {
            var room = new RoomSpec(6000, 5000);
            var (x, z) = ModelSpaceMapping.ToLocal(room, 3000, 2500);
            Assert.AreEqual(0f, x);
            Assert.AreEqual(0f, z);
            var (sx, sz) = ModelSpaceMapping.ToLocal(room, 0, 0);
            Assert.AreEqual(-3f, sx, 1e-6);
            Assert.AreEqual(-2.5f, sz, 1e-6);
            var (mx, mz) = ModelSpaceMapping.ToModel(room, 1.234f, -0.5f);
            Assert.AreEqual(4234.0, mx, 0.01);
            Assert.AreEqual(2000.0, mz, 0.01);
        }

        [Test]
        public void EverySnappedGridPointRoundTripsExactly()
        {
            var room = new RoomSpec(7000, 6000);
            for (var xMm = 0; xMm <= 7000; xMm += 50)
            {
                var (lx, lz) = ModelSpaceMapping.ToLocal(room, xMm, 6000 - xMm % 6000);
                var (bx, bz) = ModelSpaceMapping.ToModel(room, lx, lz);
                Assert.AreEqual(xMm, PlacementRules.Snap(bx, 50));
                Assert.AreEqual(6000 - xMm % 6000, PlacementRules.Snap(bz, 50));
            }
        }
    }
}
