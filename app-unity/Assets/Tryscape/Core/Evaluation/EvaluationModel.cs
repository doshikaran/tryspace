using System;
using System.Collections.Generic;
using System.Linq;
using Tryscape.Core.Canonical;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;
using Tryscape.Core.Validation;

namespace Tryscape.Core.Evaluation
{
    /// <summary>Wire values from contracts/v1 evaluation_result.schema.json. Never a blanket "safe".</summary>
    public enum EvaluationOutcome
    {
        ClearInModel,
        BlockedInModel,
        InvalidInput,

        /// <summary>
        /// Reserved for inputs this evaluator version cannot assess. Under evaluator 1.0.0 every R0 limitation
        /// (arbitrary yaw, diagonal segments) is already rejected by semantic validation, so this is not produced.
        /// </summary>
        Unsupported,
    }

    public static class RuleCodes
    {
        public const string BlockerIntersection = "blocker_intersection";
        public const string RoomBoundary = "room_boundary";
        public const string InvalidStart = "invalid_start";
        public const string UnsupportedOrientation = "unsupported_orientation";
        public const string InvalidRoute = "invalid_route";
    }

    /// <summary>Explanation template keys; evidence, not prose, selects them (S09).</summary>
    public static class MessageKeys
    {
        public const string BlockerObstructsRoute = "blocker_obstructs_route";
        public const string RouteLeavesRoom = "route_leaves_room";
        public const string StartObstructed = "start_obstructed";
    }

    public sealed class Violation : IEquatable<Violation>
    {
        public Violation(string ruleCode, IEnumerable<Guid> entityIds, int segmentIndex, Rational entryFraction,
            Rational contactXMm, Rational contactZMm, int modelMarginMm, string messageKey)
        {
            RuleCode = ruleCode ?? throw new ArgumentNullException(nameof(ruleCode));
            EntityIds = (entityIds ?? throw new ArgumentNullException(nameof(entityIds))).ToArray();
            SegmentIndex = segmentIndex;
            EntryFraction = entryFraction;
            ContactXMm = contactXMm;
            ContactZMm = contactZMm;
            ModelMarginMm = modelMarginMm;
            MessageKey = messageKey ?? throw new ArgumentNullException(nameof(messageKey));
        }

        public string RuleCode { get; }
        public IReadOnlyList<Guid> EntityIds { get; }
        public int SegmentIndex { get; }

        /// <summary>
        /// Blockers: first parameter where the payload centre touches the closed expanded rectangle.
        /// Room: last parameter still inside the permitted-centre rectangle (contracts/v1 README).
        /// </summary>
        public Rational EntryFraction { get; }

        /// <summary>Payload centre at the violation, model-space millimetres.</summary>
        public Rational ContactXMm { get; }

        public Rational ContactZMm { get; }
        public int ModelMarginMm { get; }
        public string MessageKey { get; }

        public bool Equals(Violation other) =>
            other != null && RuleCode == other.RuleCode && EntityIds.SequenceEqual(other.EntityIds) &&
            SegmentIndex == other.SegmentIndex && EntryFraction == other.EntryFraction &&
            ContactXMm == other.ContactXMm && ContactZMm == other.ContactZMm &&
            ModelMarginMm == other.ModelMarginMm && MessageKey == other.MessageKey;

        public override bool Equals(object obj) => Equals(obj as Violation);
        public override int GetHashCode() => SegmentIndex * 397 ^ EntryFraction.GetHashCode();

        public override string ToString() =>
            $"{RuleCode} seg {SegmentIndex} t={EntryFraction} at ({ContactXMm},{ContactZMm}) [{string.Join(",", EntityIds)}]";
    }

    /// <summary>
    /// Deterministic projection of an evaluation (S09): everything except the run envelope (run ID, timestamps,
    /// presentation progress). Identical inputs give byte-identical <see cref="CanonicalBytes"/>.
    /// </summary>
    public sealed class EvaluationEvidence
    {
        public const string SchemaVersion = "1.0.0";

        public EvaluationEvidence(Guid sceneId, long sceneRevision, string sceneHash, string routeId,
            string catalogVersion, string evaluatorVersion, EvaluationOutcome outcome, long? routeLengthMm,
            Violation firstViolation, IEnumerable<Assumption> assumptions, IEnumerable<ValidationError> validationErrors)
        {
            SceneId = sceneId;
            SceneRevision = sceneRevision;
            SceneHash = sceneHash;
            RouteId = routeId ?? throw new ArgumentNullException(nameof(routeId));
            CatalogVersion = catalogVersion ?? throw new ArgumentNullException(nameof(catalogVersion));
            EvaluatorVersion = evaluatorVersion ?? throw new ArgumentNullException(nameof(evaluatorVersion));
            Outcome = outcome;
            RouteLengthMm = routeLengthMm;
            FirstViolation = firstViolation;
            Assumptions = (assumptions ?? throw new ArgumentNullException(nameof(assumptions))).ToArray();
            ValidationErrors = (validationErrors ?? throw new ArgumentNullException(nameof(validationErrors))).ToArray();

            if (outcome == EvaluationOutcome.BlockedInModel && (firstViolation == null || sceneHash == null))
                throw new ArgumentException("Blocked evidence needs a violation and scene hash");
            if (outcome == EvaluationOutcome.ClearInModel && (firstViolation != null || sceneHash == null || routeLengthMm == null))
                throw new ArgumentException("Clear evidence needs a scene hash, a length and no violation");
            if (outcome == EvaluationOutcome.InvalidInput && (sceneHash != null || firstViolation != null || ValidationErrors.Count == 0))
                throw new ArgumentException("Invalid-input evidence references validation errors, never a scene hash");
        }

