using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Model;

namespace Tryscape.Application.Rehearsal
{
    public enum AttemptSlot
    {
        A,
        B,
    }

    /// <summary>
    /// A retained attempt (D03.6): an immutable <see cref="RehearsalRun"/> with the scene snapshot it evaluated. Later
    /// edits to the live scene cannot change it, because <see cref="SceneSpec"/> and the evidence are immutable values.
    /// </summary>
    public sealed class SavedAttempt
    {
        public SavedAttempt(AttemptSlot slot, RehearsalRun run)
        {
            Slot = slot;
            Run = run ?? throw new ArgumentNullException(nameof(run));
        }

        public AttemptSlot Slot { get; }
        public RehearsalRun Run { get; }
        public string Label => Slot == AttemptSlot.A ? "Attempt A" : "Attempt B";
    }

    /// <summary>The two retained attempts of the current project.</summary>
    public sealed class AttemptBook
    {
        public SavedAttempt A { get; private set; }
        public SavedAttempt B { get; private set; }

        public event Action Changed;

        public SavedAttempt Get(AttemptSlot slot) => slot == AttemptSlot.A ? A : B;

        public SavedAttempt Keep(AttemptSlot slot, RehearsalRun run)
        {
            var attempt = new SavedAttempt(slot, run);
            if (slot == AttemptSlot.A)
                A = attempt;
            else
                B = attempt;
            Changed?.Invoke();
            return attempt;
        }

        /// <summary>Replaces both slots (project reopen). Either may be null.</summary>
        public void Restore(RehearsalRun a, RehearsalRun b)
        {
            A = a == null ? null : new SavedAttempt(AttemptSlot.A, a);
            B = b == null ? null : new SavedAttempt(AttemptSlot.B, b);
            Changed?.Invoke();
        }

        public void Clear() => Restore(null, null);

        public bool BothRetained => A != null && B != null;
    }

    public sealed class AttemptSummary
    {
        public AttemptSummary(SavedAttempt attempt)
        {
            var e = attempt.Run.Evidence;
            Label = attempt.Label;
            Revision = e.SceneRevision;
            RouteLabel = attempt.Run.Route?.Label ?? e.RouteId;
            Outcome = e.Outcome;
            OutcomeText = AttemptComparison.OutcomeWords(e.Outcome);
            RouteLengthMm = e.RouteLengthMm;
            SceneHashPrefix = e.SceneHash == null ? "n/a" : e.SceneHash.Substring(0, 8);
            EvaluatorVersion = e.EvaluatorVersion;
            CatalogVersion = e.CatalogVersion;
            ObstructionLabel = AttemptComparison.BlockerLabel(attempt.Run);
        }

        public string Label { get; }
        public long Revision { get; }
        public string RouteLabel { get; }
        public EvaluationOutcome Outcome { get; }
        public string OutcomeText { get; }
        public long? RouteLengthMm { get; }
        public string SceneHashPrefix { get; }
        public string EvaluatorVersion { get; }
        public string CatalogVersion { get; }

        /// <summary>Named obstruction from the evidence, or null.</summary>
        public string ObstructionLabel { get; }
    }

    /// <summary>
    /// Result of comparing two retained attempts (S09). It is derived only from the two immutable snapshots and their
    /// evidence. It states model outcomes, computed route lengths and scene differences, and never probabilities,
    /// savings or physical guarantees. Incompatible pairs are reported as such, not compared.
    /// </summary>
    public sealed class ComparisonReport
    {
        internal ComparisonReport(AttemptSummary a, AttemptSummary b, string incompatibleCode, string incompatibleText,
            IReadOnlyList<string> changes, long? lengthDifferenceMm, string explanation, string preparation, IReadOnlyList<string> notes)
        {
            A = a;
            B = b;
            IncompatibleCode = incompatibleCode;
            IncompatibleText = incompatibleText;
            Changes = changes;
            LengthDifferenceMm = lengthDifferenceMm;
            Explanation = explanation;
            Preparation = preparation;
            Notes = notes;
        }

        public AttemptSummary A { get; }
        public AttemptSummary B { get; }
        public bool Comparable => IncompatibleCode == null;
        public string IncompatibleCode { get; }
        public string IncompatibleText { get; }

