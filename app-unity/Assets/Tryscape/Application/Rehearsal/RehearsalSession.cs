using System;
using System.Collections.Generic;
using System.Globalization;
using Tryscape.Application.Editing;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Model;

namespace Tryscape.Application.Rehearsal
{
    /// <summary>UTC wall clock for run-envelope audit timestamps only; never used for ordering or results.</summary>
    public interface IUtcClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemUtcClock : IUtcClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    /// <summary>
    /// One immutable evaluation run: the result envelope plus the exact scene it evaluated, so explanations,
    /// replay and later comparison never read the (possibly edited) live scene.
    /// </summary>
    public sealed class RehearsalRun
    {
        public RehearsalRun(EvaluationResult result, SceneSpec scene)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
            Scene = scene ?? throw new ArgumentNullException(nameof(scene));
            if (scene.SceneId != result.Evidence.SceneId || scene.Revision != result.Evidence.SceneRevision)
                throw new ArgumentException("Run scene does not match its evidence", nameof(scene));
            Route = scene.FindRoute(result.Evidence.RouteId);
        }

        public EvaluationResult Result { get; }
        public EvaluationEvidence Evidence => Result.Evidence;
        public SceneSpec Scene { get; }

        /// <summary>Null only for invalid input naming an unknown route.</summary>
        public RouteSpec Route { get; }

        public bool CanReplay =>
            Route != null && (Evidence.Outcome == EvaluationOutcome.ClearInModel || Evidence.Outcome == EvaluationOutcome.BlockedInModel);

        public PlaybackTimeline CreateTimeline() =>
            CanReplay ? new PlaybackTimeline(Route, Evidence) : throw new InvalidOperationException("Run cannot be replayed");
    }

    /// <summary>
    /// Local rehearsal use case (S08/S09): evaluates the live scene's active route with the pure-core evaluator,
    /// keeps a bounded history of immutable runs and reports when the latest run is stale for the live scene.
    /// No network, model provider or per-frame work: evaluation happens once per user request.
    /// </summary>
    public sealed class RehearsalSession
    {
        public const int HistoryLimit = 20;

        readonly EditingSession _editing;
        readonly IIdGenerator _ids;
        readonly IUtcClock _clock;
        readonly List<RehearsalRun> _history = new List<RehearsalRun>();

        public RehearsalSession(EditingSession editing, IIdGenerator ids, IUtcClock clock)
        {
            _editing = editing ?? throw new ArgumentNullException(nameof(editing));
            _ids = ids ?? throw new ArgumentNullException(nameof(ids));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public RehearsalRun Latest { get; private set; }
        public IReadOnlyList<RehearsalRun> History => _history;

        /// <summary>True when the live scene has changed (any new revision) since the latest run.</summary>
        public bool IsLatestStale => Latest != null && IsStale(Latest);

        public bool IsStale(RehearsalRun run) =>
            run.Scene.SceneId != _editing.Current.SceneId || run.Scene.Revision != _editing.Current.Revision;

        public event Action<RehearsalRun> Evaluated;

        /// <summary>Evaluates the live scene's active route.</summary>
        public RehearsalRun Rehearse() => Rehearse(_editing.Current.ActiveRouteId);

        public RehearsalRun Rehearse(string routeId)
        {
            var scene = _editing.Current;
            var evidence = RouteEvaluator.Evaluate(scene, _editing.Catalog, routeId);
            var createdAt = _clock.UtcNow.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            var run = new RehearsalRun(new EvaluationResult(_ids.NewId(), createdAt, evidence), scene);
            _history.Add(run);
            while (_history.Count > HistoryLimit)
                _history.RemoveAt(0);
            Latest = run;
            Evaluated?.Invoke(run);
            return run;
        }
    }
}
