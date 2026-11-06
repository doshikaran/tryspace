using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tryscape.Application.Creation;
using Tryscape.Content.MovingDay;
using Tryscape.Core.Canonical;
using Tryscape.Infrastructure.Json;

namespace Tryscape.Tests.EditMode.Core
{
    /// <summary>
    /// On-device interpreter (decision D12). Two independent checks per shared case:
    /// <list type="number">
    /// <item>hand-written expectations from interpretation/moving_day_cases.json (status, intent, question fields,
    /// assumption and unsupported codes);</item>
    /// <item>exact parity with the Python service implementation, recorded in interpretation/moving_day_parity.json
    /// (same IDs, wording, choices, assumptions and canonical scene bytes).</item>
    /// </list>
    /// </summary>
    public sealed class LocalInterpreterTests
    {
        static readonly JObject Cases = JObject.Parse(Fixtures.Text("interpretation/moving_day_cases.json"));
        static readonly JObject Parity = JObject.Parse(Fixtures.Text("interpretation/moving_day_parity.json"));

        static IEnumerable<string> CaseIds() => Cases["cases"].Select(c => (string)c["id"]);

        /// <summary>The Python fixture's IDs: 00000000-0000-4000-8000-000000000001 upward.</summary>
        sealed class PythonStyleIds : Tryscape.Core.Model.IIdGenerator
        {
            int _n;
            public Guid NewId() => Guid.Parse($"00000000-0000-4000-8000-{++_n:D12}");
        }

        static SceneProposal Local(string transcript) => new MovingDayLocalProposals(new PythonStyleIds()).Propose(transcript);

        static JObject Case(string id) => (JObject)Cases["cases"].Single(c => (string)c["id"] == id);

        static string Wire(ProposalStatus s) =>
            s == ProposalStatus.Ready ? "ready" : s == ProposalStatus.NeedsClarification ? "needs_clarification" : "unsupported";

        [TestCaseSource(nameof(CaseIds))]
        public void MeetsTheHandWrittenExpectation(string id)
        {
            var c = Case(id);
            var p = Local((string)c["transcript"]);
            Assert.AreEqual((string)c["status"], Wire(p.Status));
            Assert.AreEqual(MovingDayInterpreter.ProviderId, p.ProviderId, "never presented as an AI model");
            var codes = p.Assumptions.Select(a => a.Code).ToList();
            if (p.Status == ProposalStatus.Ready)
            {
                var intent = (JObject)c["intent"];
                Assert.AreEqual((int)intent["room_width_mm"], p.Intent.RoomWidthMm);
                Assert.AreEqual((int)intent["room_depth_mm"], p.Intent.RoomDepthMm);
                Assert.AreEqual((string)intent["payload_catalog_id"], p.Intent.PayloadCatalogId);
                Assert.AreEqual((string)intent["obstruction_state"], p.Intent.ObstructionState);
                Assert.AreEqual((string)intent["preferred_route_id"], p.Intent.PreferredRouteId);
                Assert.IsNull(new MovingDayProposals(MovingDayPack.CreateCatalog()).Problem(p), "client pack check accepts it");
                Assert.AreEqual(codes, p.Scene.Assumptions.Select(a => a.Code).ToList());
            }
            else
            {
                Assert.IsNull(p.Scene);
                Assert.IsNull(p.Intent);
            }
            if (c["question_fields"] is JArray fields)
                Assert.AreEqual(fields.Select(f => (string)f).ToList(), p.Questions.Select(q => q.Field).ToList());
            if (c["unsupported_codes"] is JArray unsupported)
                Assert.AreEqual(unsupported.Select(f => (string)f).ToList(), codes);
            if (c["assumptions_include"] is JArray include)
                foreach (var code in include)
                    CollectionAssert.Contains(codes, (string)code);
            if (c["assumptions_exclude"] is JArray exclude)
                foreach (var code in exclude)
                    CollectionAssert.DoesNotContain(codes, (string)code);
        }

