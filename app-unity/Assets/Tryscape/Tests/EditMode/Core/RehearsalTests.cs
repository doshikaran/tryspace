using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tryscape.Application.Editing;
using Tryscape.Application.Rehearsal;
using Tryscape.Content.MovingDay;
using Tryscape.Core.Canonical;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;
using ModelPose = Tryscape.Core.Model.Pose;

namespace Tryscape.Tests.EditMode.Core
{
    sealed class FixedClock : IUtcClock
    {
        public FixedClock(DateTime utc) => UtcNow = utc;
        public DateTime UtcNow { get; set; }
    }

    /// <summary>Edit -> evaluate cycles (demo-scope D06, AC07-AC10, AC13-AC14) through the application use case.</summary>
    public sealed class RehearsalSessionTests
    {
        EditingSession _editing;
        RehearsalSession _rehearsal;
        FixedClock _clock;

        [SetUp]
        public void SetUp()
        {
            _editing = new EditingSession(Fixtures.Session(), new SequenceIds());
            _clock = new FixedClock(new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc));
            _rehearsal = new RehearsalSession(_editing, new SequenceIds(), _clock);
        }

        [Test]
        public void MovingTheConsoleClearsTheRouteAndUndoRestoresTheOriginalObstruction()
        {
            var first = _rehearsal.Rehearse();
            Assert.AreEqual(EvaluationOutcome.BlockedInModel, first.Evidence.Outcome);
            Assert.AreEqual(Fixtures.ConsoleId, first.Evidence.FirstViolation.EntityIds.Single());

            Assert.IsTrue(_editing.Move(Fixtures.ConsoleId, 2500, 800, _editing.Current.Revision).Accepted);
            Assert.IsTrue(_rehearsal.IsLatestStale, "an accepted edit makes the live result stale (AC14)");

            var moved = _rehearsal.Rehearse();
            Assert.IsFalse(_rehearsal.IsLatestStale);
            Assert.AreEqual(EvaluationOutcome.ClearInModel, moved.Evidence.Outcome);
            Assert.AreEqual(4300, moved.Evidence.RouteLengthMm);
            Assert.AreEqual(1, moved.Evidence.SceneRevision);

            Assert.IsTrue(_editing.Undo().Accepted);
            var undone = _rehearsal.Rehearse();
            Assert.AreEqual(2, undone.Evidence.SceneRevision, "undo is a new revision");
            Assert.AreEqual(first.Evidence.FirstViolation, undone.Evidence.FirstViolation, "same constraint outcome after undo");
            Assert.AreNotEqual(first.Evidence.SceneHash, undone.Evidence.SceneHash, "the scene hash includes the revision");

            // Earlier runs are immutable snapshots, unaffected by later edits.
            Assert.AreEqual(EvaluationOutcome.BlockedInModel, first.Evidence.Outcome);
            Assert.AreEqual(2500, first.Scene.FindEntity(Fixtures.ConsoleId).Pose.ZMm);
            Assert.AreEqual(3, _rehearsal.History.Count);
        }

        [Test]
        public void ADifferentEditCanBlockTheAlternativeRoute()
        {
            // D06.4 / F04: console to (2500, 4000); Northern becomes blocked on segment 1; Direct stays clear.
            Assert.IsTrue(_editing.Move(Fixtures.ConsoleId, 2500, 4000, _editing.Current.Revision).Accepted);
            Assert.IsTrue(_editing.SelectRoute(MovingDayPack.NorthRouteId).Accepted);
            var north = _rehearsal.Rehearse();
            Assert.AreEqual(EvaluationOutcome.BlockedInModel, north.Evidence.Outcome);
            Assert.AreEqual(1, north.Evidence.FirstViolation.SegmentIndex);
            Assert.AreEqual(Fixtures.ConsoleId, north.Evidence.FirstViolation.EntityIds.Single());
            StringAssert.Contains("console table", Explanation.For(north).Reason);

            Assert.IsTrue(_editing.SelectRoute(MovingDayPack.DirectRouteId).Accepted);
            Assert.AreEqual(EvaluationOutcome.ClearInModel, _rehearsal.Rehearse().Evidence.Outcome);
        }

        [Test]
        public void AnUnrelatedEditDoesNotChangeTheConstraintOutcome()
        {
            var before = _rehearsal.Rehearse().Evidence;
            _editing.SelectRoute(MovingDayPack.NorthRouteId);
            _editing.SelectRoute(MovingDayPack.DirectRouteId);
            var after = _rehearsal.Rehearse().Evidence;
            Assert.AreEqual(before.FirstViolation, after.FirstViolation);
            Assert.AreEqual(before.Outcome, after.Outcome);
        }

