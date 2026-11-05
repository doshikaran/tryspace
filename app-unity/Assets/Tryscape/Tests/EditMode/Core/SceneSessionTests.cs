using System;
using System.Linq;
using NUnit.Framework;
using Tryscape.Core.Commands;
using Tryscape.Core.Model;
using Tryscape.Core.Validation;

namespace Tryscape.Tests.EditMode.Core
{
    /// <summary>Command, revision, undo/redo/reset and duplicate-delivery semantics (S07, S18.1 group 6).</summary>
    public sealed class SceneSessionTests
    {
        static Pose ConsolePose(SceneSession s) => s.Current.FindEntity(Fixtures.ConsoleId).Pose;

        [Test]
        public void AcceptedMoveIncrementsRevisionOnceAndReportsTheChangedEntity()
        {
            var session = Fixtures.Session();
            var result = session.Execute(Cmd.Move(2500, 800, 0));
            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(1, result.Revision);
            Assert.AreEqual(1, session.Current.Revision);
            CollectionAssert.AreEqual(new[] { Fixtures.ConsoleId }, result.ChangedEntityIds);
            Assert.AreEqual(new Pose(2500, 800, 0), ConsolePose(session));
            // An unrelated entity is untouched.
            Assert.AreEqual(new Pose(1000, 2500, 0), session.Current.FindEntity(Fixtures.SofaId).Pose);
        }

        [Test]
        public void RejectedMoveChangesNothing()
        {
            var session = Fixtures.Session();
            var before = session.Current;
            var changedEvents = 0;
            session.Changed += (_, __) => changedEvents++;
            var result = session.Execute(Cmd.Move(1500, 2500, 0));
            Assert.IsFalse(result.Accepted);
            Assert.AreEqual(CommandErrorCode.OverlapsPayloadStart, result.ErrorCode);
            Assert.AreEqual(0, result.Revision);
            Assert.IsEmpty(result.ChangedEntityIds);
            Assert.AreSame(before, session.Current, "a rejected command must not replace the scene");
            Assert.AreEqual(0, changedEvents);
            Assert.IsFalse(session.CanUndo);
        }

        [Test]
        public void StaleExpectedRevisionIsRejected()
        {
            var session = Fixtures.Session();
            session.Execute(Cmd.Move(2500, 800, 0));
            var stale = session.Execute(Cmd.Move(2500, 1200, 0));
            Assert.AreEqual(CommandErrorCode.RevisionConflict, stale.ErrorCode);
            Assert.AreEqual(1, session.Current.Revision);
            Assert.AreEqual(new Pose(2500, 800, 0), ConsolePose(session));
        }

        [Test]
        public void WrongProjectIsRejected()
        {
            var session = Fixtures.Session();
            var result = session.Execute(Cmd.Of(new MoveEntityCommand(Fixtures.ConsoleId, 2500, 800), 0, project: Guid.NewGuid()));
            Assert.AreEqual(CommandErrorCode.ProjectMismatch, result.ErrorCode);
            Assert.AreEqual(0, session.Current.Revision);
        }

        [Test]
        public void DuplicateCommandIdReturnsOriginalResponseWithoutSecondMutation()
        {
            var session = Fixtures.Session();
            var id = Guid.NewGuid();
            var first = session.Execute(Cmd.Move(2500, 800, 0, id));
            var again = session.Execute(Cmd.Move(2500, 800, 0, id));
            Assert.AreSame(first, again);
            Assert.AreEqual(1, session.Current.Revision);
        }

        [Test]
        public void DuplicateCacheIsBounded()
        {
            var session = Fixtures.Session();
            var firstId = Guid.NewGuid();
            session.Execute(Cmd.Of(new SelectRouteCommand("north_detour"), 0, firstId));
            for (var i = 0; i < SceneSession.CommandCacheSize; i++)
                session.Execute(Cmd.Of(new SelectRouteCommand(i % 2 == 0 ? "direct" : "north_detour"), session.Current.Revision));
            // The first ID has been evicted, so it is processed as a new (now stale) command.
            var replay = session.Execute(Cmd.Of(new SelectRouteCommand("north_detour"), 0, firstId));
            Assert.AreEqual(CommandErrorCode.RevisionConflict, replay.ErrorCode);
        }

