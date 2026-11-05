using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tryscape.Content.MovingDay;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;
using ModelPose = Tryscape.Core.Model.Pose;

namespace Tryscape.Tests.EditMode.Core
{
    /// <summary>
    /// Evaluator against independently derived expectations (contracts/v1/fixtures/evaluation/expectations.json,
    /// transcribed from demo-scope D06 / S08.2, never generated from this evaluator) plus hand-derived edge cases.
    /// Expected numbers in this file are worked out in comments from catalog dimensions, not from the evaluator.
    /// </summary>
    public sealed class RouteEvaluatorTests
    {
        static readonly Guid SecondConsole = Guid.Parse("30000000-0000-4000-8000-000000000003");

        // ---- shared fixture expectations (the eleven D06/S08.2 cases) ----

        [Test]
        public void ReproducesEveryIndependentExpectation()
        {
            var doc = JObject.Parse(Fixtures.Text("evaluation/expectations.json"));
            Assert.AreEqual(25, (int)doc["model_margin_mm"]);
            var cases = (JArray)doc["cases"];
            Assert.AreEqual(11, cases.Count, "expected the eleven shared cases");
            foreach (var item in cases)
            {
                var id = (string)item["id"];
                var scene = Fixtures.Scene((string)item["scene"]);
                var evidence = RouteEvaluator.Evaluate(scene, Fixtures.Catalog, (string)item["route_id"]);
                Assert.AreEqual((string)item["outcome"], EvaluationEvidence.OutcomeText(evidence.Outcome), id);
                Assert.AreEqual((long)item["route_length_mm"], evidence.RouteLengthMm, id + " length");
                Assert.IsNotNull(evidence.SceneHash, id);
                var want = item["first_violation"];
                if (want.Type == JTokenType.Null)
                {
                    Assert.IsNull(evidence.FirstViolation, $"{id}: unexpected {evidence.FirstViolation}");
                    continue;
                }
                var v = evidence.FirstViolation;
                Assert.IsNotNull(v, id);
                Assert.AreEqual((string)want["rule_code"], v.RuleCode, id);
                CollectionAssert.AreEqual(((JArray)want["entity_ids"]).Select(t => Guid.Parse((string)t)).ToArray(), v.EntityIds.ToArray(), id);
                Assert.AreEqual((int)want["segment_index"], v.SegmentIndex, id);
                Assert.AreEqual(R(want["entry_fraction"]), v.EntryFraction, id + " fraction");
                Assert.AreEqual(R(want["contact_center"]["x_mm"]), v.ContactXMm, id + " contact x");
                Assert.AreEqual(R(want["contact_center"]["z_mm"]), v.ContactZMm, id + " contact z");
                Assert.AreEqual(25, v.ModelMarginMm, id);
            }
        }

        [Test]
        public void ResultsDoNotDependOnEntityOrRouteArrayOrder()
        {
            foreach (var file in new[] { "f01_direct_blocked", "f04_console_north_blocks_detour", "boundary_wall_violation" })
            {
                var scene = Fixtures.Scene($"scenes/valid/{file}.json");
                var reordered = Rebuild(scene, entities: scene.Entities.Reverse(), routes: scene.Routes.Reverse());
                foreach (var route in scene.Routes)
                {
                    var a = RouteEvaluator.Evaluate(scene, Fixtures.Catalog, route.Id);
                    var b = RouteEvaluator.Evaluate(reordered, Fixtures.Catalog, route.Id);
                    CollectionAssert.AreEqual(a.CanonicalBytes(), b.CanonicalBytes(), $"{file}/{route.Id}");
                }
            }
        }

        // ---- earliest obstruction and tie-breaking ----

