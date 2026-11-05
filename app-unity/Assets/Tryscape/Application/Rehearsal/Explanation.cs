using System;
using System.Linq;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Model;

namespace Tryscape.Application.Rehearsal
{
    /// <summary>
    /// Three-layer explanation (S09): observed event, modelled reason, supported action, plus the scope disclosure.
    /// Built only from a run's evidence and its own scene snapshot via authored templates keyed by
    /// <see cref="Violation.MessageKey"/>; no generated prose, no claims beyond the footprint model.
    /// </summary>
    public sealed class Explanation
    {
        public const string Scope = "Floor footprint only. Height, turning and lifting were not assessed.";

        Explanation(string headline, string observed, string reason, string action)
        {
            Headline = headline;
            Observed = observed;
            Reason = reason;
            Action = action;
        }

        public string Headline { get; }
        public string Observed { get; }
        public string Reason { get; }
        public string Action { get; }

        public static Explanation For(RehearsalRun run)
        {
            if (run == null)
                throw new ArgumentNullException(nameof(run));
            var evidence = run.Evidence;
            var scene = run.Scene;
            var routeLabel = run.Route?.Label ?? evidence.RouteId;
            var payload = run.Route == null ? null : scene.FindEntity(run.Route.PayloadEntityId);
            var payloadName = Name(payload, "the payload");
            var length = evidence.RouteLengthMm.HasValue ? $"{evidence.RouteLengthMm.Value / 1000.0:0.0##} m" : "unknown length";

            switch (evidence.Outcome)
            {
                case EvaluationOutcome.ClearInModel:
                    return new Explanation(
                        "Clear in this model",
                        $"{Capitalise(payloadName)} reaches the destination along the {routeLabel} ({length}).",
                        $"No modelled floor obstruction on this route, keeping at least the {ModelMargin(run)} margin.",
                        "Compare another approach, or move objects and rehearse again.");

                case EvaluationOutcome.BlockedInModel:
                {
                    var v = evidence.FirstViolation;
                    var blocker = v.EntityIds.Count > 0 ? scene.FindEntity(v.EntityIds[0]) : null;
                    var blockerName = Name(blocker, "an obstacle");
                    var alternative = scene.Routes.FirstOrDefault(r => r.Id != evidence.RouteId);
                    var tryOther = alternative == null ? "" : $" or try the {alternative.Label}";
                    switch (v.MessageKey)
                    {
                        case MessageKeys.BlockerObstructsRoute:
                            return new Explanation(
                                "Blocked in this model",
                                $"{Capitalise(payloadName)} stops on the {routeLabel}, segment {v.SegmentIndex + 1}.",
                                $"{Capitalise(blockerName)} obstructs {Possessive(payloadName)} modelled route: the footprints come within the {v.ModelMarginMm} mm model margin.",
                                $"Move {blockerName} aside{tryOther}.");
                        case MessageKeys.RouteLeavesRoom:
                            return new Explanation(
                                "Blocked in this model",
                                $"{Capitalise(payloadName)} stops on the {routeLabel}, segment {v.SegmentIndex + 1}.",
                                $"The route would bring {payloadName} closer than the {v.ModelMarginMm} mm model margin to a wall.",
                                alternative == null ? "This route cannot be fixed by moving objects." : $"Try the {alternative.Label}.");
                        case MessageKeys.StartObstructed:
                            return new Explanation(
                                "Cannot start in this model",
                                $"{Capitalise(payloadName)} cannot leave its starting position.",
                                blocker == null
                                    ? $"The start is within the {v.ModelMarginMm} mm model margin of a wall."
                                    : $"{Capitalise(blockerName)} is within the {v.ModelMarginMm} mm model margin of {Possessive(payloadName)} start.",
                                blocker == null ? "Choose another route." : $"Move {blockerName} away from {Possessive(payloadName)} starting position.");
                        default:
                            throw new ArgumentException($"No template for message key '{v.MessageKey}'", nameof(run));
                    }
                }

                case EvaluationOutcome.InvalidInput:
                    return new Explanation(
                        "Not evaluated",
                        "This scene could not be checked.",
                        $"The scene breaks a model rule ({string.Join(", ", evidence.ValidationErrors.Select(e => e.Code).Distinct())}).",
                        "Undo or reset the last change, then rehearse again.");

                default:
                    return new Explanation(
                        "Not assessed",
                        "This evaluator version cannot assess the scene.",
                        "The scene uses something this model does not represent.",
                        "Reset to the example and rehearse again.");
            }
        }

        static string ModelMargin(RehearsalRun run) => $"{Tryscape.Core.Validation.ModelParameters.PayloadMarginMm} mm";

        /// <summary>"Console table" -> "the console table"; labels starting with an acronym keep their case.</summary>
        static string Name(EntitySpec entity, string fallback)
        {
            if (entity == null || string.IsNullOrEmpty(entity.Label))
                return fallback;
            var label = entity.Label;
            var lower = label.Length > 1 && char.IsUpper(label[0]) && !char.IsUpper(label[1])
                ? char.ToLowerInvariant(label[0]) + label.Substring(1)
                : label;
            return "the " + lower;
        }

        static string Possessive(string name) => name.EndsWith("s", StringComparison.Ordinal) ? name + "'" : name + "'s";

        static string Capitalise(string text) =>
            string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
