using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tryscape.Application.Creation;
using Tryscape.Content.MovingDay;
using Tryscape.Core.Canonical;
using Tryscape.Core.Commands;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;
using Tryscape.Core.Validation;
using Tryscape.Infrastructure.Json;

namespace Tryscape.Tests.EditMode.Core
{
    static class Proposals
    {
        public static string ValidReadyJson => Fixtures.Text("proposals/valid_ready.json");

        public static SceneProposal ValidReady()
        {
            var parsed = CreationJson.ReadProposal(ValidReadyJson);
            Assert.That(parsed.Succeeded, () => string.Join(", ", parsed.Errors));
            return parsed.Proposal;
        }

        /// <summary>valid_ready.json with a JSON edit applied (for tampered-proposal checks).</summary>
        public static SceneProposal Edited(Action<JObject> edit)
        {
            var doc = JObject.Parse(ValidReadyJson);
            edit(doc);
            var parsed = CreationJson.ReadProposal(doc.ToString());
            Assert.That(parsed.Succeeded, () => "edited proposal must stay shape-valid: " + string.Join(", ", parsed.Errors));
            return parsed.Proposal;
        }

        /// <summary>A pack-consistent ready proposal for other parameters, built by the pack template.</summary>
        public static SceneProposal ReadyFor(MovingDayParameters p, string transcript = "test")
        {
            var scene = MovingDayTemplate.Assemble(p, new SequenceIds(), new[]
            {
                new Assumption("room_dimensions_from_request", "Room size taken from your request; not a confirmed measurement.", AssumptionSource.UserRequest, true),
                MovingDayTemplate.UprightAssumption,
            });
            var intent = new ProposalIntent("moving_day", p.RoomWidthMm, p.RoomDepthMm, "sofa_standard",
                p.Obstruction == ObstructionState.Center ? "center" : "parked_south", p.RouteId);
            return new SceneProposal(Guid.NewGuid(), ProposalStatus.Ready, transcript, intent, scene, Array.Empty<ProposalQuestion>(),
                scene.Assumptions, "deterministic_rules", "1.0.0");
        }

        public static SceneProposal Clarification(string transcript = "Prepare my room for a delivery") =>
            new SceneProposal(Guid.NewGuid(), ProposalStatus.NeedsClarification, transcript, null, null,
                new[] { new ProposalQuestion("room_dimensions", "No room size was given.", new[] { "Use 6 m by 5 m example", ClarificationAnswers.EnterDimensions }) },
                Array.Empty<Assumption>(), "deterministic_rules", "1.0.0");
    }

    public sealed class CreationJsonTests
    {
        [Test]
        public void SharedProposalFixturesParseExactlyAsTheirNamesSay()
        {
            var files = Fixtures.Files("proposals").ToArray();
            Assert.That(files.Length, Is.GreaterThanOrEqualTo(10));
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var parsed = CreationJson.ReadProposal(File.ReadAllText(file));
                Assert.That(parsed.Succeeded, Is.EqualTo(name.StartsWith("valid_", StringComparison.Ordinal)), name + ": " + string.Join(", ", parsed.Errors));
            }
        }

        [Test]
        public void ReadyFixtureCarriesIntentSceneAndProviderMetadata()
        {
            var p = Proposals.ValidReady();
            Assert.That(p.Status, Is.EqualTo(ProposalStatus.Ready));
            Assert.That(p.Intent.RoomWidthMm, Is.EqualTo(6000));
            Assert.That(p.Intent.ObstructionState, Is.EqualTo("center"));
            Assert.That(p.Scene.Room.DepthMm, Is.EqualTo(5000));
            Assert.That(p.ProviderId, Is.EqualTo("backend_orchestrator"));
        }

        [Test]
        public void JobRecordsFollowTheSchemaStatusRulesAndKind()
        {
            Assert.That(CreationJson.TryReadJob(Fixtures.Text("api/valid_job_succeeded_clarification.json"), "scene_proposal", out var job), Is.True);
            Assert.That(job.IsTerminal, Is.True);
            Assert.That(CreationJson.ReadProposal(job.Result).Proposal.Status, Is.EqualTo(ProposalStatus.NeedsClarification));
            Assert.That(CreationJson.TryReadJob(Fixtures.Text("api/valid_job_failed.json"), "scene_proposal", out var failed), Is.True);
            Assert.That(failed.ErrorCode, Is.EqualTo("provider_unavailable"));
            Assert.That(CreationJson.TryReadJob(Fixtures.Text("api/invalid_job_cancelled_with_result.json"), "scene_proposal", out _), Is.False);
            Assert.That(CreationJson.TryReadJob(Fixtures.Text("api/valid_job_succeeded_transcription.json"), "scene_proposal", out _), Is.False,
                "a transcription job must not be accepted where a scene proposal is expected");
            Assert.That(CreationJson.TryReadReceipt(Fixtures.Text("api/valid_job_receipt.json"), out var id, out var poll), Is.True);
            Assert.That((id.ToString("D"), poll), Is.EqualTo(("40000000-0000-4000-8000-000000000001", 1000)));
        }

