using System;
using System.Collections.Generic;
using System.Linq;
using Tryscape.Core.Canonical;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;
using Tryscape.Core.Validation;

namespace Tryscape.Core.Evaluation
{
    /// <summary>
    /// Deterministic 2D footprint/path checker, evaluator version <see cref="Version"/> (technical-spec S08).
    /// Question answered: does the fixed-orientation payload footprint, plus the model margin, touch any
    /// floor-obstructing blocker or leave the permitted room interior anywhere along the continuous route?
    /// <para>
    /// Method: in doubled millimetres, each blocker footprint is expanded by the payload's extents plus margin
    /// (Minkowski sum) and the payload centre's segment is intersected with that closed rectangle; the room is shrunk
    /// the same way. Everything is integer/rational, so render frame rate, sampling and float rounding cannot change
    /// the outcome. Earliest violation: segment index, then exact fraction, then room before blockers, then blocker
    /// UUID ordinal. Pure: no clock, I/O or Unity dependency.
    /// </para>
    /// </summary>
    public static class RouteEvaluator
    {
        public const string Version = "1.0.0";
        public const int MaxValidationErrors = 32;

        public static EvaluationEvidence Evaluate(SceneSpec scene, Catalog catalog, string routeId)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (catalog == null)
                throw new ArgumentNullException(nameof(catalog));
            if (routeId == null)
                throw new ArgumentNullException(nameof(routeId));

            var validation = SceneValidator.Validate(scene, catalog);
            var route = scene.FindRoute(routeId);
            if (!validation.IsValid || route == null)
            {
                var errors = validation.Errors.ToList();
                if (route == null)
                    errors.Add(new ValidationError("route_id", "unknown_route"));
                return Invalid(scene, routeId, errors);
            }

            var margin = ModelParameters.PayloadMarginMm;
            var payload = scene.FindEntity(route.PayloadEntityId);
            catalog.TryGet(payload.CatalogId, out var payloadEntry);
            var (payloadExtentX, payloadExtentZ) = Footprints.Extents(payloadEntry, payload.Pose.YawDeg);
            Footprints.TryPermittedCenters(scene.Room, payloadEntry, payload.Pose.YawDeg, margin, out var permitted);

            var obstacles = new List<(string orderKey, Guid id, Rect2 expanded)>();
            foreach (var entity in scene.Entities)
            {
                if (entity.Role != EntityRole.Blocker || !catalog.TryGet(entity.CatalogId, out var entry) ||
                    entry.Collision != CollisionParticipation.FloorObstruction)
                    continue;
                var f = Footprints.Of(entry, entity.Pose);
                checked
                {
                    // Doubled units: a payload centre C2 touches the footprint expanded by half-extent + margin when
                    // C2 is within [min2 - extent - 2m, max2 + extent + 2m].
                    long grow = 2L * margin;
                    obstacles.Add((SceneCanonicalizer.Uuid(entity.Id), entity.Id, new Rect2(
                        f.MinX2 - payloadExtentX - grow, f.MinZ2 - payloadExtentZ - grow,
                        f.MaxX2 + payloadExtentX + grow, f.MaxZ2 + payloadExtentZ + grow)));
                }
            }
            obstacles.Sort((a, b) => string.CompareOrdinal(a.orderKey, b.orderKey));

            var length = RouteLengthMm(route);
            var hash = SceneCanonicalizer.SceneHash(scene);

            for (var s = 0; s + 1 < route.Waypoints.Count; s++)
            {
                var a = route.Waypoints[s];
                var b = route.Waypoints[s + 1];
                long ax2 = 2L * a.XMm, az2 = 2L * a.ZMm, bx2 = 2L * b.XMm, bz2 = 2L * b.ZMm;

                // Candidates in tie-break order: room (rank 0) is considered first so that, at an equal fraction,
                // it wins; obstacles are already in UUID order, so strict '<' keeps the first on a tie.
                Rational? best = null;
                Guid? bestEntity = null;
                var bestIsRoom = false;

                var roomExit = RoomExit(ax2, az2, bx2, bz2, permitted);
                if (roomExit.HasValue)
                {
                    best = roomExit;
                    bestIsRoom = true;
                }

                foreach (var obstacle in obstacles)
                {
                    var entry = Entry(ax2, az2, bx2, bz2, obstacle.expanded);
                    if (entry.HasValue && (!best.HasValue || entry.Value < best.Value))
                    {
                        best = entry;
                        bestEntity = obstacle.id;
                        bestIsRoom = false;
                    }
                }

                if (!best.HasValue)
                    continue;

                var t = best.Value;
                var atStart = s == 0 && t == Rational.Zero;
                var rule = atStart ? RuleCodes.InvalidStart : bestIsRoom ? RuleCodes.RoomBoundary : RuleCodes.BlockerIntersection;
                var message = atStart ? MessageKeys.StartObstructed : bestIsRoom ? MessageKeys.RouteLeavesRoom : MessageKeys.BlockerObstructsRoute;
                var violation = new Violation(rule,
                    bestEntity.HasValue ? new[] { bestEntity.Value } : Array.Empty<Guid>(),
                    s, t, PointOnSegmentMm(ax2, bx2, t), PointOnSegmentMm(az2, bz2, t), margin, message);
                return new EvaluationEvidence(scene.SceneId, scene.Revision, hash, route.Id, scene.CatalogVersion, Version,
                    EvaluationOutcome.BlockedInModel, length, violation, scene.Assumptions, Array.Empty<ValidationError>());
            }