        [Test]
        public void RepeatedEvaluationGivesIdenticalEvidenceWithDifferentEnvelopes()
        {
            var a = _rehearsal.Rehearse();
            _clock.UtcNow = _clock.UtcNow.AddMinutes(7);
            var b = _rehearsal.Rehearse();

            Assert.AreNotEqual(a.Result.RunId, b.Result.RunId);
            Assert.AreNotEqual(a.Result.CreatedAtUtc, b.Result.CreatedAtUtc);
            CollectionAssert.AreEqual(a.Evidence.CanonicalBytes(), b.Evidence.CanonicalBytes());
            Assert.AreEqual(a.Evidence.EvidenceHash(), b.Evidence.EvidenceHash());

            var evidenceText = Encoding.UTF8.GetString(a.Evidence.CanonicalBytes());
            StringAssert.DoesNotContain(SceneCanonicalizer.Uuid(a.Result.RunId), evidenceText);
            StringAssert.DoesNotContain("2026-11-05", evidenceText);
            StringAssert.DoesNotContain("run_id", evidenceText);

            var wire = CanonicalJson.ToText(a.Result.ToWire());
            StringAssert.Contains("\"run_id\":\"" + SceneCanonicalizer.Uuid(a.Result.RunId) + "\"", wire);
            StringAssert.Contains("\"created_at\":\"2026-11-05T09:00:00Z\"", wire);
        }