        [TestCaseSource(nameof(CaseIds))]
        public void MatchesThePythonServiceExactly(string id)
        {
            var expectedToken = Parity["cases"].Single(c => (string)c["id"] == id)["proposal"];
            var parsed = CreationJson.ReadProposal(expectedToken);
            Assert.IsTrue(parsed.Succeeded, "parity fixture must satisfy the client contract reader");
            var expected = parsed.Proposal;
            var actual = Local(expected.Transcript);

            Assert.AreEqual(expected.ProposalId, actual.ProposalId);
            Assert.AreEqual(expected.Status, actual.Status);
            Assert.AreEqual(expected.ProviderId, actual.ProviderId);
            Assert.AreEqual(expected.ProviderVersion, actual.ProviderVersion);
            Assert.AreEqual(expected.Questions.Count, actual.Questions.Count);
            for (var i = 0; i < expected.Questions.Count; i++)
            {
                Assert.AreEqual(expected.Questions[i].Field, actual.Questions[i].Field);
                Assert.AreEqual(expected.Questions[i].Prompt, actual.Questions[i].Prompt);
                Assert.AreEqual(expected.Questions[i].Choices, actual.Questions[i].Choices);
            }
            Assert.AreEqual(expected.Assumptions.Count, actual.Assumptions.Count);
            for (var i = 0; i < expected.Assumptions.Count; i++)
                Assert.AreEqual(expected.Assumptions[i], actual.Assumptions[i], $"assumption {i}");
            if (expected.Intent == null)
            {
                Assert.IsNull(actual.Intent);
            }
            else
            {
                Assert.AreEqual(expected.Intent.RoomWidthMm, actual.Intent.RoomWidthMm);
                Assert.AreEqual(expected.Intent.RoomDepthMm, actual.Intent.RoomDepthMm);
                Assert.AreEqual(expected.Intent.ObstructionState, actual.Intent.ObstructionState);
                Assert.AreEqual(expected.Intent.PreferredRouteId, actual.Intent.PreferredRouteId);
                Assert.AreEqual(expected.Intent.PayloadCatalogId, actual.Intent.PayloadCatalogId);
            }
            if (expected.Scene == null)
                Assert.IsNull(actual.Scene);
            else
                Assert.AreEqual(SceneCanonicalizer.CanonicalBytes(expected.Scene), SceneCanonicalizer.CanonicalBytes(actual.Scene),
                    "byte-identical canonical scene");
        }

        [Test]
        public void ClarificationAnswersResolveOnDevice()
        {
            var first = Local("Prepare my room for a delivery");
            Assert.AreEqual(ProposalStatus.NeedsClarification, first.Status);
            var answered = Local("Prepare my room for a delivery\nAnswer: " + MovingDayInterpreter.AnswerExampleRoom);
            Assert.AreEqual(ProposalStatus.Ready, answered.Status);
            Assert.AreEqual(6000, answered.Intent.RoomWidthMm);
            Assert.AreEqual(5000, answered.Intent.RoomDepthMm);
        }

        [Test]
        public void ProviderRejectsEmptyOrOversizedTranscriptsAndHonoursCancellation()
        {
            var provider = new MovingDayLocalProposals(new PythonStyleIds());
            Assert.AreEqual(ProposalFailure.Rejected, provider.ProposeAsync("  ", CancellationToken.None).Result.Failure);
            Assert.AreEqual(ProposalFailure.Rejected,
                provider.ProposeAsync(new string('a', SceneProposal.MaxTranscriptChars + 1), CancellationToken.None).Result.Failure);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.IsTrue(provider.ProposeAsync("six by five metres, sofa", cts.Token).IsCanceled);
            }
            Assert.IsTrue(provider.ProposeAsync("six by five metres, sofa", CancellationToken.None).Result.Succeeded);
        }
    }
}