        [Test]
        public void EarliestObstructionWinsEvenWhenListedLater()
        {
            // Two consoles on the direct route's first segment (z = 2500). Expanded x half-width 300 + 900 + 25 = 1225.
            // Near console at x = 2500 -> entry x 1275, t = 275/3000 = 11/120.
            // Far console at x = 3200 (footprints [2200,2800] and [2900,3500] do not overlap) -> entry x 1975,
            // t = 975/3000 = 13/40. The near one must be reported although it is listed second and has the larger ID.
            var nearId = Guid.Parse("30000000-0000-4000-8000-0000000000ff");
            var scene = Scenes.F01With(
                new EntitySpec(SecondConsole, MovingDayPack.ConsoleCatalogId, EntityRole.Blocker, new ModelPose(3200, 2500, 0), true, "Far console"),
                new EntitySpec(nearId, MovingDayPack.ConsoleCatalogId, EntityRole.Blocker, new ModelPose(2500, 2500, 0), true, "Near console"));
            var v = RouteEvaluator.Evaluate(scene, Fixtures.Catalog, "direct").FirstViolation;
            Assert.AreEqual(nearId, v.EntityIds.Single());
            Assert.AreEqual(new Rational(11, 120), v.EntryFraction);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EqualEntryFractionsBreakTiesByEntityIdNotListOrder(bool swapListOrder)
        {
            // Consoles south (z 1700) and north (z 3300) of the route line, same x = 2500, footprints z [1200,2200]
            // and [2800,3800]: no overlap. Expanded z half-depth 500 + 400 + 25 = 925: [775,2625] and [2375,4225]
            // both contain z = 2500, so both are first touched at x = 1275 (t = 11/120). Lower UUID wins.
            var low = new EntitySpec(Fixtures.ConsoleId, MovingDayPack.ConsoleCatalogId, EntityRole.Blocker, new ModelPose(2500, 3300, 0), true, "North console");
            var high = new EntitySpec(SecondConsole, MovingDayPack.ConsoleCatalogId, EntityRole.Blocker, new ModelPose(2500, 1700, 0), true, "South console");
            var scene = swapListOrder ? Scenes.F01With(high, low) : Scenes.F01With(low, high);
            var v = RouteEvaluator.Evaluate(scene, Fixtures.Catalog, "direct").FirstViolation;
            Assert.AreEqual(Fixtures.ConsoleId, v.EntityIds.Single());
            Assert.AreEqual(new Rational(11, 120), v.EntryFraction);
            Assert.AreEqual(Rational.FromInteger(1275), v.ContactXMm);
        }

        [Test]
        public void LaterSegmentObstructionIsNotReportedWhenAnEarlierSegmentIsBlocked()
        {
            // F01 direct is blocked on segment 0; a second console placed across segment 1 (x = 4000, z 2500->3800)
            // must not be reported. Console at (4700, 3500): footprint x [4400,5000] expanded by 925 -> [3475,5925]
            // contains x = 4000; z [3000,4000] expanded by 425 -> [2575,4425], above segment 0's z = 2500 but crossing
            // segment 1's z range [2500,3800]. Its lower UUID must not let it win over the earlier segment.
            var scene = Scenes.F01With(
                new EntitySpec(Fixtures.ConsoleId, MovingDayPack.ConsoleCatalogId, EntityRole.Blocker, new ModelPose(2500, 2500, 0), true, "Console table"),
                new EntitySpec(Guid.Parse("30000000-0000-4000-8000-000000000000"), MovingDayPack.ConsoleCatalogId, EntityRole.Blocker, new ModelPose(4700, 3500, 0), true, "Second console"));
            var v = RouteEvaluator.Evaluate(scene, Fixtures.Catalog, "direct").FirstViolation;
            Assert.AreEqual(0, v.SegmentIndex);
            Assert.AreEqual(Fixtures.ConsoleId, v.EntityIds.Single(), "the lower-ID console on segment 1 must not win");
        }

        // ---- boundary contact and margin ----

        [Test]
        public void CornerTangencyIsBlocked()
        {
            // Console at (2500, 3425) touches z = 2500 with its expanded z min (3425 - 500 - 400 - 25 = 2500), as B01.
            // Shift it east so the payload's segment 1 (x = 4000) touches its expanded x min exactly at the corner:
            // expanded x min = cx - 300 - 900 - 25 = 4000 -> cx = 5225; footprint x [4925,5525] inside the 6000 room.
            // Segment 0 ends at (4000, 2500) = the expanded rectangle's south-west corner: t = 1 on segment 0.
            var scene = Scenes.F01WithConsoleAt(5225, 3425);
            var v = RouteEvaluator.Evaluate(scene, Fixtures.Catalog, "direct").FirstViolation;
            Assert.IsNotNull(v, "touching an expanded corner is contact");
            Assert.AreEqual(RuleCodes.BlockerIntersection, v.RuleCode);
            Assert.AreEqual(0, v.SegmentIndex);
            Assert.AreEqual(Rational.One, v.EntryFraction);
            Assert.AreEqual((Rational.FromInteger(4000), Rational.FromInteger(2500)), (v.ContactXMm, v.ContactZMm));
        }

        [Test]
        public void OneMillimetrePastTheCornerIsClear()
        {
            var v = RouteEvaluator.Evaluate(Scenes.F01WithConsoleAt(5226, 3426), Fixtures.Catalog, "direct");
            Assert.AreEqual(EvaluationOutcome.ClearInModel, v.Outcome, v.FirstViolation?.ToString());
        }

        [Test]
        public void HalfMillimetreFootprintBoundsStayExact()
        {
            // A rotated console (yaw 90) swaps to 1000 (x) by 600 (z) mm, with odd centre coordinates.
            // Console at (2501, 3226) yaw 90: z extent 600 -> expanded
            // z min = 3226 - 300 - 400 - 25 = 2501 > 2500: clear. At z 3225: 2500 -> blocked (tangent).
            var clear = Scenes.F01WithConsoleAt(2501, 3226, 90);
            var touching = Scenes.F01WithConsoleAt(2501, 3225, 90);
            Assert.AreEqual(EvaluationOutcome.ClearInModel, RouteEvaluator.Evaluate(clear, Fixtures.Catalog, "direct").Outcome);
            var v = RouteEvaluator.Evaluate(touching, Fixtures.Catalog, "direct").FirstViolation;
            // expanded x min = 2501 - 500 - 900 - 25 = 1076 -> t = 76/3000 = 19/750
            Assert.AreEqual(new Rational(19, 750), v.EntryFraction);
        }

        [Test]
        public void ObstacleWithinMarginOfTheStartIsInvalidStart()
        {
            // Payload footprint x max 1900. Console at x = 2210: footprint x min 1910, a 10 mm gap (< 25 mm margin),
            // edge-separated so the scene is valid, but the start already lies in the expanded rectangle.
            var scene = Scenes.F01WithConsoleAt(2210, 2500);
            var evidence = RouteEvaluator.Evaluate(scene, Fixtures.Catalog, "direct");
            Assert.AreEqual(EvaluationOutcome.BlockedInModel, evidence.Outcome);
            Assert.AreEqual(RuleCodes.InvalidStart, evidence.FirstViolation.RuleCode);
            Assert.AreEqual(MessageKeys.StartObstructed, evidence.FirstViolation.MessageKey);
            Assert.AreEqual(Rational.Zero, evidence.FirstViolation.EntryFraction);
        }

        [Test]
        public void ExactWallMarginOnTheNorthSideIsAcceptedAndOneMoreMillimetreIsNot()
        {
            // Depth 5000: permitted centre z max = 5000 - 400 - 25 = 4575.
            Assert.AreEqual(EvaluationOutcome.ClearInModel,
                RouteEvaluator.Evaluate(Scenes.NorthOnly(4575), Fixtures.Catalog, "north_only").Outcome);
            var v = RouteEvaluator.Evaluate(Scenes.NorthOnly(4576), Fixtures.Catalog, "north_only").FirstViolation;
            Assert.AreEqual(RuleCodes.RoomBoundary, v.RuleCode);
            Assert.AreEqual(MessageKeys.RouteLeavesRoom, v.MessageKey);
            Assert.IsEmpty(v.EntityIds);
            // segment 0 runs z 2500 -> 4576; last inside at 4575: t = 2075/2076
            Assert.AreEqual(new Rational(2075, 2076), v.EntryFraction);
            Assert.AreEqual(Rational.FromInteger(4575), v.ContactZMm);
        }

        [Test]
        public void DecorationsAndUnknownCollisionDoNotObstruct()
        {
            var catalog = Scenes.CatalogWith(new CatalogEntry("rug", "Rug", 1500, 1500, 10, CollisionParticipation.None,
                new[] { EntityRole.Decoration }, "rug", 50, new[] { 0 }, "test"));
            var scene = Scenes.F01With(new EntitySpec(SecondConsole, "rug", EntityRole.Decoration, new ModelPose(2500, 2500, 0), false, "Rug"));
            Assert.AreEqual(EvaluationOutcome.ClearInModel, RouteEvaluator.Evaluate(scene, catalog, "direct").Outcome);
        }

        // ---- thin obstacles that frame sampling misses ----

        [Test]
        public void ThinObstacleBetweenSampledPositionsIsStillBlocked()
        {
            // Test-only catalog: a 20 mm square payload and a 10 mm thin panel. The payload centre touches the panel's
            // expanded rectangle within 10/2 + 20/2 + 25 = 40 mm of the panel centre: x in [2510, 2590] for x = 2550.
            // A frame-sampled check at 5 Hz (e.g. a 200 ms hitch) at 500 mm/s samples every 100 mm from x = 1000,
            // so samples 2500 and 2600 straddle the panel and neither sees it.
            var catalog = Scenes.ThinCatalog();
            var scene = Scenes.ThinPanelScene(panelX: 2550);
            var evidence = RouteEvaluator.Evaluate(scene, catalog, "direct");

            Assert.IsFalse(NaiveSampledHit(scene, catalog, stepMm: 100), "precondition: sampling misses the panel");
            Assert.AreEqual(EvaluationOutcome.BlockedInModel, evidence.Outcome);
            // entry at x = 2550 - 40 = 2510 -> t = 1510/3000 = 151/300
            Assert.AreEqual(new Rational(151, 300), evidence.FirstViolation.EntryFraction);
            Assert.AreEqual(Rational.FromInteger(2510), evidence.FirstViolation.ContactXMm);
        }

        /// <summary>A deliberately naive frame-sampled overlap check, used only to prove the precondition.</summary>
        static bool NaiveSampledHit(SceneSpec scene, Catalog catalog, int stepMm)
        {
            var route = scene.FindRoute("direct");
            catalog.TryGet(scene.FindEntity(route.PayloadEntityId).CatalogId, out var payload);
            for (var s = 1; s < route.Waypoints.Count; s++)
            {
                var a = route.Waypoints[s - 1];
                var b = route.Waypoints[s];
                var len = Math.Abs(b.XMm - a.XMm) + Math.Abs(b.ZMm - a.ZMm);
                for (var d = 0; d <= len; d += stepMm)
                {
                    var x = a.XMm + Math.Sign(b.XMm - a.XMm) * d;
                    var z = a.ZMm + Math.Sign(b.ZMm - a.ZMm) * d;
                    var p = Footprints.Of(payload, new ModelPose(x, z, 0));
                    var grown = new Rect2(p.MinX2 - 50, p.MinZ2 - 50, p.MaxX2 + 50, p.MaxZ2 + 50);
                    foreach (var e in scene.Entities.Where(e => e.Role == EntityRole.Blocker))
                    {
                        catalog.TryGet(e.CatalogId, out var entry);
                        if (grown.OverlapsArea(Footprints.Of(entry, e.Pose)))
                            return true;
                    }
                }
            }
            return false;
        }

        // ---- invalid and unsupported inputs ----

        [Test]
        public void ArbitraryYawIsInvalidInputWithoutAHash()
        {
            var scene = Scenes.F01WithConsoleAt(2500, 800, 45);
            var evidence = RouteEvaluator.Evaluate(scene, Fixtures.Catalog, "direct");
            Assert.AreEqual(EvaluationOutcome.InvalidInput, evidence.Outcome);
            Assert.IsNull(evidence.SceneHash);
            Assert.IsNull(evidence.RouteLengthMm);
            Assert.IsTrue(evidence.ValidationErrors.Any(e => e.Code == "yaw_not_allowed"));
        }

        [Test]
        public void DiagonalSegmentIsInvalidInput()
        {
            var f01 = Fixtures.F01;
            var diagonal = new RouteSpec("direct", "Direct approach", Fixtures.SofaId, 250,
                new[] { new Waypoint(1000, 2500), new Waypoint(4000, 3800) });
            var north = f01.FindRoute("north_detour");
            var scene = Rebuild(f01, routes: new[] { diagonal, north });
            var evidence = RouteEvaluator.Evaluate(scene, Fixtures.Catalog, "direct");
            Assert.AreEqual(EvaluationOutcome.InvalidInput, evidence.Outcome);
            Assert.IsTrue(evidence.ValidationErrors.Any(e => e.Code == "route_segment_not_axis_aligned"));
        }

        [Test]
        public void UnknownRouteIsInvalidInput()
        {
            var evidence = RouteEvaluator.Evaluate(Fixtures.F01, Fixtures.Catalog, "via_roof");
            Assert.AreEqual(EvaluationOutcome.InvalidInput, evidence.Outcome);
            Assert.AreEqual("unknown_route", evidence.ValidationErrors.Single().Code);
        }

        [Test]
        public void EvidenceRefusesContractViolatingCombinations()
        {
            Assert.Throws<ArgumentException>(() => new EvaluationEvidence(Fixtures.SceneId, 0, "ab", "direct", "1.0.0",
                "1.0.0", EvaluationOutcome.InvalidInput, null, null, Array.Empty<Assumption>(),
                new[] { new Tryscape.Core.Validation.ValidationError("x", "y") }));
            Assert.Throws<ArgumentException>(() => new EvaluationEvidence(Fixtures.SceneId, 0, "ab", "direct", "1.0.0",
                "1.0.0", EvaluationOutcome.BlockedInModel, 4300, null, Array.Empty<Assumption>(),
                Array.Empty<Tryscape.Core.Validation.ValidationError>()));
        }

        // ---- property-style checks ----

        [Test]
        public void AddingABlockerNeverTurnsABlockedRouteClear()
        {
            var baseline = RouteEvaluator.Evaluate(Fixtures.F01, Fixtures.Catalog, "direct");
            Assert.AreEqual(EvaluationOutcome.BlockedInModel, baseline.Outcome);
            var evaluated = 0;
            for (var x = 2900; x <= 5500; x += 150)
                for (var z = 600; z <= 4400; z += 400)
                {
                    var extra = new EntitySpec(SecondConsole, MovingDayPack.ConsoleCatalogId, EntityRole.Blocker, new ModelPose(x, z, 0), true, "Extra");
                    var scene = Scenes.F01With(Fixtures.F01.FindEntity(Fixtures.ConsoleId), extra);
                    var evidence = RouteEvaluator.Evaluate(scene, Fixtures.Catalog, "direct");
                    if (evidence.Outcome == EvaluationOutcome.InvalidInput)
                        continue; // overlapping placement; not a reachable scene
                    evaluated++;
                    Assert.AreEqual(EvaluationOutcome.BlockedInModel, evidence.Outcome, $"extra console at ({x},{z})");
                    Assert.That(evidence.FirstViolation.EntryFraction, Is.LessThanOrEqualTo(baseline.FirstViolation.EntryFraction).Using<Rational>((a, b) => a.CompareTo(b)));
                }
            Assert.That(evaluated, Is.GreaterThan(50), "the sweep must exercise valid scenes, not skip them all");
        }

        [Test]
        public void SweepingTheConsoleAcrossTheRouteMatchesAnIndependentIntervalDerivation()
        {
            // For a console at (cx, cz) yaw 0, the direct route's segment 0 (z = 2500, x 1000->4000) is touched iff
            // |cz - 2500| <= 925 and cx - 1225 <= 4000 and cx + 1225 >= 1000; segment 1 (x = 4000, z 2500->3800)
            // iff |cx - 4000| <= 1225 and cz + 925 >= 2500 and cz - 925 <= 3800. Derived from catalog sizes alone.
            int blocked = 0, clear = 0;
            for (var cx = 1000; cx <= 5600; cx += 100)
                for (var cz = 600; cz <= 4400; cz += 100)
                {
                    var scene = Scenes.F01WithConsoleAt(cx, cz);
                    var evidence = RouteEvaluator.Evaluate(scene, Fixtures.Catalog, "direct");
                    if (evidence.Outcome == EvaluationOutcome.InvalidInput)
                        continue;
                    var seg0 = Math.Abs(cz - 2500) <= 925 && cx - 1225 <= 4000 && cx + 1225 >= 1000;
                    var seg1 = Math.Abs(cx - 4000) <= 1225 && cz + 925 >= 2500 && cz - 925 <= 3800;
                    var expectBlocked = seg0 || seg1;
                    if (expectBlocked) blocked++; else clear++;
                    Assert.AreEqual(expectBlocked, evidence.Outcome == EvaluationOutcome.BlockedInModel, $"console at ({cx},{cz})");
                    if (seg0)
                        Assert.AreEqual(0, evidence.FirstViolation.SegmentIndex, $"({cx},{cz})");
                    else if (seg1)
                        Assert.AreEqual(1, evidence.FirstViolation.SegmentIndex, $"({cx},{cz})");
                }
            Assert.That(blocked, Is.GreaterThan(100), "sweep covered blocked placements");
            Assert.That(clear, Is.GreaterThan(100), "sweep covered clear placements");
        }

        static Rational R(JToken token) => new Rational((long)token["numerator"], (long)token["denominator"]);

        static SceneSpec Rebuild(SceneSpec s, IEnumerable<EntitySpec> entities = null, IEnumerable<RouteSpec> routes = null) =>
            new SceneSpec(s.SceneId, s.ProjectId, s.ScenarioPackId, s.ScenarioPackVersion, s.CatalogVersion, s.Revision,
                s.Room, entities ?? s.Entities, routes ?? s.Routes, s.ActiveRouteId, s.Assumptions, s.Provenance, s.MeasurementSources);
    }

