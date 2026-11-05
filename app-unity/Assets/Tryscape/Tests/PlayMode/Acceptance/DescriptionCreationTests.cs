using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Tryscape.Application.Creation;
using Tryscape.Application.Editing;
using Tryscape.Content.MovingDay;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;
using Tryscape.Infrastructure.Json;
using Tryscape.Unity;
using Tryscape.Unity.Creation;
using Tryscape.Unity.Experience;
using Tryscape.Unity.Presentation;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tryscape.Tests.PlayMode.Acceptance
{
    /// <summary>
    /// Phase 3 creation from a typed development transcript, driven through the same panel dispatch as a hand press:
    /// transcript, proposal, review, correction with hands, confirmation, staged build, then the existing rehearsal.
    /// <see cref="Rig"/> builds the scene. In the "Experience" category the proposal source is scripted, so timing,
    /// cancellation and late results are controlled; the interpreter itself is covered by backend tests.
    /// <see cref="LiveBackendCreationTests"/> runs the same path against a real local service.
    /// </summary>
    public sealed class Rig : IDisposable
    {
        const string RegistryPath = "Assets/Tryscape/Content/MovingDay/MovingDayPrefabRegistry.asset";

        public static readonly string[] PanelIds =
        {
            "CreateWithHands", "DescribeTyped", "SpeakSituation", "OpenSaved", "Help", "ResumeEditing",
            "Preset0", "Preset1", "Preset2", "Preset3", "SubmitTranscript", "CancelProposal", "DescribeBack",
            "Choice0", "Choice1", "Choice2", "Choice3", "ConfirmProposal", "AdjustProposal", "ReviewHands", "DescribeAgain", "ReviewMenu",
            "WidthMinus", "WidthMinusFine", "WidthPlusFine", "WidthPlus", "DepthMinus", "DepthMinusFine", "DepthPlusFine", "DepthPlus",
            "ObstructionCenter", "ObstructionSouth", "RouteDirect", "RouteNorth", "ConfirmCreate", "BackToWelcome",
            "Rotate", "ToggleRoute", "Undo", "Redo", "Reset", "ZoomOut", "ZoomIn", "Recenter", "Menu", "Rehearse", "PlayPause", "Details",
        };

        public GameObject Root;
        public TryscapeApp App;
        public PanelController Panel;

        public IEnumerator Build()
        {
            Root = new GameObject("Test Root");
            var tabletop = new GameObject("Tabletop");
            tabletop.transform.SetParent(Root.transform, false);
            var tabletopController = tabletop.AddComponent<TabletopController>();
            var presenter = tabletop.AddComponent<ScenePresenter>();
#if UNITY_EDITOR
            var registry = UnityEditor.AssetDatabase.LoadAssetAtPath<PrefabRegistry>(RegistryPath);
#else
            PrefabRegistry registry = null;
#endif
            Assert.IsNotNull(registry, "Prefab registry missing; run Tryscape.Editor.SceneBuilder.BuildBootstrapBatch");
            presenter.Configure(registry, null, null, null, null);
            var ghostGo = new GameObject("Ghost");
            ghostGo.transform.SetParent(tabletop.transform, false);
            var ghost = ghostGo.AddComponent<PlacementGhost>();

            var panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(Root.transform, false);
            Panel = panelGo.AddComponent<PanelController>();
            var buttons = PanelIds.Select(id =>
            {
                var go = new GameObject(id, typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
                go.transform.SetParent(panelGo.transform, false);
                var label = new GameObject("Text", typeof(RectTransform), typeof(UnityEngine.UI.Text));
                label.transform.SetParent(go.transform, false);
                return go.GetComponent<UnityEngine.UI.Button>();
            }).ToArray();
            var reviewCard = new GameObject("ReviewCard", typeof(RectTransform), typeof(UnityEngine.UI.Text)).GetComponent<UnityEngine.UI.Text>();
            reviewCard.transform.SetParent(panelGo.transform, false);
            Panel.Configure(null, null, null, null, null, null, buttons, null, null, null, reviewCard);

            var appGo = new GameObject("App");
            appGo.transform.SetParent(Root.transform, false);
            appGo.SetActive(false);
            App = appGo.AddComponent<TryscapeApp>();
            App.Configure(presenter, ghost, Panel, tabletopController);
            appGo.SetActive(true);
            yield return null;
        }

        public void Dispose()
        {
            if (Root != null)
                UnityEngine.Object.Destroy(Root);
        }

        public static string Contracts(string relative) =>
            Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", "..", "contracts", "v1", "fixtures", relative));

        public static SceneProposal Fixture(string relative)
        {
            var parsed = CreationJson.ReadProposal(File.ReadAllText(Contracts(relative)));
            Assert.That(parsed.Succeeded, () => string.Join(", ", parsed.Errors));
            return parsed.Proposal;
        }

        /// <summary>A pack-consistent ready proposal (as the backend assembler would build it) for other parameters.</summary>
        public static SceneProposal ReadyFor(MovingDayParameters p, string transcript)
        {
            var assumptions = new[]
            {
                new Assumption("room_dimensions_from_request", "Room size taken from your request; not a confirmed measurement.", AssumptionSource.UserRequest, true),
                MovingDayTemplate.UprightAssumption,
            };
            var scene = MovingDayTemplate.Assemble(p, new RandomIdGenerator(), assumptions);
            var intent = new ProposalIntent("moving_day", p.RoomWidthMm, p.RoomDepthMm, "sofa_standard",
                p.Obstruction == ObstructionState.Center ? "center" : "parked_south", p.RouteId);
            return new SceneProposal(Guid.NewGuid(), ProposalStatus.Ready, transcript, intent, scene, Array.Empty<ProposalQuestion>(),
                assumptions, "deterministic_rules", "1.0.0");
        }

        public string Press(string id)
        {
            Assert.That(Panel.IsInteractable(id), Is.True, $"'{id}' should be pressable in {App.Mode}");
            Panel.Press(id);
            return App.Status;
        }

        public IEnumerator Until(Func<bool> condition, float seconds, string what)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > end)
                    Assert.Fail($"Timed out waiting for {what}; mode={App.Mode} creation={App.Creation.State} status='{App.Status}'");
                yield return null;
            }
        }
    }

    [Category("Experience")]
    public sealed class DescriptionCreationTests
    {
        /// <summary>Worst-case provider: it ignores cancellation, so results can arrive after the operation ended.</summary>
        sealed class ScriptedProvider : ISceneProposalProvider
        {
            public readonly List<(string transcript, TaskCompletionSource<ProposalOutcome> pending, CancellationToken token)> Calls =
                new List<(string, TaskCompletionSource<ProposalOutcome>, CancellationToken)>();

            public Task<ProposalOutcome> ProposeAsync(string transcript, CancellationToken cancellationToken)
            {
                var pending = new TaskCompletionSource<ProposalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                Calls.Add((transcript, pending, cancellationToken));
                return pending.Task;
            }

            /// <summary>Delivers a result for a call whose operation was already cancelled, superseded or suspended.</summary>
            public void CompleteLate(int index, ProposalOutcome outcome) => Calls[index].pending.TrySetResult(outcome);
        }

        Rig _rig;
        ScriptedProvider _provider;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _rig = new Rig();
            yield return _rig.Build();
            _provider = new ScriptedProvider();
            _rig.App.UseProposalProvider(_provider);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _rig.Dispose();
            yield return null;
        }

        IEnumerator SubmitPreset(int preset)
        {
            _rig.Press("DescribeTyped");
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.DescribingTyped));
            StringAssert.Contains("DEVELOPMENT INPUT", _rig.App.Status);
            _rig.Press("Preset" + preset);
            Assert.That(_rig.App.TypedTranscript, Is.EqualTo(PanelController.PresetTranscripts[preset]));
            _rig.Press("SubmitTranscript");
            Assert.That(_rig.App.Creation.State, Is.EqualTo(CreationState.Proposing));
            Assert.That(_rig.Panel.IsInteractable("CancelProposal"), Is.True);
            yield return null;
        }

        IEnumerator Answer(int call, SceneProposal proposal)
        {
            _provider.Calls[call].pending.SetResult(ProposalOutcome.Of(proposal));
            yield return _rig.Until(() => _rig.App.Creation.State != CreationState.Proposing, 2, "proposal applied");
        }

        [UnityTest]
        public IEnumerator TypedTranscriptBecomesAReviewedSceneThatRehearses()
        {
            yield return SubmitPreset(0);
            Assert.That(_provider.Calls[0].transcript, Is.EqualTo(PanelController.PresetTranscripts[0]));
            Assert.That(_rig.App.Session, Is.Null, "nothing is built before confirmation");
            yield return Answer(0, Rig.Fixture("proposals/valid_ready.json"));
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.ReviewingProposal));
            var review = _rig.App.ReviewText;
            StringAssert.Contains("Typed development input (not voice)", review);
            StringAssert.Contains("Room 6.0 m x 5.0 m", review);
            StringAssert.Contains("Console in the way", review);
            StringAssert.Contains("illustrative, not measurements", review);
            StringAssert.Contains("Check: Room size taken from your request", review);
            Assert.That(_rig.Panel.IsInteractable("Choice0"), Is.False, "no clarification choices for a ready proposal");

            _rig.Press("ConfirmProposal");
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.Editing), _rig.App.Status);
            Assert.That(_rig.App.Session.Current.Room.WidthMm, Is.EqualTo(6000));
            Assert.That(_rig.App.Session.Current.Provenance, Is.EqualTo(Provenance.Illustrative));
            Assert.That(_rig.App.Presenter.Views.Count, Is.EqualTo(2));
            yield return null;

            _rig.Press("Rehearse");
            var run = _rig.App.LatestRun;
            Assert.That(run.Evidence.Outcome, Is.EqualTo(EvaluationOutcome.BlockedInModel));
            Assert.That(run.Evidence.FirstViolation.EntryFraction, Is.EqualTo(new Rational(11, 120))); // D06 F01
        }

        [UnityTest]
        public IEnumerator HandCorrectionChangesWhatIsBuiltAndTheRehearsalResult()
        {
            yield return SubmitPreset(0);
            yield return Answer(0, Rig.Fixture("proposals/valid_ready.json"));
            _rig.Press("AdjustProposal");
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.CorrectingProposal));
            Assert.That(_rig.Panel.LabelOf("ConfirmCreate"), Is.EqualTo("Confirm and build"));
            _rig.Press("WidthPlus");
            _rig.Press("ObstructionSouth");
            _rig.Press("BackToWelcome"); // labelled "Review": returns to review, keeps corrections
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.ReviewingProposal));
            StringAssert.Contains("Edited with hand controls", _rig.App.ReviewText);
            StringAssert.Contains("Room 6.5 m x 5.0 m", _rig.App.ReviewText);
            _rig.Press("ConfirmProposal");
            var scene = _rig.App.Session.Current;
            Assert.That(scene.Room.WidthMm, Is.EqualTo(6500));
            var console = scene.Entities.Single(e => e.Role == EntityRole.Blocker);
            Assert.That((console.Pose.XMm, console.Pose.ZMm), Is.EqualTo((2500, 800)));
            Assert.That(scene.Assumptions.Select(a => a.Code), Does.Contain(MovingDayProposals.HandCorrectedCode));
            yield return null;
            _rig.Press("Rehearse");
            Assert.That(_rig.App.LatestRun.Evidence.Outcome, Is.EqualTo(EvaluationOutcome.ClearInModel)); // D06 F02
            Assert.That(_rig.App.LatestRun.Evidence.RouteLengthMm, Is.EqualTo(4300));
        }

        [UnityTest]
        public IEnumerator DifferentRequestsBuildDifferentRooms()
        {
            yield return SubmitPreset(1);
            var p = MovingDayParameters.Example.With(widthMm: 7000, depthMm: 6000, obstruction: ObstructionState.ParkedSouth, routeId: MovingDayPack.NorthRouteId);
            yield return Answer(0, Rig.ReadyFor(p, PanelController.PresetTranscripts[1]));
            _rig.Press("ConfirmProposal");
            var scene = _rig.App.Session.Current;
            Assert.That((scene.Room.WidthMm, scene.Room.DepthMm), Is.EqualTo((7000, 6000)));
            Assert.That(scene.ActiveRouteId, Is.EqualTo(MovingDayPack.NorthRouteId));
            Assert.That(_rig.App.Presenter.Room.WidthMm, Is.EqualTo(7000), "rendered room follows the confirmed scene");
        }

        [UnityTest]
        public IEnumerator ProviderFailureKeepsTheCurrentSceneAndOffersHands()
        {
            _rig.Press("CreateWithHands");
            _rig.Press("ConfirmCreate");
            var before = _rig.App.Session.Current;
            var views = _rig.App.Presenter.Views.Values.ToArray();
            _rig.Press("Menu");
            yield return SubmitPreset(0);
            _provider.Calls[0].pending.SetResult(ProposalOutcome.Failed(ProposalFailure.Unavailable, "network_unreachable", true));
            yield return _rig.Until(() => _rig.App.Creation.State == CreationState.Failed, 2, "failure");
            StringAssert.Contains("not reachable", _rig.App.Status);
            StringAssert.Contains("Create with hands", _rig.App.Status);
            StringAssert.Contains("current scene is unchanged", _rig.App.Status);
            Assert.That(_rig.App.Session.Current, Is.SameAs(before));
            Assert.That(_rig.App.Presenter.Views.Values.ToArray(), Is.EqualTo(views), "no rebuild happened");
            _rig.Press("DescribeBack");
            _rig.Press("ResumeEditing");
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.Editing));
            _rig.Press("Rehearse");
            Assert.That(_rig.App.LatestRun.Evidence.Outcome, Is.EqualTo(EvaluationOutcome.BlockedInModel), "local rehearsal works offline");
        }

        [UnityTest]
        public IEnumerator CancelThenLateResultNeverReplacesTheNewerHandScene()
        {
            yield return SubmitPreset(0);
            _rig.Press("CancelProposal");
            Assert.That(_rig.App.Creation.State, Is.EqualTo(CreationState.Idle));
            StringAssert.Contains("Nothing was changed", _rig.App.Status);
            _rig.Press("DescribeBack");
            _rig.Press("CreateWithHands");
            _rig.Press("WidthPlus");
            _rig.Press("ConfirmCreate");
            var handScene = _rig.App.Session.Current;
            Assert.That(handScene.Room.WidthMm, Is.EqualTo(6500));

            _provider.CompleteLate(0, ProposalOutcome.Of(Rig.Fixture("proposals/valid_ready.json")));
            yield return null;
            yield return null;
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.Editing));
            Assert.That(_rig.App.Session.Current, Is.SameAs(handScene));
            Assert.That(_rig.App.Creation.TryGetConfirmable(out _, out _), Is.False);
        }

        [UnityTest]
        public IEnumerator SupersededProposalCannotReplaceTheNewerOne()
        {
            yield return SubmitPreset(0);
            _rig.Press("DescribeBack");
            yield return SubmitPreset(1); // a newer operation supersedes the first
            Assert.That(_provider.Calls[0].token.IsCancellationRequested, Is.True);
            var p = MovingDayParameters.Example.With(widthMm: 7000, depthMm: 6000);
            yield return Answer(1, Rig.ReadyFor(p, PanelController.PresetTranscripts[1]));
            _provider.CompleteLate(0, ProposalOutcome.Of(Rig.Fixture("proposals/valid_ready.json")));
            yield return null;
            yield return null;
            Assert.That(_rig.App.ProposedParameters.RoomWidthMm, Is.EqualTo(7000));
            _rig.Press("ConfirmProposal");
            Assert.That(_rig.App.Session.Current.Room.WidthMm, Is.EqualTo(7000));
        }

        [UnityTest]
        public IEnumerator BackgroundingCancelsInterpretation()
        {
            yield return SubmitPreset(0);
            _rig.App.Suspend(); // what OnApplicationPause(true) / focus loss call
            Assert.That(_provider.Calls[0].token.IsCancellationRequested, Is.True);
            Assert.That(_rig.App.Creation.State, Is.EqualTo(CreationState.Idle));
            StringAssert.Contains("lost focus", _rig.App.Status);
            _provider.CompleteLate(0, ProposalOutcome.Of(Rig.Fixture("proposals/valid_ready.json")));
            yield return null;
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.DescribingTyped));
            Assert.That(_rig.App.Session, Is.Null);
        }

        [UnityTest]
        public IEnumerator TimeoutReportsAndKeepsStateIntact()
        {
            _rig.App.UseProposalProvider(_provider, TimeSpan.FromMilliseconds(200));
            yield return SubmitPreset(0);
            yield return _rig.Until(() => _rig.App.Creation.State == CreationState.Failed, 3, "timeout");
            Assert.That(_rig.App.Creation.Failure.Failure, Is.EqualTo(ProposalFailure.Timeout));
            StringAssert.Contains("took too long", _rig.App.Status);
            Assert.That(_rig.App.Session, Is.Null);
        }

        [UnityTest]
        public IEnumerator ClarificationChoiceIsResubmittedAsAnAnswer()
        {
            yield return SubmitPreset(2);
            yield return Answer(0, Rig.Fixture("proposals/valid_needs_clarification.json"));
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.ReviewingProposal));
            StringAssert.Contains("Use an example room or enter dimensions?", _rig.App.ReviewText);
            Assert.That(_rig.Panel.LabelOf("Choice0"), Is.EqualTo("Use 6 m by 5 m example"));
            Assert.That(_rig.Panel.LabelOf("Choice1"), Is.EqualTo("Enter dimensions"));
            Assert.That(_rig.Panel.IsInteractable("Choice2"), Is.False);
            Assert.That(_rig.Panel.IsInteractable("ConfirmProposal"), Is.False, "a clarification cannot be built");
            _rig.Press("Choice0");
            Assert.That(_provider.Calls.Count, Is.EqualTo(2));
            Assert.That(_provider.Calls[1].transcript, Is.EqualTo("Prepare my room for a delivery\nAnswer: Use 6 m by 5 m example"));
            yield return Answer(1, Rig.ReadyFor(MovingDayParameters.Example, _provider.Calls[1].transcript));
            Assert.That(_rig.Panel.IsInteractable("ConfirmProposal"), Is.True);
        }

        [UnityTest]
        public IEnumerator EnterDimensionsOpensTheHandEditor()
        {
            yield return SubmitPreset(2);
            yield return Answer(0, Rig.Fixture("proposals/valid_needs_clarification.json"));
            _rig.Press("Choice1");
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.CreatingWithHands));
            Assert.That(_provider.Calls.Count, Is.EqualTo(1), "no request for the local choice");
        }

        [UnityTest]
        public IEnumerator UnsupportedRequestIsExplainedAndCannotBeBuilt()
        {
            yield return SubmitPreset(3);
            yield return Answer(0, Rig.Fixture("proposals/valid_unsupported.json"));
            StringAssert.Contains("Not supported in this build", _rig.App.ReviewText);
            Assert.That(_rig.Panel.IsInteractable("ConfirmProposal"), Is.False);
            Assert.That(_rig.Panel.IsInteractable("ReviewHands"), Is.True);
            _rig.Press("ReviewHands");
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.CreatingWithHands));
        }

        [UnityTest]
        public IEnumerator TamperedProposalIsDiscardedBeforeReview()
        {
            yield return SubmitPreset(0);
            var good = Rig.Fixture("proposals/valid_ready.json");
            var console = good.Scene.Entities.Single(e => e.Role == EntityRole.Blocker);
            var moved = new SceneSpec(good.Scene.SceneId, good.Scene.ProjectId, good.Scene.ScenarioPackId, good.Scene.ScenarioPackVersion,
                good.Scene.CatalogVersion, 0, good.Scene.Room,
                good.Scene.Entities.Select(e => e.Id == console.Id ? new EntitySpec(e.Id, e.CatalogId, e.Role, new Tryscape.Core.Model.Pose(3500, 4000, 0), e.Editable, e.Label) : e),
                good.Scene.Routes, good.Scene.ActiveRouteId, good.Scene.Assumptions, good.Scene.Provenance, good.Scene.MeasurementSources);
            var tampered = new SceneProposal(good.ProposalId, ProposalStatus.Ready, good.Transcript, good.Intent, moved, good.Questions,
                good.Assumptions, good.ProviderId, good.ProviderVersion);
            yield return Answer(0, tampered);
            Assert.That(_rig.App.Creation.State, Is.EqualTo(CreationState.Failed));
            StringAssert.Contains("cannot accept", _rig.App.Status);
            Assert.That(_rig.App.Session, Is.Null);
        }

        [UnityTest]
        public IEnumerator HandOnlyCreationStillWorksWithoutAnyService()
        {
            _rig.App.UseProposalProvider(new BackendSceneProposalProvider(CreationServiceConfig.For(null), "1.0.0"));
            _rig.Press("CreateWithHands");
            _rig.Press("ConfirmCreate");
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.Editing));
            yield return null;
            _rig.Press("Rehearse");
            Assert.That(_rig.App.LatestRun.Evidence.Outcome, Is.EqualTo(EvaluationOutcome.BlockedInModel));
            var scene = _rig.App.Session.Current;
            _rig.Press("Menu");
            _rig.Press("DescribeTyped");
            _rig.Press("Preset0");
            _rig.Press("SubmitTranscript"); // fails immediately: no service in this configuration
            yield return _rig.Until(() => _rig.App.Creation.State == CreationState.Failed, 2, "unconfigured service failure");
            Assert.That(_rig.App.Creation.Failure.DetailCode, Is.EqualTo("service_not_configured"));
            StringAssert.Contains("Create with hands", _rig.App.Status);
            Assert.That(_rig.App.Session.Current, Is.SameAs(scene));
        }
    }

    /// <summary>
    /// Opt-in end-to-end check against a real local creation service: uvicorn plus worker on 127.0.0.1:8000 with
    /// PostgreSQL. Run it with <c>-testCategory Backend</c> while the service is up. The interpreter is the deterministic
    /// development parser, so this proves the client/backend pipeline, not model-assisted interpretation or speech.
    /// </summary>
    [Category("Backend")]
    public sealed class LiveBackendCreationTests
    {
        Rig _rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _rig = new Rig();
            yield return _rig.Build();
            _rig.App.UseProposalProvider(new BackendSceneProposalProvider(CreationServiceConfig.For(CreationServiceConfig.DevelopmentBaseUrl), "1.0.0"));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _rig.Dispose();
            yield return null;
        }

        IEnumerator Interpret(string transcript)
        {
            if (_rig.App.Mode != AppMode.DescribingTyped)
                _rig.Press("DescribeTyped");
            _rig.App.SetTypedTranscript(transcript);
            _rig.Press("SubmitTranscript");
            yield return _rig.Until(() => _rig.App.Creation.State != CreationState.Proposing, 15, "backend proposal");
            Assert.That(_rig.App.Creation.State, Is.EqualTo(CreationState.Reviewing), _rig.App.Status);
        }

        [UnityTest]
        public IEnumerator ReferenceRequestRoundTripsThroughTheServiceAndRehearses()
        {
            yield return Interpret(PanelController.PresetTranscripts[0]);
            var proposal = _rig.App.Creation.Proposal;
            Assert.That(proposal.ProviderId, Is.EqualTo("deterministic_rules"));
            Assert.That(proposal.Intent.RoomWidthMm, Is.EqualTo(6000));
            _rig.Press("ConfirmProposal");
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.Editing), _rig.App.Status);
            yield return null;
            _rig.Press("Rehearse");
            Assert.That(_rig.App.LatestRun.Evidence.FirstViolation.EntryFraction, Is.EqualTo(new Rational(11, 120)));
        }

        [UnityTest]
        public IEnumerator DifferentSpokenParametersGiveDifferentScenesAndClarificationRoundTrips()
        {
            yield return Interpret(PanelController.PresetTranscripts[1]);
            _rig.Press("ConfirmProposal");
            var first = _rig.App.Session.Current;
            Assert.That((first.Room.WidthMm, first.Room.DepthMm, first.ActiveRouteId), Is.EqualTo((7000, 6000, "north_detour")));
            Assert.That(first.Entities.Single(e => e.Role == EntityRole.Blocker).Pose.ZMm, Is.EqualTo(800));

            _rig.Press("Menu");
            yield return Interpret(PanelController.PresetTranscripts[2]);
            Assert.That(_rig.App.Creation.Proposal.Status, Is.EqualTo(ProposalStatus.NeedsClarification));
            _rig.Press("Choice0");
            yield return _rig.Until(() => _rig.App.Creation.State != CreationState.Proposing, 15, "answered proposal");
            Assert.That(_rig.App.Creation.Proposal.Status, Is.EqualTo(ProposalStatus.Ready), _rig.App.Status);
            Assert.That(_rig.App.Creation.Proposal.Assumptions.Select(a => a.Code), Does.Contain("example_room_dimensions"));

            _rig.Press("DescribeAgain");
            yield return Interpret(PanelController.PresetTranscripts[3]);
            Assert.That(_rig.App.Creation.Proposal.Status, Is.EqualTo(ProposalStatus.Unsupported));
            Assert.That(_rig.App.Session.Current, Is.SameAs(first), "unsupported request leaves the scene unchanged");
        }
    }
}