            return new EvaluationEvidence(scene.SceneId, scene.Revision, hash, route.Id, scene.CatalogVersion, Version,
                EvaluationOutcome.ClearInModel, length, null, scene.Assumptions, Array.Empty<ValidationError>());
        }

        /// <summary>Sum of axis-aligned segment lengths (S08.3).</summary>
        public static long RouteLengthMm(RouteSpec route)
        {
            long total = 0;
            checked
            {
                for (var i = 1; i < route.Waypoints.Count; i++)
                    total += Math.Abs((long)route.Waypoints[i].XMm - route.Waypoints[i - 1].XMm) +
                             Math.Abs((long)route.Waypoints[i].ZMm - route.Waypoints[i - 1].ZMm);
            }
            return total;
        }

        /// <summary>
        /// Smallest t in [0,1] at which a + (b - a)t lies in the closed rectangle, or null (slab method, exact).
        /// Works for any segment; R0 validation guarantees axis-aligned, nonzero segments.
        /// </summary>
        internal static Rational? Entry(long ax2, long az2, long bx2, long bz2, Rect2 r)
        {
            var lo = Rational.Zero;
            var hi = Rational.One;
            if (!Slab(ax2, bx2, r.MinX2, r.MaxX2, ref lo, ref hi))
                return null;
            if (!Slab(az2, bz2, r.MinZ2, r.MaxZ2, ref lo, ref hi))
                return null;
            return lo;
        }

        static bool Slab(long p0, long p1, long min, long max, ref Rational lo, ref Rational hi)
        {
            var d = checked(p1 - p0);
            if (d == 0)
                return min <= p0 && p0 <= max;
            var t0 = new Rational(checked(min - p0), d);
            var t1 = new Rational(checked(max - p0), d);
            if (t0 > t1)
            {
                var swap = t0;
                t0 = t1;
                t1 = swap;
            }
            if (t0 > lo)
                lo = t0;
            if (t1 < hi)
                hi = t1;
            return lo <= hi;
        }

        /// <summary>
        /// For a segment whose start is inside the closed permitted rectangle: the largest t such that [0, t] stays
        /// inside, if the segment leaves it; null if the end is inside (the rectangle is convex). Exactly reaching the
        /// boundary is accepted (S08.2 wall rule).
        /// </summary>
        internal static Rational? RoomExit(long ax2, long az2, long bx2, long bz2, Rect2 permitted)
        {
            if (permitted.ContainsPoint2(bx2, bz2))
                return null;
            var exit = Rational.One;
            Limit(ax2, bx2, permitted.MinX2, permitted.MaxX2, ref exit);
            Limit(az2, bz2, permitted.MinZ2, permitted.MaxZ2, ref exit);
            return exit;
        }

        static void Limit(long p0, long p1, long min, long max, ref Rational exit)
        {
            var d = checked(p1 - p0);
            Rational? t = null;
            if (d > 0 && p1 > max)
                t = new Rational(checked(max - p0), d);
            else if (d < 0 && p1 < min)
                t = new Rational(checked(min - p0), d);
            if (t.HasValue && t.Value < exit)
                exit = t.Value;
        }

        /// <summary>(p0 + (p1 - p0) t) in doubled units, converted to millimetres as an exact rational.</summary>
        static Rational PointOnSegmentMm(long p0x2, long p1x2, Rational t)
        {
            checked
            {
                var numerator = p0x2 * t.Denominator + (p1x2 - p0x2) * t.Numerator;
                return new Rational(numerator, 2 * t.Denominator);
            }
        }

        static EvaluationEvidence Invalid(SceneSpec scene, string routeId, IEnumerable<ValidationError> errors) =>
            new EvaluationEvidence(scene.SceneId, scene.Revision, null, routeId, scene.CatalogVersion, Version,
                EvaluationOutcome.InvalidInput, null, null, scene.Assumptions, errors.Take(MaxValidationErrors));
    }
}