    /// <summary>Hand-built scenes for edge cases, all derived from F01.</summary>
    public static class Scenes
    {
        public static SceneSpec F01With(params EntitySpec[] blockers)
        {
            var f01 = Fixtures.F01;
            return new SceneSpec(f01.SceneId, f01.ProjectId, f01.ScenarioPackId, f01.ScenarioPackVersion, f01.CatalogVersion,
                f01.Revision, f01.Room, new[] { f01.FindEntity(Fixtures.SofaId) }.Concat(blockers), f01.Routes,
                f01.ActiveRouteId, f01.Assumptions, f01.Provenance, f01.MeasurementSources);
        }

        public static SceneSpec F01WithConsoleAt(int x, int z, int yaw = 0) =>
            F01With(new EntitySpec(Fixtures.ConsoleId, MovingDayPack.ConsoleCatalogId, EntityRole.Blocker, new ModelPose(x, z, yaw), true, "Console table"));

        /// <summary>F01 room, console parked south, a single route heading north to z = <paramref name="endZ"/>.</summary>
        public static SceneSpec NorthOnly(int endZ)
        {
            var f01 = Fixtures.F01;
            var route = new RouteSpec("north_only", "North only", Fixtures.SofaId, 250, new[] { new Waypoint(1000, 2500), new Waypoint(1000, endZ) });
            return new SceneSpec(f01.SceneId, f01.ProjectId, f01.ScenarioPackId, f01.ScenarioPackVersion, f01.CatalogVersion,
                0, f01.Room, new[]
                {
                    f01.FindEntity(Fixtures.SofaId),
                    new EntitySpec(Fixtures.ConsoleId, MovingDayPack.ConsoleCatalogId, EntityRole.Blocker, new ModelPose(4500, 800, 0), true, "Console table"),
                }, new[] { route }, "north_only", f01.Assumptions, f01.Provenance, f01.MeasurementSources);
        }