        [Test]
        public void UndoAndRedoCreateNewRevisionsAndRestoreExactPoses()
        {
            var session = Fixtures.Session();
            session.Execute(Cmd.Move(2500, 800, 0));                                     // rev 1
            var undo = session.Execute(Cmd.Of(new UndoCommand(), 1));                    // rev 2
            Assert.IsTrue(undo.Accepted);
            Assert.AreEqual(2, undo.Revision);
            Assert.AreEqual(new Pose(2500, 2500, 0), ConsolePose(session));
            CollectionAssert.AreEqual(new[] { Fixtures.ConsoleId }, undo.ChangedEntityIds);
            Assert.IsTrue(session.Current.ContentEquals(Fixtures.F01.WithRevision(2)));

            var redo = session.Execute(Cmd.Of(new RedoCommand(), 2));                    // rev 3
            Assert.AreEqual(3, redo.Revision);
            Assert.AreEqual(new Pose(2500, 800, 0), ConsolePose(session));
        }

        [Test]
        public void UndoOnEmptyHistoryIsRejectedWithoutRevisionChange()
        {
            var session = Fixtures.Session();
            Assert.AreEqual(CommandErrorCode.NothingToUndo, session.Execute(Cmd.Of(new UndoCommand(), 0)).ErrorCode);
            Assert.AreEqual(CommandErrorCode.NothingToRedo, session.Execute(Cmd.Of(new RedoCommand(), 0)).ErrorCode);
            Assert.AreEqual(0, session.Current.Revision);
        }

        [Test]
        public void NewEditAfterUndoClearsRedo()
        {
            var session = Fixtures.Session();
            session.Execute(Cmd.Move(2500, 800, 0));
            session.Execute(Cmd.Of(new UndoCommand(), 1));
            Assert.IsTrue(session.CanRedo);
            session.Execute(Cmd.Move(2500, 4000, 2));
            Assert.IsFalse(session.CanRedo);
            Assert.AreEqual(CommandErrorCode.NothingToRedo, session.Execute(Cmd.Of(new RedoCommand(), 3)).ErrorCode);
        }

        [Test]
        public void F04UndoRestoresPriorLayoutSemanticsWithANewRevision()
        {
            // F03 -> F04: select the northern route, then move the console onto it; undo restores F03's layout.
            var session = Fixtures.Session();
            session.Execute(Cmd.Of(new SelectRouteCommand("north_detour"), 0));         // rev 1 (== F03 content)
            session.Execute(Cmd.Move(2500, 4000, 1));                                    // rev 2 (== F04 content)
            Assert.IsTrue(session.Current.ContentEquals(Fixtures.Scene("scenes/valid/f04_console_north_blocks_detour.json")));
            session.Execute(Cmd.Of(new UndoCommand(), 2));                               // rev 3
            Assert.AreEqual(3, session.Current.Revision);
            Assert.IsTrue(session.Current.ContentEquals(Fixtures.Scene("scenes/valid/f03_north_detour.json")));
        }

        [Test]
        public void ResetRestoresConfirmedSnapshotOnceAndClearsHistory()
        {
            var session = Fixtures.Session();
            session.Execute(Cmd.Move(2500, 800, 0));
            session.Execute(Cmd.Of(new SelectRouteCommand("north_detour"), 1));
            session.Execute(Cmd.Of(new RotateEntityCommand(Fixtures.ConsoleId, 90), 2));
            var reset = session.Execute(Cmd.Of(new ResetSceneCommand(), 3));
            Assert.IsTrue(reset.Accepted);
            Assert.AreEqual(4, reset.Revision);
            Assert.IsTrue(session.Current.ContentEquals(session.Confirmed));
            Assert.IsFalse(session.CanUndo);
            Assert.IsFalse(session.CanRedo);
            Assert.AreEqual(0, session.Confirmed.Revision, "the confirmed snapshot itself is immutable");
        }