        /// <summary>Scene differences from A to B: moved/rotated entities, route choice, room size.</summary>
        public IReadOnlyList<string> Changes { get; }

        /// <summary>B length minus A length when both have a computed length (model calculation only).</summary>
        public long? LengthDifferenceMm { get; }

        public string Explanation { get; }

        /// <summary>Evidence-based preparation note, or null when the evidence does not support one.</summary>
        public string Preparation { get; }

        public IReadOnlyList<string> Notes { get; }
    }

    public static class AttemptComparison
    {
        public static ComparisonReport Compare(SavedAttempt a, SavedAttempt b)
        {
            if (a == null)
                throw new ArgumentNullException(nameof(a));
            if (b == null)
                throw new ArgumentNullException(nameof(b));
            var sa = new AttemptSummary(a);
            var sb = new AttemptSummary(b);
            var ea = a.Run.Evidence;
            var eb = b.Run.Evidence;
            var notes = new List<string>();
            if (ea.EvaluatorVersion != RouteEvaluator.Version || eb.EvaluatorVersion != RouteEvaluator.Version)
                notes.Add($"Saved with evaluator {ea.EvaluatorVersion}/{eb.EvaluatorVersion}; this app runs {RouteEvaluator.Version}. Results are shown as saved and not re-evaluated.");

            string code = null, text = null;
            if (ea.EvaluatorVersion != eb.EvaluatorVersion)
            {
                code = "evaluator_mismatch";
                text = $"These attempts were evaluated by different evaluator versions ({ea.EvaluatorVersion} and {eb.EvaluatorVersion}), so their results are not compared.";
            }
            else if (ea.CatalogVersion != eb.CatalogVersion)
            {
                code = "catalog_mismatch";
                text = $"These attempts use different catalog versions ({ea.CatalogVersion} and {eb.CatalogVersion}), so their results are not compared.";
            }
            else if (a.Run.Scene.SceneId != b.Run.Scene.SceneId)
            {
                code = "different_scene";
                text = "These attempts belong to different rooms, so they are not compared.";
            }
            else if (!Viable(ea.Outcome) || !Viable(eb.Outcome))
            {
                code = "not_viable";
                text = "At least one attempt could not be evaluated (invalid input or unsupported), so it is not compared as an alternative.";
            }

            var changes = Changes(a.Run, b.Run);
            if (code != null)
                return new ComparisonReport(sa, sb, code, text, changes, null, null, null, notes);

            long? difference = ea.RouteLengthMm.HasValue && eb.RouteLengthMm.HasValue ? eb.RouteLengthMm - ea.RouteLengthMm : null;
            var (explanation, preparation) = Explain(a, b, sa, sb, changes);
            return new ComparisonReport(sa, sb, null, null, changes, difference, explanation, preparation, notes);
        }

        public static string OutcomeWords(EvaluationOutcome outcome)
        {
            switch (outcome)
            {
                case EvaluationOutcome.ClearInModel: return "Clear in this model";
                case EvaluationOutcome.BlockedInModel: return "Blocked in this model";
                case EvaluationOutcome.InvalidInput: return "Invalid input";
                default: return "Unsupported";
            }
        }

        internal static string BlockerLabel(RehearsalRun run)
        {
            var v = run.Evidence.FirstViolation;
            if (v == null || v.EntityIds.Count == 0)
                return v != null && v.RuleCode == RuleCodes.RoomBoundary ? "the room boundary" : null;
            var entity = run.Scene.FindEntity(v.EntityIds[0]);
            return entity == null ? "an obstacle" : "the " + entity.Label.ToLowerInvariant();
        }

        static bool Viable(EvaluationOutcome o) => o == EvaluationOutcome.ClearInModel || o == EvaluationOutcome.BlockedInModel;

        static string M(int mm) => (mm / 1000.0).ToString("0.00", CultureInfo.InvariantCulture);