        public Guid SceneId { get; }
        public long SceneRevision { get; }

        /// <summary>Null exactly when the input was invalid.</summary>
        public string SceneHash { get; }

        public string RouteId { get; }
        public string CatalogVersion { get; }
        public string EvaluatorVersion { get; }
        public EvaluationOutcome Outcome { get; }
        public long? RouteLengthMm { get; }
        public Violation FirstViolation { get; }
        public IReadOnlyList<Assumption> Assumptions { get; }
        public IReadOnlyList<ValidationError> ValidationErrors { get; }

        public byte[] CanonicalBytes() => CanonicalJson.ToBytes(ToCanonical());
        public string EvidenceHash() => CanonicalJson.Sha256Hex(CanonicalBytes());

        public CanonicalObject ToCanonical()
        {
            var assumptions = CanonicalJson.Array();
            foreach (var a in Assumptions)
                assumptions.Add(CanonicalJson.Object()
                    .Set("code", a.Code)
                    .Set("text", a.Text)
                    .Set("source", SceneCanonicalizer.Source(a.Source))
                    .Set("requires_confirmation", a.RequiresConfirmation));

            var errors = CanonicalJson.Array();
            foreach (var e in ValidationErrors)
                errors.Add(CanonicalJson.Object().Set("path", e.Path).Set("code", e.Code));

            return CanonicalJson.Object()
                .Set("schema_version", SchemaVersion)
                .Set("scene_id", SceneCanonicalizer.Uuid(SceneId))
                .Set("scene_revision", SceneRevision)
                .Set("scene_hash", SceneHash)
                .Set("route_id", RouteId)
                .Set("catalog_version", CatalogVersion)
                .Set("evaluator_version", EvaluatorVersion)
                .Set("outcome", OutcomeText(Outcome))
                .Set("route_length_mm", RouteLengthMm.HasValue ? (CanonicalValue)RouteLengthMm.Value : CanonicalNull.Instance)
                .Set("first_violation", FirstViolation == null ? (CanonicalValue)CanonicalNull.Instance : ViolationObject(FirstViolation))
                .Set("assumptions", assumptions)
                .Set("validation_errors", errors);
        }

        public static string OutcomeText(EvaluationOutcome outcome)
        {
            switch (outcome)
            {
                case EvaluationOutcome.ClearInModel: return "clear_in_model";
                case EvaluationOutcome.BlockedInModel: return "blocked_in_model";
                case EvaluationOutcome.InvalidInput: return "invalid_input";
                case EvaluationOutcome.Unsupported: return "unsupported";
                default: throw new ArgumentOutOfRangeException(nameof(outcome));
            }
        }

        static CanonicalObject ViolationObject(Violation v)
        {
            var ids = CanonicalJson.Array();
            foreach (var id in v.EntityIds)
                ids.Add(SceneCanonicalizer.Uuid(id));
            return CanonicalJson.Object()
                .Set("rule_code", v.RuleCode)
                .Set("entity_ids", ids)
                .Set("segment_index", v.SegmentIndex)
                .Set("entry_fraction", RationalObject(v.EntryFraction))
                .Set("contact_center", CanonicalJson.Object()
                    .Set("x_mm", RationalObject(v.ContactXMm))
                    .Set("z_mm", RationalObject(v.ContactZMm)))
                .Set("model_margin_mm", v.ModelMarginMm)
                .Set("message_key", v.MessageKey);
        }

        static CanonicalObject RationalObject(Rational r) =>
            CanonicalJson.Object().Set("numerator", r.Numerator).Set("denominator", r.Denominator);
    }

    /// <summary>
    /// Run envelope around deterministic evidence. <see cref="RunId"/> and <see cref="CreatedAtUtc"/> are supplied
    /// by the application layer (Core has no clock) and never enter the evidence hash.
    /// </summary>
    public sealed class EvaluationResult
    {
        public EvaluationResult(Guid runId, string createdAtUtc, EvaluationEvidence evidence)
        {
            RunId = runId;
            CreatedAtUtc = createdAtUtc ?? throw new ArgumentNullException(nameof(createdAtUtc));
            Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        }

        public Guid RunId { get; }
        public string CreatedAtUtc { get; }
        public EvaluationEvidence Evidence { get; }

        /// <summary>Full contract shape (evaluation_result.schema.json): evidence plus envelope fields.</summary>
        public CanonicalObject ToWire() =>
            Evidence.ToCanonical().Set("run_id", SceneCanonicalizer.Uuid(RunId)).Set("created_at", CreatedAtUtc);
    }
}