        [Test]
        public void RevisionsAreStrictlyMonotonicAcrossMixedOperations()
        {
            var session = Fixtures.Session();
            long last = session.Current.Revision;
            void Step(SceneCommand command)
            {
                var r = session.Execute(Cmd.Of(command, session.Current.Revision));
                if (r.Accepted)
                {
                    Assert.AreEqual(last + 1, r.Revision);
                    last = r.Revision;
                }
                else
                {
                    Assert.AreEqual(last, r.Revision);
                }
            }
            Step(new MoveEntityCommand(Fixtures.ConsoleId, 2500, 800));
            Step(new UndoCommand());
            Step(new UndoCommand()); // rejected
            Step(new RedoCommand());
            Step(new MoveEntityCommand(Fixtures.ConsoleId, 1500, 2500)); // rejected
            Step(new ResetSceneCommand());
            Step(new RedoCommand()); // rejected after reset
            Assert.AreEqual(4, last);
        }

        [Test]
        public void UndoHistoryKeepsTheLastFiftyEdits()
        {
            var session = Fixtures.Session();
            for (var i = 0; i < 55; i++)
                Assert.IsTrue(session.Execute(Cmd.Of(new SelectRouteCommand(i % 2 == 0 ? "north_detour" : "direct"), session.Current.Revision)).Accepted);
            var undos = 0;
            while (session.Execute(Cmd.Of(new UndoCommand(), session.Current.Revision)).Accepted)
                undos++;
            Assert.AreEqual(SceneSession.UndoLimit, undos);
        }

        [Test]
        public void RotateUsesQuarterTurnsAndRevalidatesPlacement()
        {
            var session = Fixtures.Session();
            Assert.IsTrue(session.Execute(Cmd.Of(new RotateEntityCommand(Fixtures.ConsoleId, 90), 0)).Accepted);
            Assert.AreEqual(90, ConsolePose(session).YawDeg);
            Assert.AreEqual(CommandErrorCode.UnsupportedYaw,
                session.Execute(Cmd.Of(new RotateEntityCommand(Fixtures.ConsoleId, 45), 1)).ErrorCode);
            // Move next to the east wall at yaw 0 (5550 spans 5250..5850), then a quarter turn would leave the room.
            session.Execute(Cmd.Of(new RotateEntityCommand(Fixtures.ConsoleId, 0), 1));
            Assert.IsTrue(session.Execute(Cmd.Move(5550, 2500, 2)).Accepted);
            Assert.AreEqual(CommandErrorCode.OutOfRoom,
                session.Execute(Cmd.Of(new RotateEntityCommand(Fixtures.ConsoleId, 90), 3)).ErrorCode);
        }

        [Test]
        public void SelectRouteRejectsUnknownRouteAndDoesNotTouchWaypoints()
        {
            var session = Fixtures.Session();
            Assert.AreEqual(CommandErrorCode.UnknownRoute,
                session.Execute(Cmd.Of(new SelectRouteCommand("south_detour"), 0)).ErrorCode);
            var routesBefore = session.Current.Routes;
            Assert.IsTrue(session.Execute(Cmd.Of(new SelectRouteCommand("north_detour"), 0)).Accepted);
            Assert.AreEqual("north_detour", session.Current.ActiveRouteId);
            CollectionAssert.AreEqual(routesBefore, session.Current.Routes);
        }

        [Test]
        public void SessionCannotStartFromAnInvalidScene()
        {
            var invalid = Fixtures.Scene("scenes/invalid_semantic/blocker_overlaps_payload_start.json");
            Assert.IsFalse(SceneSession.TryCreate(invalid, Fixtures.Catalog, out var session, out var validation));
            Assert.IsNull(session);
            Assert.IsTrue(validation.Has(ValidationCodes.BlockerOverlapsPayload));
        }
    }
}