        static List<string> Changes(RehearsalRun a, RehearsalRun b)
        {
            var changes = new List<string>();
            var sa = a.Scene;
            var sb = b.Scene;
            if (sa.Room.WidthMm != sb.Room.WidthMm || sa.Room.DepthMm != sb.Room.DepthMm)
                changes.Add($"Room size changed from {M(sa.Room.WidthMm)} x {M(sa.Room.DepthMm)} m to {M(sb.Room.WidthMm)} x {M(sb.Room.DepthMm)} m.");
            foreach (var before in sa.Entities.OrderBy(e => e.Id.ToString("D"), StringComparer.Ordinal))
            {
                var after = sb.FindEntity(before.Id);
                if (after == null)
                {
                    changes.Add($"{before.Label} is not in Attempt B.");
                    continue;
                }
                if (before.Pose.XMm != after.Pose.XMm || before.Pose.ZMm != after.Pose.ZMm)
                    changes.Add($"{before.Label} moved from ({M(before.Pose.XMm)}, {M(before.Pose.ZMm)}) m to ({M(after.Pose.XMm)}, {M(after.Pose.ZMm)}) m.");
                if (before.Pose.YawDeg != after.Pose.YawDeg)
                    changes.Add($"{before.Label} turned from {before.Pose.YawDeg} to {after.Pose.YawDeg} degrees.");
            }
            foreach (var added in sb.Entities.Where(e => sa.FindEntity(e.Id) == null))
                changes.Add($"{added.Label} appears only in Attempt B.");
            var ra = a.Route?.Label ?? a.Evidence.RouteId;
            var rb = b.Route?.Label ?? b.Evidence.RouteId;
            if (a.Evidence.RouteId != b.Evidence.RouteId)
                changes.Add($"Route changed from {ra} to {rb}.");
            return changes;
        }

        static (string explanation, string preparation) Explain(SavedAttempt a, SavedAttempt b, AttemptSummary sa, AttemptSummary sb,
            List<string> changes)
        {
            var blockedA = sa.Outcome == EvaluationOutcome.BlockedInModel;
            var blockedB = sb.Outcome == EvaluationOutcome.BlockedInModel;
            var routeChanged = a.Run.Evidence.RouteId != b.Run.Evidence.RouteId;
            var blockerA = a.Run.Evidence.FirstViolation?.EntityIds.FirstOrDefault() ?? Guid.Empty;
            var blockerMoved = blockerA != Guid.Empty && Moved(a.Run.Scene, b.Run.Scene, blockerA);
            if (changes.Count == 0)
                return ($"No scene or route change between the attempts; both are {sa.OutcomeText.ToLowerInvariant()}.", null);

            if (blockedA && !blockedB)
            {
                var why = blockerMoved && !routeChanged
                    ? $"after {sa.ObstructionLabel} was moved"
                    : routeChanged && !blockerMoved
                        ? $"using the {sb.RouteLabel} instead"
                        : "after the changes listed";
                var prep = blockerMoved && !routeChanged
                    ? $"Before delivery, move {sa.ObstructionLabel} out of the {sa.RouteLabel.ToLowerInvariant()}, as in Attempt B."
                    : routeChanged && !blockerMoved
                        ? $"Plan the delivery along the {sb.RouteLabel.ToLowerInvariant()}, as in Attempt B."
                        : null;
                return ($"Attempt A was blocked in this model by {sa.ObstructionLabel}. Attempt B is clear in this model {why}.", prep);
            }
            if (!blockedA && blockedB)
                return ($"Attempt A was clear in this model. Attempt B is blocked in this model by {sb.ObstructionLabel} after the changes listed.",
                    "Keep the arrangement from Attempt A.");
            if (blockedA)
                return sa.ObstructionLabel == sb.ObstructionLabel
                    ? ($"Both attempts are blocked in this model by {sa.ObstructionLabel}.", null)
                    : ($"Both attempts are blocked in this model: A by {sa.ObstructionLabel}, B by {sb.ObstructionLabel}.", null);
            return ("Both attempts are clear in this model.", null);
        }

        static bool Moved(SceneSpec a, SceneSpec b, Guid id)
        {
            var before = a.FindEntity(id);
            var after = b.FindEntity(id);
            return before != null && after != null && !before.Pose.Equals(after.Pose);
        }
    }
}