        [Test]
        public void WireResultMatchesTheContractFieldSet()
        {
            var wire = JObject.Parse(CanonicalJson.ToText(_rehearsal.Rehearse().Result.ToWire()));
            var schema = JObject.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(Fixtures.Root, "schemas", "evaluation_result.schema.json")));
            CollectionAssert.AreEquivalent(((JArray)schema["required"]).Select(t => (string)t), wire.Properties().Select(p => p.Name));
            var violationFields = ((JArray)schema["$defs"]["violation"]["required"]).Select(t => (string)t);
            CollectionAssert.AreEquivalent(violationFields, ((JObject)wire["first_violation"]).Properties().Select(p => p.Name));
            Assert.AreEqual("blocker_obstructs_route", (string)wire["first_violation"]["message_key"]);
        }

        [Test]
        public void ExplanationNamesTheModelledReasonFromEvidence()
        {
            var blocked = Explanation.For(_rehearsal.Rehearse());
            Assert.AreEqual("Blocked in this model", blocked.Headline);
            StringAssert.Contains("the console table obstructs the delivery sofa's modelled route", blocked.Reason.ToLowerInvariant());
            StringAssert.Contains("25 mm", blocked.Reason);
            StringAssert.Contains("Northern approach", blocked.Action);
            StringAssert.DoesNotContain("wall", blocked.Reason);

            _editing.Move(Fixtures.ConsoleId, 2500, 800, _editing.Current.Revision);
            var clear = Explanation.For(_rehearsal.Rehearse());
            Assert.AreEqual("Clear in this model", clear.Headline);
            StringAssert.Contains("4.3 m", clear.Observed);
            StringAssert.DoesNotContain("safe", (clear.Observed + clear.Reason + clear.Action).ToLowerInvariant());
        }
    }

    /// <summary>S09.1 canonical bytes and hashes, cross-checked with the Python reference serializer.</summary>
    public sealed class CanonicalHashTests
    {
        static JObject Pinned => JObject.Parse(Fixtures.Text("canonical/hashes.json"));

        [Test]
        public void F01CanonicalBytesMatchThePythonReferenceExactly()
        {
            Assert.AreEqual(CanonicalJson.SerializerVersion, (string)Pinned["serializer_version"]);
            Assert.AreEqual((string)Pinned["f01_canonical_scene"], Encoding.UTF8.GetString(SceneCanonicalizer.CanonicalBytes(Fixtures.F01)));
        }

        [Test]
        public void EverySceneHashMatchesThePinnedCrossRuntimeValue()
        {
            var hashes = (JObject)Pinned["scene_hashes"];
            Assert.That(hashes.Count, Is.GreaterThanOrEqualTo(11));
            foreach (var pair in hashes)
                Assert.AreEqual((string)pair.Value, SceneCanonicalizer.SceneHash(Fixtures.Scene("scenes/valid/" + pair.Key)), pair.Key);
        }

        [Test]
        public void EveryEvidenceHashMatchesThePinnedCrossRuntimeValue()
        {
            var pinned = (JObject)Pinned["evidence_hashes"];
            var cases = (JArray)JObject.Parse(Fixtures.Text("evaluation/expectations.json"))["cases"];
            Assert.AreEqual(cases.Count, pinned.Count);
            Assert.AreEqual(RouteEvaluator.Version, (string)Pinned["evaluator_version"]);
            foreach (var item in cases)
            {
                var id = (string)item["id"];
                var evidence = RouteEvaluator.Evaluate(Fixtures.Scene((string)item["scene"]), Fixtures.Catalog, (string)item["route_id"]);
                Assert.AreEqual((string)pinned[id], evidence.EvidenceHash(), id);
            }
        }

        [Test]
        public void HashIgnoresArrayOrderAndAuditTimestampsButNotContent()
        {
            var f01 = Fixtures.F01;
            var reordered = new SceneSpec(f01.SceneId, f01.ProjectId, f01.ScenarioPackId, f01.ScenarioPackVersion, f01.CatalogVersion,
                f01.Revision, f01.Room, f01.Entities.Reverse(), f01.Routes.Reverse(), f01.ActiveRouteId, f01.Assumptions,
                f01.Provenance, f01.MeasurementSources);
            Assert.AreEqual(SceneCanonicalizer.SceneHash(f01), SceneCanonicalizer.SceneHash(reordered));
            Assert.AreNotEqual(SceneCanonicalizer.SceneHash(f01), SceneCanonicalizer.SceneHash(f01.WithRevision(1)));
            Assert.AreNotEqual(SceneCanonicalizer.SceneHash(f01), SceneCanonicalizer.SceneHash(f01.WithActiveRoute("north_detour")));
            Assert.AreNotEqual(SceneCanonicalizer.SceneHash(f01),
                SceneCanonicalizer.SceneHash(f01.WithEntityPose(Fixtures.ConsoleId, new ModelPose(2500, 2550, 0))));

            var measured = Fixtures.Scene("scenes/valid/user_measured_room.json");
            var restamped = new SceneSpec(measured.SceneId, measured.ProjectId, measured.ScenarioPackId, measured.ScenarioPackVersion,
                measured.CatalogVersion, measured.Revision, measured.Room, measured.Entities, measured.Routes, measured.ActiveRouteId,
                measured.Assumptions, measured.Provenance,
                measured.MeasurementSources.Select(m => new MeasurementSource(m.FieldPath, "2027-01-01T00:00:00Z")));
            Assert.AreEqual(SceneCanonicalizer.SceneHash(measured), SceneCanonicalizer.SceneHash(restamped));
        }

        [Test]
        public void LabelsAreNormalisedToNfcAndEscapedDeterministically()
        {
            var composed = Label("Café \"table\" \\ x");
            var decomposed = Label("Café \"table\" \\ x");
            Assert.AreEqual(SceneCanonicalizer.SceneHash(composed), SceneCanonicalizer.SceneHash(decomposed));
            StringAssert.Contains("\"label\":\"Café \\\"table\\\" \\\\ x\"", Encoding.UTF8.GetString(SceneCanonicalizer.CanonicalBytes(composed)));
        }

        [Test]
        public void CanonicalTextHasNoWhitespaceAndSortedKeys()
        {
            var text = Encoding.UTF8.GetString(SceneCanonicalizer.CanonicalBytes(Fixtures.F01));
            StringAssert.StartsWith("{\"active_route_id\":", text);
            StringAssert.DoesNotContain("\n", text);
            StringAssert.DoesNotContain("\": ", text);
            Assert.Less(text.IndexOf("\"catalog_version\"", StringComparison.Ordinal), text.IndexOf("\"entities\"", StringComparison.Ordinal));
        }

        static SceneSpec Label(string label)
        {
            var f01 = Fixtures.F01;
            var entities = f01.Entities.Select(e => e.Id == Fixtures.ConsoleId
                ? new EntitySpec(e.Id, e.CatalogId, e.Role, e.Pose, e.Editable, label)
                : e);
            return new SceneSpec(f01.SceneId, f01.ProjectId, f01.ScenarioPackId, f01.ScenarioPackVersion, f01.CatalogVersion,
                f01.Revision, f01.Room, entities, f01.Routes, f01.ActiveRouteId, f01.Assumptions, f01.Provenance, f01.MeasurementSources);
        }
    }

    /// <summary>20 Hz logical playback (S08.3): exact stop positions, frame-rate independent display.</summary>
    public sealed class PlaybackTimelineTests
    {
        static PlaybackTimeline Timeline(string sceneFile, string routeId)
        {
            var scene = Fixtures.Scene($"scenes/valid/{sceneFile}.json");
            var evidence = RouteEvaluator.Evaluate(scene, Fixtures.Catalog, routeId);
            return new PlaybackTimeline(scene.FindRoute(routeId), evidence);
        }

        [Test]
        public void BlockedPlaybackStopsAtTheModelledContactCentre()
        {
            // F01: contact centre (1275, 2500) is 275 mm along the route; at 250 mm/s and 20 Hz each tick is 12.5 mm,
            // so the stop is reached at tick 275 / 12.5 = 22. The sofa footprint there still has the 25 mm margin
            // to the console: playback never shows penetration.
            var t = Timeline("f01_direct_blocked", "direct");
            Assert.IsFalse(t.ReachesDestination);
            Assert.AreEqual(Rational.FromInteger(275), t.StopDistanceMm);
            Assert.AreEqual(22, t.TickCount);
            Assert.AreEqual((Rational.FromInteger(1100), Rational.FromInteger(2500)), t.PositionAtTick(8));
            Assert.AreEqual((Rational.FromInteger(1275), Rational.FromInteger(2500)), t.PositionAtTick(22));
            Assert.AreEqual((Rational.FromInteger(1275), Rational.FromInteger(2500)), t.PositionAtTick(10_000), "clamped at the stop");
        }

        [Test]
        public void ClearPlaybackTurnsTheCornerAndReachesTheDestination()
        {
            // F02 direct: 3000 + 1300 = 4300 mm -> 344 ticks; the corner (4000, 2500) is at 3000 mm = tick 240.
            var t = Timeline("f02_console_parked_south", "direct");
            Assert.IsTrue(t.ReachesDestination);
            Assert.AreEqual(344, t.TickCount);
            Assert.AreEqual((Rational.FromInteger(4000), Rational.FromInteger(2500)), t.PositionAtTick(240));
            Assert.AreEqual((Rational.FromInteger(4000), new Rational(5025, 2)), t.PositionAtTick(241)); // 12.5 mm north
            Assert.AreEqual((Rational.FromInteger(4000), Rational.FromInteger(3800)), t.PositionAtTick(344));
        }

        [Test]
        public void SecondSegmentStopUsesCumulativeDistance()
        {
            // F04 north detour: segment 0 is 1500 mm, then 275 mm into segment 1 -> 1775 mm; ceil(1775 / 12.5) = 142.
            var t = Timeline("f04_console_north_blocks_detour", "north_detour");
            Assert.AreEqual(Rational.FromInteger(1775), t.StopDistanceMm);
            Assert.AreEqual(142, t.TickCount);
            Assert.AreEqual((Rational.FromInteger(1275), Rational.FromInteger(4000)), t.PositionAtTick(142));
        }

        [TestCase(30)]
        [TestCase(72)]
        [TestCase(90)]
        [TestCase(7)]
        public void DisplayPositionDependsOnLogicalTimeNotFrameRate(int fps)
        {
            var t = Timeline("f02_console_parked_south", "direct");
            var elapsed = 0.0;
            (double x, double z) last = (0, 0);
            var frames = (int)Math.Ceiling((t.DurationSeconds + 1) * fps);
            for (var i = 0; i < frames; i++)
            {
                elapsed = (i + 1) / (double)fps; // recomputed from the frame index; no accumulated drift
                last = t.DisplayPositionAt(elapsed);
            }
            Assert.AreEqual(4000, last.x, 1e-9);
            Assert.AreEqual(3800, last.z, 1e-9);
            var mid = t.DisplayPositionAt(6.0); // 1500 mm along segment 0
            Assert.AreEqual(2500, mid.xMm, 1e-9);
            Assert.AreEqual(2500, mid.zMm, 1e-9);
        }

        [Test]
        public void InvalidResultsCannotBeReplayed()
        {
            var scene = Scenes.F01WithConsoleAt(2500, 800, 45);
            var evidence = RouteEvaluator.Evaluate(scene, Fixtures.Catalog, "direct");
            Assert.Throws<ArgumentException>(() => new PlaybackTimeline(scene.FindRoute("direct"), evidence));
        }
    }

    /// <summary>Exact rational helper used by the evaluator.</summary>
    public sealed class RationalTests
    {
        [Test]
        public void NormalisesAndComparesExactly()
        {
            Assert.AreEqual(new Rational(11, 120), new Rational(550, 6000));
            Assert.AreEqual(new Rational(-1, 2), new Rational(1, -2));
            Assert.AreEqual(1, new Rational(-1, -2).Numerator);
            Assert.IsTrue(new Rational(163, 164) < Rational.One);
            Assert.IsTrue(new Rational(1, 3) > new Rational(333333, 1000000));
            Assert.AreEqual(Rational.Zero, default(Rational));
            Assert.Throws<DivideByZeroException>(() => new Rational(1, 0));
        }
    }
}
