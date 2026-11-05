using System;
using System.Collections.Generic;
using System.Linq;
using Tryscape.Application.Creation;
using Tryscape.Core.Canonical;
using Tryscape.Core.Model;
using Tryscape.Core.Validation;

namespace Tryscape.Content.MovingDay
{
    /// <summary>
    /// Client-side acceptance and review mapping for Moving Day proposals (S05.2 step 2, S10.3).
    /// <para>
    /// A ready proposal is accepted only if its scene passes Core semantic validation against this pack's catalog, is
    /// illustrative with no measurement sources, and is byte-for-byte the scene <see cref="MovingDayTemplate"/>
    /// assembles from the proposal's own intent, using the proposal's IDs and assumptions. A service therefore cannot
    /// add entities, move fixed coordinates, change labels or claim measured provenance: anything other than the pack's
    /// constrained assembly is rejected.
    /// </para>
    /// </summary>
    public sealed class MovingDayProposals : IProposalCheck
    {
        public const string HandCorrectedCode = "room_set_with_hand_controls";

        readonly Catalog _catalog;

        public MovingDayProposals(Catalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public string Problem(SceneProposal proposal)
        {
            if (proposal == null)
                return "missing_proposal";
            switch (proposal.Status)
            {
                case ProposalStatus.NeedsClarification:
                    if (proposal.Scene != null || proposal.Questions.Count < 1 || proposal.Questions.Count > 3)
                        return "clarification_shape";
                    return proposal.Questions.Any(q => q.Choices.Count < 2 || q.Choices.Count > 4) ? "clarification_choices" : null;
                case ProposalStatus.Unsupported:
                    return proposal.Scene != null ? "unsupported_with_scene" : null;
            }

            var intent = proposal.Intent;
            var scene = proposal.Scene;
            if (intent == null || scene == null || proposal.Questions.Count != 0)
                return "ready_shape";
            if (intent.ScenarioPackId != MovingDayPack.PackId || scene.ScenarioPackId != MovingDayPack.PackId ||
                scene.ScenarioPackVersion != MovingDayPack.PackVersion || scene.CatalogVersion != _catalog.CatalogVersion)
                return "pack_mismatch";
            if (!TryParameters(intent, out var parameters))
                return "intent_unsupported";
            if (scene.Provenance != Provenance.Illustrative || scene.MeasurementSources.Count != 0)
                return "provenance_not_illustrative";
            if (!SceneValidator.Validate(scene, _catalog).IsValid)
                return "scene_invalid";

            var payload = scene.Entities.Where(e => e.Role == EntityRole.Payload).ToList();
            var blockers = scene.Entities.Where(e => e.Role == EntityRole.Blocker).ToList();
            if (payload.Count != 1 || blockers.Count != 1 || scene.Entities.Count != 2)
                return "entities_not_pack_layout";
            var expected = MovingDayTemplate.Assemble(parameters,
                new FixedIds(scene.SceneId, scene.ProjectId, payload[0].Id, blockers[0].Id), scene.Assumptions);
            return SceneCanonicalizer.CanonicalBytes(expected).SequenceEqual(SceneCanonicalizer.CanonicalBytes(scene))
                ? null
                : "scene_not_pack_assembly";
        }

        /// <summary>Review draft for a ready proposal's intent; false if the intent is outside this pack.</summary>
        public static bool TryParameters(ProposalIntent intent, out MovingDayParameters parameters)
        {
            parameters = null;
            if (intent == null || intent.PayloadCatalogId != MovingDayPack.SofaCatalogId)
                return false;
            ObstructionState obstruction;
            if (intent.ObstructionState == "center")
                obstruction = ObstructionState.Center;
            else if (intent.ObstructionState == "parked_south")
                obstruction = ObstructionState.ParkedSouth;
            else
                return false;
            var candidate = new MovingDayParameters(intent.RoomWidthMm, intent.RoomDepthMm, obstruction, intent.PreferredRouteId);
            if (candidate.Problem() != null)
                return false;
            parameters = candidate;
            return true;
        }

        /// <summary>
        /// The scene to construct after review. If the user did not change anything, this is the proposal's own scene. If
        /// they corrected values with hands, the pack template rebuilds the scene from the corrected values, the same path
        /// as hand creation. Assumptions about corrected fields are replaced, and provenance stays illustrative.
        /// </summary>
        public static SceneSpec SceneFor(SceneProposal proposal, MovingDayParameters reviewed, IIdGenerator ids)
        {
            if (proposal?.Scene == null || !TryParameters(proposal.Intent, out var proposed))
                throw new ArgumentException("Only an accepted ready proposal can be confirmed", nameof(proposal));
            if (reviewed == null)
                throw new ArgumentNullException(nameof(reviewed));
            if (Same(proposed, reviewed))
                return proposal.Scene;

            var roomChanged = proposed.RoomWidthMm != reviewed.RoomWidthMm || proposed.RoomDepthMm != reviewed.RoomDepthMm;
            var obstructionChanged = proposed.Obstruction != reviewed.Obstruction;
            var routeChanged = proposed.RouteId != reviewed.RouteId;
            var assumptions = new List<Assumption>();
            foreach (var a in proposal.Scene.Assumptions)
            {
                if (roomChanged && (a.Code == "room_dimensions_from_request" || a.Code == "unit_interpreted" ||
                                    a.Code.StartsWith("example_room", StringComparison.Ordinal)))
                    continue;
                if (obstructionChanged && a.Code == "obstruction_default")
                    continue;
                if (routeChanged && a.Code == "route_default")
                    continue;
                assumptions.Add(a);
            }
            if (roomChanged)
                assumptions.Insert(0, new Assumption(HandCorrectedCode,
                    $"Room size {reviewed.RoomWidthMm / 1000.0:0.0##} m by {reviewed.RoomDepthMm / 1000.0:0.0##} m set with hand controls; not a confirmed measurement.",
                    AssumptionSource.UserRequest, false));
            if (assumptions.Count == 0)
                assumptions.Add(MovingDayTemplate.UprightAssumption);
            return MovingDayTemplate.Assemble(reviewed, ids, assumptions);
        }

        public static bool Same(MovingDayParameters a, MovingDayParameters b) =>
            a.RoomWidthMm == b.RoomWidthMm && a.RoomDepthMm == b.RoomDepthMm && a.Obstruction == b.Obstruction &&
            string.Equals(a.RouteId, b.RouteId, StringComparison.Ordinal);

        sealed class FixedIds : IIdGenerator
        {
            readonly Queue<Guid> _ids;

            public FixedIds(params Guid[] ids) => _ids = new Queue<Guid>(ids);

            public Guid NewId() => _ids.Count > 0 ? _ids.Dequeue() : throw new InvalidOperationException("Pack layout needs exactly four IDs");
        }
    }
}
