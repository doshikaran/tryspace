using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Tryscape.Application.Creation;
using Tryscape.Core.Model;

namespace Tryscape.Content.MovingDay
{
    /// <summary>
    /// On-device scene proposals (decision D12): port of services/api/src/tryscape_api/creation/proposals.py.
    /// Same statuses, question wording, choice labels, assumption codes and texts, and the same ID allocation order
    /// (proposal, scene, project, sofa, console), so a proposal built here equals the service's for the same transcript.
    /// The scene comes from <see cref="MovingDayTemplate"/>, the same constrained assembly the client accepts, so the
    /// regular <see cref="MovingDayProposals"/> check still applies before review.
    /// </summary>
    public sealed class MovingDayLocalProposals : ISceneProposalProvider
    {
        static readonly Dictionary<string, string> UnsupportedText = new Dictionary<string, string>
        {
            ["unsupported_input_kind"] = "Floor plans, scans, photos and files are not supported. Describe a rectangular room " +
                                         "5 to 8 m on each side instead.",
            ["unsupported_prediction"] = "Predicting what people will do is not supported. Participants follow the declared " +
                                         "route only.",
            ["unsupported_threshold"] = "Doors, hallways, stairs, vehicles and lifting are not modelled. Only the floor footprint " +
                                        "inside one room is checked.",
            ["unsupported_object"] = "Only one standard sofa and one console table are available in this example.",
            ["unsupported_scenario"] = "This build supports one situation: preparing a room for a sofa delivery.",
        };

        static readonly Dictionary<MovingDayInterpreter.DimensionIssue, string> DimensionPrompts =
            new Dictionary<MovingDayInterpreter.DimensionIssue, string>
            {
                [MovingDayInterpreter.DimensionIssue.Missing] = "No room size was given. Use the 6 m by 5 m example, or enter dimensions?",
                [MovingDayInterpreter.DimensionIssue.OutOfRange] = "That room size is outside the supported 5 to 8 m range. Use the 6 m by 5 m " +
                                                                   "example, or enter dimensions?",
                [MovingDayInterpreter.DimensionIssue.UnsupportedUnit] = "Only metres and centimetres are supported. Use the 6 m by 5 m example, or " +
                                                                        "enter dimensions?",
                [MovingDayInterpreter.DimensionIssue.UnitMissing] = "The room size needs a unit. Use the 6 m by 5 m example, or enter dimensions?",
                [MovingDayInterpreter.DimensionIssue.NotWholeMm] = "Room sizes must be whole millimetres. Use the 6 m by 5 m example, or enter " +
                                                                   "dimensions?",
                [MovingDayInterpreter.DimensionIssue.Conflicting] = "The room size was unclear (more than one size heard). Use the 6 m by 5 m " +
                                                                    "example, or enter dimensions?",
                [MovingDayInterpreter.DimensionIssue.AreaOnly] = "Width and depth are needed, not an area. Use the 6 m by 5 m example, or enter " +
                                                                 "dimensions?",
            };

        readonly IIdGenerator _ids;

        public MovingDayLocalProposals(IIdGenerator ids) => _ids = ids ?? throw new ArgumentNullException(nameof(ids));

        public Task<ProposalOutcome> ProposeAsync(string transcript, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<ProposalOutcome>(cancellationToken);
            if (string.IsNullOrWhiteSpace(transcript) || transcript.Length > SceneProposal.MaxTranscriptChars)
                return Task.FromResult(ProposalOutcome.Failed(ProposalFailure.Rejected, "transcript_invalid"));
            return Task.FromResult(ProposalOutcome.Of(Propose(transcript)));
        }

        /// <summary>Pure and synchronous: the same transcript and ID sequence always give the same proposal.</summary>
        public SceneProposal Propose(string transcript)
        {
            var e = MovingDayInterpreter.Extract(transcript);
            var proposalId = _ids.NewId();

            var unsupported = new List<string>(e.Unsupported);
            if (unsupported.Count == 0 && !e.ScenarioCue && e.WidthMm == null)
                unsupported.Add("unsupported_scenario");
            if (unsupported.Count > 0)
            {
                var reasons = new List<Assumption>();
                foreach (var code in unsupported)
                    reasons.Add(new Assumption(code, UnsupportedText[code], AssumptionSource.ScenarioModel, false));
                return Make(proposalId, ProposalStatus.Unsupported, transcript, null, null, null, reasons);
            }

            var questions = Questions(e);
            if (questions.Count > 0)
                return Make(proposalId, ProposalStatus.NeedsClarification, transcript, null, null, questions, null);

            var assumptions = Assumptions(e);
            var intent = new ProposalIntent(MovingDayPack.PackId, e.WidthMm.Value, e.DepthMm.Value,
                e.PayloadCatalogId ?? MovingDayPack.SofaCatalogId, e.Obstruction ?? "center", e.Route ?? MovingDayPack.DirectRouteId);
            if (!MovingDayProposals.TryParameters(intent, out var parameters))
                throw new InvalidOperationException("Interpreter produced an intent outside the pack"); // compiler defect, never user error
            var scene = MovingDayTemplate.Assemble(parameters, _ids, assumptions);
            return Make(proposalId, ProposalStatus.Ready, transcript, intent, scene, null, assumptions);
        }