        public static Catalog CatalogWith(params CatalogEntry[] extra) =>
            new Catalog(MovingDayPack.PackId, MovingDayPack.CatalogVersion, MovingDayPack.CreateCatalog().Entries.Concat(extra));

        public static Catalog ThinCatalog() => new Catalog(MovingDayPack.PackId, MovingDayPack.CatalogVersion, new[]
        {
            new CatalogEntry("test_rod", "Rod", 20, 20, 900, CollisionParticipation.Payload, new[] { EntityRole.Payload }, "rod", 50, new[] { 0 }, "test"),
            new CatalogEntry("test_panel", "Thin panel", 10, 1000, 900, CollisionParticipation.FloorObstruction, new[] { EntityRole.Blocker }, "panel", 50, new[] { 0 }, "test"),
        });

        public static SceneSpec ThinPanelScene(int panelX)
        {
            var f01 = Fixtures.F01;
            var route = new RouteSpec("direct", "Direct approach", Fixtures.SofaId, 500, new[] { new Waypoint(1000, 2500), new Waypoint(4000, 2500) });
            return new SceneSpec(f01.SceneId, f01.ProjectId, f01.ScenarioPackId, f01.ScenarioPackVersion, f01.CatalogVersion,
                0, f01.Room, new[]
                {
                    new EntitySpec(Fixtures.SofaId, "test_rod", EntityRole.Payload, new ModelPose(1000, 2500, 0), false, "Rod"),
                    new EntitySpec(Fixtures.ConsoleId, "test_panel", EntityRole.Blocker, new ModelPose(panelX, 2500, 0), true, "Thin panel"),
                }, new[] { route }, "direct", f01.Assumptions, f01.Provenance, f01.MeasurementSources);
        }
    }
}