        [Test]
        public void MalformedOversizedAndDuplicateKeyProposalsFail()
        {
            Assert.That(CreationJson.ReadProposal("{").Succeeded, Is.False);
            Assert.That(CreationJson.ReadProposal(new string(' ', CreationJson.MaxResponseChars + 1)).Succeeded, Is.False);
            var dup = Proposals.ValidReadyJson.Replace("\"status\": \"ready\",", "\"status\": \"ready\", \"status\": \"ready\",");
            Assert.That(CreationJson.ReadProposal(dup).Succeeded, Is.False);
            var doc = JObject.Parse(Proposals.ValidReadyJson);
            doc["transcript"] = new string('x', 2001);
            Assert.That(CreationJson.ReadProposal(doc.ToString()).Succeeded, Is.False);
            doc = JObject.Parse(Proposals.ValidReadyJson);
            doc["scene"]["entities"][0]["prefab_path"] = "Assets/Evil.prefab";
            Assert.That(CreationJson.ReadProposal(doc.ToString()).Succeeded, Is.False);
        }

        [Test]
        public void RequestBodyMatchesTheSharedRequestFixtureShape()
        {
            var body = JObject.Parse(CreationJson.ProposalJobRequest("Prepare a room", "1.0.0"));
            var fixture = JObject.Parse(Fixtures.Text("api/valid_proposal_request.json"));
            Assert.That(body.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal),
                Is.EqualTo(fixture.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)));
            Assert.That((string)body["transcript"], Is.EqualTo("Prepare a room"));
        }
    }

    public sealed class MovingDayProposalCheckTests
    {
        readonly MovingDayProposals _check = new MovingDayProposals(Fixtures.Catalog);

        [Test]
        public void IndependentReadyFixtureIsAcceptedAndMatchesTheCSharpTemplate()
        {
            var p = Proposals.ValidReady();
            Assert.That(_check.Problem(p), Is.Null);
            // Cross-runtime assembly parity: the Python assembler test pins the same fixture (tests/test_creation.py).
            var rebuilt = MovingDayTemplate.Assemble(MovingDayParameters.Example,
                new SequenceIds(Fixtures.SceneId, Fixtures.ProjectId, Fixtures.SofaId, Fixtures.ConsoleId), p.Scene.Assumptions);
            Assert.That(SceneCanonicalizer.CanonicalBytes(rebuilt), Is.EqualTo(SceneCanonicalizer.CanonicalBytes(p.Scene)));
        }

        [TestCase("scene.entities[1].pose.x_mm", 2600, "scene_not_pack_assembly")]
        [TestCase("scene.room.width_mm", 7000, "scene_not_pack_assembly")]
        [TestCase("intent.room_depth_mm", 6000, "scene_not_pack_assembly")]
        [TestCase("scene.routes[0].speed_mm_per_s", 300, "scene_not_pack_assembly")]
        public void TamperedReadyProposalsAreRejected(string path, int value, string expected)
        {
            var p = Proposals.Edited(doc => doc.SelectToken(path).Replace(value));
            Assert.That(_check.Problem(p), Is.EqualTo(expected));
        }

        [Test]
        public void MeasuredProvenanceUnknownCatalogAndExtraEntitiesAreRejected()
        {
            var measured = Proposals.Edited(doc =>
            {
                doc["scene"]["provenance"] = "user_measured";
                doc["scene"]["measurement_sources"] = JArray.Parse(
                    "[{\"field_path\":\"room.width_mm\",\"source\":\"user_confirmed\",\"confirmed_at\":\"2026-11-05T00:00:00Z\"}," +
                    "{\"field_path\":\"room.depth_mm\",\"source\":\"user_confirmed\",\"confirmed_at\":\"2026-11-05T00:00:00Z\"}]");
            });
            Assert.That(_check.Problem(measured), Is.EqualTo("provenance_not_illustrative"));
            var unknown = Proposals.Edited(doc => doc["scene"]["entities"][1]["catalog_id"] = "grand_piano");
            Assert.That(_check.Problem(unknown), Is.EqualTo("scene_invalid"));
            var payload = Proposals.Edited(doc => doc["intent"]["payload_catalog_id"] = "grand_piano");
            Assert.That(_check.Problem(payload), Is.EqualTo("intent_unsupported"));
            var extra = Proposals.Edited(doc =>
            {
                var copy = doc["scene"]["entities"][1].DeepClone();
                copy["id"] = "30000000-0000-4000-8000-000000000009";
                copy["pose"]["z_mm"] = 800;
                ((JArray)doc["scene"]["entities"]).Add(copy);
            });
            Assert.That(_check.Problem(extra), Is.EqualTo("entities_not_pack_layout"));
        }

        [Test]
        public void ClarificationAndUnsupportedProposalsAreAcceptedForReviewOnly()
        {
            Assert.That(_check.Problem(CreationJson.ReadProposal(Fixtures.Text("proposals/valid_needs_clarification.json")).Proposal), Is.Null);
            Assert.That(_check.Problem(CreationJson.ReadProposal(Fixtures.Text("proposals/valid_unsupported.json")).Proposal), Is.Null);
        }

        [Test]
        public void UncorrectedConfirmationUsesTheProposalSceneItself()
        {
            var p = Proposals.ValidReady();
            Assert.That(MovingDayProposals.TryParameters(p.Intent, out var reviewed), Is.True);
            Assert.That(MovingDayProposals.SceneFor(p, reviewed, new SequenceIds()), Is.SameAs(p.Scene));
        }

        [Test]
        public void HandCorrectionRebuildsFromTemplateAndReplacesOnlyAffectedAssumptions()
        {
            var p = Proposals.ReadyFor(MovingDayParameters.Example.With(routeId: MovingDayPack.DirectRouteId));
            MovingDayProposals.TryParameters(p.Intent, out var reviewed);
            var corrected = MovingDayProposals.SceneFor(p, reviewed.With(widthMm: 7000, obstruction: ObstructionState.ParkedSouth), new SequenceIds());
            Assert.That(corrected.Room.WidthMm, Is.EqualTo(7000));
            Assert.That(corrected.Room.DepthMm, Is.EqualTo(5000));
            var console = corrected.Entities.Single(e => e.Role == EntityRole.Blocker);
            Assert.That((console.Pose.XMm, console.Pose.ZMm), Is.EqualTo((2500, 800)));
            var codes = corrected.Assumptions.Select(a => a.Code).ToArray();
            Assert.That(codes, Does.Contain(MovingDayProposals.HandCorrectedCode));
            Assert.That(codes, Does.Not.Contain("room_dimensions_from_request"));
            Assert.That(codes, Does.Contain("upright_fixed_orientation"));
            Assert.That(corrected.Provenance, Is.EqualTo(Provenance.Illustrative));
            Assert.That(corrected.MeasurementSources, Is.Empty);
            Assert.That(SceneValidator.Validate(corrected, Fixtures.Catalog).IsValid, Is.True);
        }

        [Test]
        public void ConfirmedProposalScenesWorkWithTheExistingEvaluator()
        {
            // Reference proposal = D06 F01: Direct is blocked by the console at fraction 11/120, contact x 1275.
            var f01 = Proposals.ValidReady().Scene;
            Assert.That(SceneSession.TryCreate(f01, Fixtures.Catalog, out _, out _), Is.True);
            var blocked = RouteEvaluator.Evaluate(f01, Fixtures.Catalog, "direct");
            Assert.That(blocked.Outcome, Is.EqualTo(EvaluationOutcome.BlockedInModel));
            Assert.That(blocked.FirstViolation.EntryFraction, Is.EqualTo(new Rational(11, 120)));
            Assert.That(blocked.FirstViolation.ContactXMm, Is.EqualTo(Rational.FromInteger(1275)));
            Assert.That(RouteEvaluator.Evaluate(f01, Fixtures.Catalog, "north_detour").Outcome, Is.EqualTo(EvaluationOutcome.ClearInModel));

            // Parked south (D06 F02 position): Direct clear, 4300 mm.
            var south = Proposals.ReadyFor(MovingDayParameters.Example.With(obstruction: ObstructionState.ParkedSouth)).Scene;
            var clear = RouteEvaluator.Evaluate(south, Fixtures.Catalog, "direct");
            Assert.That(clear.Outcome, Is.EqualTo(EvaluationOutcome.ClearInModel));
            Assert.That(clear.RouteLengthMm, Is.EqualTo(4300));

            // D06.5 variant: a 7 x 6 m room keeps the fixed internal layout, so the same contact is found.
            var variant = Proposals.ReadyFor(MovingDayParameters.Example.With(widthMm: 7000, depthMm: 6000)).Scene;
            var v = RouteEvaluator.Evaluate(variant, Fixtures.Catalog, "direct");
            Assert.That(v.Outcome, Is.EqualTo(EvaluationOutcome.BlockedInModel));
            Assert.That(v.FirstViolation.EntryFraction, Is.EqualTo(new Rational(11, 120)));
            Assert.That(v.SceneHash, Is.Not.EqualTo(blocked.SceneHash), "different room must give a different scene identity");
        }
    }

    public sealed class CreationCoordinatorTests
    {
        SynchronizationContext _previous;

        [SetUp]
        public void SetUp()
        {
            // Continuations run inline so each step is deterministic; Unity's context only pumps in the editor loop.
            _previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown() => SynchronizationContext.SetSynchronizationContext(_previous);

        sealed class ControlledProvider : ISceneProposalProvider
        {
            public readonly List<(string transcript, TaskCompletionSource<ProposalOutcome> pending, CancellationToken token)> Calls =
                new List<(string, TaskCompletionSource<ProposalOutcome>, CancellationToken)>();

            public Task<ProposalOutcome> ProposeAsync(string transcript, CancellationToken cancellationToken)
            {
                var pending = new TaskCompletionSource<ProposalOutcome>();
                Calls.Add((transcript, pending, cancellationToken));
                return pending.Task;
            }
        }

        sealed class HangingProvider : ISceneProposalProvider
        {
            public async Task<ProposalOutcome> ProposeAsync(string transcript, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            }
        }

        sealed class ThrowingProvider : ISceneProposalProvider
        {
            public Task<ProposalOutcome> ProposeAsync(string transcript, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("socket closed by internal-host:5432");
        }

        static CreationCoordinator Make(ISceneProposalProvider provider, TimeSpan? budget = null) =>
            new CreationCoordinator(provider, new MovingDayProposals(Fixtures.Catalog), budget);

        [Test]
        public void ReadyProposalReachesReviewAndIsConfirmable()
        {
            var provider = new ControlledProvider();
            var c = Make(provider);
            var task = c.Submit("Six by five metres");
            Assert.That(c.State, Is.EqualTo(CreationState.Proposing));
            provider.Calls[0].pending.SetResult(ProposalOutcome.Of(Proposals.ValidReady()));
            Assert.That(task.IsCompleted, Is.True);
            Assert.That(c.State, Is.EqualTo(CreationState.Reviewing));
            Assert.That(c.TryGetConfirmable(out var p, out var generation), Is.True);
            Assert.That(p.Intent.RoomWidthMm, Is.EqualTo(6000));
            Assert.That(c.IsCurrentGeneration(generation), Is.True);
        }

        [Test]
        public void TamperedProposalNeverReachesReview()
        {
            var provider = new ControlledProvider();
            var c = Make(provider);
            _ = c.Submit("x");
            provider.Calls[0].pending.SetResult(ProposalOutcome.Of(Proposals.Edited(d => d["scene"]["entities"][1]["pose"]["x_mm"] = 3000)));
            Assert.That(c.State, Is.EqualTo(CreationState.Failed));
            Assert.That(c.Failure.Failure, Is.EqualTo(ProposalFailure.InvalidResponse));
            Assert.That(c.Proposal, Is.Null);
        }

        [Test]
        public void CancelledOperationIgnoresItsLateResult()
        {
            var provider = new ControlledProvider();
            var c = Make(provider);
            _ = c.Submit("x");
            c.Cancel();
            Assert.That(provider.Calls[0].token.IsCancellationRequested, Is.True, "cancellation must reach the provider");
            provider.Calls[0].pending.SetResult(ProposalOutcome.Of(Proposals.ValidReady())); // provider ignored the token
            Assert.That(c.State, Is.EqualTo(CreationState.Idle));
            Assert.That(c.Proposal, Is.Null);
            Assert.That(c.DiscardedResults, Is.EqualTo(1));
        }

        [Test]
        public void SupersededOperationCannotOverwriteTheNewerOne()
        {
            var provider = new ControlledProvider();
            var c = Make(provider);
            _ = c.Submit("first");
            _ = c.Submit("second");
            Assert.That(provider.Calls[0].token.IsCancellationRequested, Is.True);
            provider.Calls[1].pending.SetResult(ProposalOutcome.Of(Proposals.ReadyFor(MovingDayParameters.Example.With(widthMm: 7000), "second")));
            provider.Calls[0].pending.SetResult(ProposalOutcome.Of(Proposals.ValidReady())); // late, obsolete
            Assert.That(c.Proposal.Transcript, Is.EqualTo("second"));
            Assert.That(c.Proposal.Intent.RoomWidthMm, Is.EqualTo(7000));
            Assert.That(c.DiscardedResults, Is.EqualTo(1));
        }

        [Test]
        public void SceneReplacedDuringProposalDiscardsTheResult()
        {
            var provider = new ControlledProvider();
            var c = Make(provider);
            _ = c.Submit("x");
            c.SceneReplaced(); // e.g. the user created a scene with hands meanwhile
            provider.Calls[0].pending.SetResult(ProposalOutcome.Of(Proposals.ValidReady()));
            Assert.That(c.State, Is.EqualTo(CreationState.Idle));
            Assert.That(c.TryGetConfirmable(out _, out _), Is.False);
            Assert.That(c.DiscardedResults, Is.EqualTo(1));
        }

        [Test]
        public void SceneReplacedAfterReviewMakesTheProposalUnconfirmable()
        {
            var provider = new ControlledProvider();
            var c = Make(provider);
            _ = c.Submit("x");
            provider.Calls[0].pending.SetResult(ProposalOutcome.Of(Proposals.ValidReady()));
            Assert.That(c.TryGetConfirmable(out _, out var generation), Is.True);
            c.SceneReplaced();
            Assert.That(c.IsCurrentGeneration(generation), Is.False);
            Assert.That(c.TryGetConfirmable(out _, out _), Is.False);
        }

        [Test]
        public void SuspendCancelsInFlightInterpretation()
        {
            var provider = new ControlledProvider();
            var c = Make(provider);
            _ = c.Submit("x");
            c.Suspend();
            Assert.That(provider.Calls[0].token.IsCancellationRequested, Is.True);
            provider.Calls[0].pending.SetResult(ProposalOutcome.Of(Proposals.ValidReady()));
            Assert.That(c.State, Is.EqualTo(CreationState.Idle));
        }

        [Test]
        public void BudgetExpiryIsReportedAsTimeout()
        {
            var c = Make(new HangingProvider(), TimeSpan.FromMilliseconds(50));
            var task = c.Submit("x");
            Assert.That(task.Wait(TimeSpan.FromSeconds(5)), Is.True, "budget must end the operation");
            Assert.That(c.State, Is.EqualTo(CreationState.Failed));
            Assert.That(c.Failure.Failure, Is.EqualTo(ProposalFailure.Timeout));
            Assert.That(c.Failure.Retryable, Is.True);
        }

        [Test]
        public void ProviderExceptionsAndFailuresBecomeSafeTypedFailures()
        {
            var c = Make(new ThrowingProvider());
            _ = c.Submit("x");
            Assert.That(c.State, Is.EqualTo(CreationState.Failed));
            Assert.That(c.Failure.Failure, Is.EqualTo(ProposalFailure.Unavailable));
            Assert.That(c.Failure.DetailCode, Is.EqualTo("provider_exception"));

            var provider = new ControlledProvider();
            var d = Make(provider);
            _ = d.Submit("x");
            provider.Calls[0].pending.SetResult(ProposalOutcome.Failed(ProposalFailure.Rejected, "rate_limited", true));
            Assert.That(d.Failure.DetailCode, Is.EqualTo("rate_limited"));
        }

        [Test]
        public void EmptyAndOversizedTranscriptsAreRejectedWithoutCallingTheProvider()
        {
            var provider = new ControlledProvider();
            var c = Make(provider);
            _ = c.Submit("   ");
            Assert.That(c.Failure.DetailCode, Is.EqualTo("transcript_empty"));
            _ = c.Submit(new string('a', SceneProposal.MaxTranscriptChars + 1));
            Assert.That(c.Failure.DetailCode, Is.EqualTo("transcript_too_long"));
            Assert.That(provider.Calls, Is.Empty);
        }

        [Test]
        public void ClarificationProposalIsReviewableButNotConfirmable()
        {
            var provider = new ControlledProvider();
            var c = Make(provider);
            _ = c.Submit("Prepare my room for a delivery");
            provider.Calls[0].pending.SetResult(ProposalOutcome.Of(Proposals.Clarification()));
            Assert.That(c.State, Is.EqualTo(CreationState.Reviewing));
            Assert.That(c.TryGetConfirmable(out _, out _), Is.False);
        }

        [Test]
        public void AnswersAreAppendedOnTheirOwnLineWithinTheLimit()
        {
            Assert.That(ClarificationAnswers.Append("Prepare my room  ", "Use 6 m by 5 m example"),
                Is.EqualTo("Prepare my room\nAnswer: Use 6 m by 5 m example"));
            Assert.That(ClarificationAnswers.Append(new string('a', 1990), "Use 6 m by 5 m example"), Is.Null);
        }
    }
}