        static SceneProposal Make(Guid id, ProposalStatus status, string transcript, ProposalIntent intent, SceneSpec scene,
            IReadOnlyList<ProposalQuestion> questions, IReadOnlyList<Assumption> assumptions) =>
            new SceneProposal(id, status, transcript, intent, scene, questions, assumptions, MovingDayInterpreter.ProviderId,
                MovingDayInterpreter.Version);

        /// <summary>Python <c>f"{mm / 1000:g} m"</c>.</summary>
        static string Metres(int mm) => (mm / 1000m).ToString("0.######", CultureInfo.InvariantCulture) + " m";

        static List<ProposalQuestion> Questions(MovingDayInterpreter.Extraction e)
        {
            var questions = new List<ProposalQuestion>();
            switch (e.Issue)
            {
                case MovingDayInterpreter.DimensionIssue.WidthMissing:
                    questions.Add(new ProposalQuestion("room_width",
                        e.DepthMm is int depth && depth != 0
                            ? $"Room depth {Metres(depth)} heard, but no width. Use the 6 m example width, or enter dimensions?"
                            : "No room width was given. Use the 6 m example width, or enter dimensions?",
                        new[] { MovingDayInterpreter.AnswerExampleWidth, MovingDayInterpreter.AnswerEnterDimensions }));
                    break;
                case MovingDayInterpreter.DimensionIssue.DepthMissing:
                    questions.Add(new ProposalQuestion("room_depth",
                        $"Room width {Metres(e.WidthMm ?? 0)} heard, but no depth. Use the 5 m example depth, or enter dimensions?",
                        new[] { MovingDayInterpreter.AnswerExampleDepth, MovingDayInterpreter.AnswerEnterDimensions }));
                    break;
                case MovingDayInterpreter.DimensionIssue issue:
                    questions.Add(new ProposalQuestion("room_dimensions", DimensionPrompts[issue],
                        new[] { MovingDayInterpreter.AnswerExampleRoom, MovingDayInterpreter.AnswerEnterDimensions }));
                    break;
            }
            if (e.ObstructionIssue != null)
            {
                var prompt = e.ObstructionIssue == "conflicting" ? "Should the console table start in the way, or parked south?"
                    : e.ObstructionIssue == "unsupported_position" ? "The console can start only in the way or parked south. Which one?"
                    : "This example always includes one console table. Should it start in the way, or parked south?";
                questions.Add(new ProposalQuestion("obstruction_state", prompt,
                    new[] { MovingDayInterpreter.AnswerConsoleCenter, MovingDayInterpreter.AnswerConsoleSouth }));
            }
            if (e.RouteIssue != null)
                questions.Add(new ProposalQuestion("preferred_route", "Both routes were mentioned. Which route should be selected first?",
                    new[] { MovingDayInterpreter.AnswerRouteDirect, MovingDayInterpreter.AnswerRouteNorth }));
            return questions.Count > 3 ? questions.GetRange(0, 3) : questions;
        }

        static List<Assumption> Assumptions(MovingDayInterpreter.Extraction e)
        {
            var width = e.WidthMm.Value;
            var depth = e.DepthMm.Value;
            var outList = new List<Assumption>();
            var size = $"{Metres(width)} by {Metres(depth)}";
            if (e.WidthSource == "example_default" && e.DepthSource == "example_default")
            {
                outList.Add(new Assumption("example_room_dimensions", $"Example room size {size} chosen; not a measurement of your home.",
                    AssumptionSource.ExampleDefault, false));
            }
            else
            {
                outList.Add(new Assumption("room_dimensions_from_request",
                    $"Room size {size} taken from your request; not a confirmed measurement.", AssumptionSource.UserRequest, true));
                foreach (var (which, source, fallback) in new[] { ("width", e.WidthSource, "6 m"), ("depth", e.DepthSource, "5 m") })
                    if (source == "example_default")
                        outList.Add(new Assumption($"example_room_{which}", $"Room {which} uses the {fallback} example value; not a measurement.",
                            AssumptionSource.ExampleDefault, false));
            }
            if (e.UnitInferred != null)
            {
                var unitWord = e.UnitInferred == "m" ? "metres" : e.UnitInferred == "cm" ? "centimetres" : "millimetres";
                var spoken = e.SpokenDimensions.Count > 0 ? e.SpokenDimensions[0] : "the room size";
                if (spoken.Length > 80)
                    spoken = spoken.Substring(0, 80);
                outList.Add(new Assumption("unit_interpreted", $"No unit was given; '{spoken}' was read as {unitWord}.",
                    AssumptionSource.UserRequest, true));
            }
            if (e.PayloadCatalogId == null)
                outList.Add(new Assumption("payload_default", "No item was named; using the standard sofa, the only supported item.",
                    AssumptionSource.Template, true));
            if (e.Obstruction == null)
                outList.Add(new Assumption("obstruction_default",
                    "Console table position not stated; placed in the way, as in the example layout.", AssumptionSource.Template, true));
            if (e.Route == null)
                outList.Add(new Assumption("route_default", "Route not stated; the Direct approach is selected.", AssumptionSource.Template, true));
            outList.Add(MovingDayTemplate.UprightAssumption);
            return outList;
        }
    }
}
