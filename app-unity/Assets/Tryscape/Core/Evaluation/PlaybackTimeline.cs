using System;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;

namespace Tryscape.Core.Evaluation
{
    /// <summary>
    /// Deterministic replay timeline for an already-computed result (S08.3). Logical ticks run at <see cref="TickHz"/>;
    /// the payload's distance along the route at tick k is exactly k * speed / 20 mm, clamped to the stop distance
    /// (the destination when clear, the modelled contact position when blocked). Presentation may interpolate
    /// between ticks, but the timeline never feeds back into the result, and there is no accumulated
    /// velocity * deltaTime drift: every position is recomputed from the tick index.
    /// </summary>
    public sealed class PlaybackTimeline
    {
        public const int TickHz = 20;

        readonly RouteSpec _route;
        readonly long[] _cumulative;

        public PlaybackTimeline(RouteSpec route, EvaluationEvidence evidence)
        {
            _route = route ?? throw new ArgumentNullException(nameof(route));
            if (evidence == null)
                throw new ArgumentNullException(nameof(evidence));
            if (evidence.Outcome != EvaluationOutcome.ClearInModel && evidence.Outcome != EvaluationOutcome.BlockedInModel)
                throw new ArgumentException("Only clear or blocked results can be replayed", nameof(evidence));
            if (evidence.RouteId != route.Id)
                throw new ArgumentException("Evidence belongs to a different route", nameof(evidence));
            if (route.SpeedMmPerS <= 0)
                throw new ArgumentOutOfRangeException(nameof(route), "Route speed must be positive");

            _cumulative = new long[route.Waypoints.Count];
            checked
            {
                for (var i = 1; i < route.Waypoints.Count; i++)
                    _cumulative[i] = _cumulative[i - 1] + SegmentLength(i - 1);
            }

            var violation = evidence.FirstViolation;
            if (violation == null)
            {
                StopDistanceMm = Rational.FromInteger(_cumulative[_cumulative.Length - 1]);
                ReachesDestination = true;
            }
            else
            {
                var t = violation.EntryFraction;
                var segment = violation.SegmentIndex;
                checked
                {
                    StopDistanceMm = new Rational(_cumulative[segment] * t.Denominator + SegmentLength(segment) * t.Numerator, t.Denominator);
                }
                ReachesDestination = false;
            }

            checked
            {
                // ceil(stop * TickHz / speed)
                long num = StopDistanceMm.Numerator * TickHz;
                long den = StopDistanceMm.Denominator * route.SpeedMmPerS;
                TickCount = (num + den - 1) / den;
            }
        }

        /// <summary>Distance travelled when playback stops (exact).</summary>
        public Rational StopDistanceMm { get; }

        public bool ReachesDestination { get; }

        /// <summary>Index of the first tick at which the payload is at the stop position.</summary>
        public long TickCount { get; }

        public double DurationSeconds => (double)TickCount / TickHz;

        /// <summary>Exact distance travelled at a logical tick, clamped to [0, stop].</summary>
        public Rational DistanceAtTick(long tick)
        {
            if (tick <= 0)
                return Rational.Zero;
            var d = new Rational(checked(tick * _route.SpeedMmPerS), TickHz);
            return d > StopDistanceMm ? StopDistanceMm : d;
        }

        /// <summary>Exact payload centre (model mm) at a distance along the route.</summary>
        public (Rational xMm, Rational zMm) PositionAt(Rational distanceMm)
        {
            if (distanceMm < Rational.Zero)
                distanceMm = Rational.Zero;
            for (var s = 0; s + 1 < _route.Waypoints.Count; s++)
            {
                var end = Rational.FromInteger(_cumulative[s + 1]);
                var last = s + 2 == _route.Waypoints.Count;
                if (distanceMm <= end || last)
                {
                    var a = _route.Waypoints[s];
                    var b = _route.Waypoints[s + 1];
                    var length = SegmentLength(s);
                    checked
                    {
                        // offset into this segment = distance - cumulative, clamped to the segment length
                        var offsetNum = distanceMm.Numerator - _cumulative[s] * distanceMm.Denominator;
                        var maxNum = length * distanceMm.Denominator;
                        if (offsetNum > maxNum)
                            offsetNum = maxNum;
                        var den = distanceMm.Denominator;
                        long sx = Math.Sign(b.XMm - a.XMm), sz = Math.Sign(b.ZMm - a.ZMm);
                        return (new Rational(a.XMm * den + sx * offsetNum, den), new Rational(a.ZMm * den + sz * offsetNum, den));
                    }
                }
            }
            var only = _route.Waypoints[0];
            return (Rational.FromInteger(only.XMm), Rational.FromInteger(only.ZMm));
        }

        public (Rational xMm, Rational zMm) PositionAtTick(long tick) => PositionAt(DistanceAtTick(tick));

        /// <summary>
        /// Presentation position for an elapsed logical time: exact positions at the surrounding ticks, linearly
        /// interpolated for display. Floats are used only after the exact positions are fixed.
        /// </summary>
        public (double xMm, double zMm) DisplayPositionAt(double seconds)
        {
            if (double.IsNaN(seconds) || seconds <= 0)
                seconds = 0;
            var exactTick = seconds * TickHz;
            if (exactTick >= TickCount)
            {
                var (sx, sz) = PositionAtTick(TickCount);
                return (sx.ToDouble(), sz.ToDouble());
            }
            var tick = (long)Math.Floor(exactTick);
            var alpha = exactTick - tick;
            var (ax, az) = PositionAtTick(tick);
            var (bx, bz) = PositionAtTick(tick + 1);
            return (ax.ToDouble() + (bx.ToDouble() - ax.ToDouble()) * alpha, az.ToDouble() + (bz.ToDouble() - az.ToDouble()) * alpha);
        }

        long SegmentLength(int s)
        {
            var a = _route.Waypoints[s];
            var b = _route.Waypoints[s + 1];
            return Math.Abs((long)b.XMm - a.XMm) + Math.Abs((long)b.ZMm - a.ZMm);
        }
    }
}
