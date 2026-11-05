using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using Oculus.Interaction;
using Tryscape.Application.Editing;
using Tryscape.Content.MovingDay;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;
using Tryscape.Unity;
using Tryscape.Unity.Experience;
using Tryscape.Unity.Presentation;
using UnityEngine;
using UnityEngine.TestTools;
using ModelPose = Tryscape.Core.Model.Pose;
using Pose = UnityEngine.Pose;

namespace Tryscape.Tests.PlayMode.Acceptance
{
    /// <summary>
    /// Phase 1 integrated slice without XR hardware: hands-only creation, console drag through the real Interaction SDK
    /// Grabbable, invalid placement, undo/redo/reset through the panel, and tabletop rescale independence (AC06 subset,
    /// AC11, AC13, AC20). Pointer events are synthetic: they validate input-to-command wiring, not tracking quality.
    /// </summary>
    [Category("Experience")]
    public sealed class HandsOnlySliceTests
    {
        const string RegistryPath = "Assets/Tryscape/Content/MovingDay/MovingDayPrefabRegistry.asset";
        const int RightHand = 7;
        const int LeftHand = 8;

        static readonly string[] PanelIds =
        {
            "CreateWithHands", "SpeakSituation", "OpenSaved", "Help", "ResumeEditing", "WidthMinus", "WidthMinusFine",
            "WidthPlusFine", "WidthPlus", "DepthMinus", "DepthMinusFine", "DepthPlusFine", "DepthPlus",
            "ObstructionCenter", "ObstructionSouth", "RouteDirect", "RouteNorth", "ConfirmCreate", "BackToWelcome",
            "Rotate", "ToggleRoute", "Undo", "Redo", "Reset", "ZoomOut", "ZoomIn", "Recenter", "Menu",
            "Rehearse", "PlayPause", "Details",
            "KeepA", "KeepB", "Compare", "Save", "InspectA", "InspectB", "InspectLive", "CompareReplay", "CompareBack",
            "ProjectPrev", "ProjectNext", "ProjectOpen", "ProjectDelete", "ProjectsBack",
        };

        GameObject _root;
        TryscapeApp _app;
        PanelController _panel;
        TabletopController _tabletop;

        string _storeRoot;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _storeRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tryscape-play-" + Guid.NewGuid().ToString("N"));
            yield return BuildApp();
        }

        /// <summary>Builds a fresh app over the test's storage folder (also used to simulate a restart).</summary>
        IEnumerator BuildApp()
        {
            _root = new GameObject("Test Root");
            var tabletop = new GameObject("Tabletop");
            tabletop.transform.SetParent(_root.transform, false);
            tabletop.transform.position = new Vector3(0.2f, 0.7f, 0.5f);
            _tabletop = tabletop.AddComponent<TabletopController>();
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
            panelGo.transform.SetParent(_root.transform, false);
            _panel = panelGo.AddComponent<PanelController>();
            var buttons = PanelIds.Select(id =>
            {
                var go = new GameObject(id, typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
                go.transform.SetParent(panelGo.transform, false);
                var label = new GameObject("Text", typeof(RectTransform), typeof(UnityEngine.UI.Text));
                label.transform.SetParent(go.transform, false); // so tests read the label a user would read
                return go.GetComponent<UnityEngine.UI.Button>();
            }).ToArray();
            _panel.Configure(null, null, null, null, null, null, buttons);

            var appGo = new GameObject("App");
            appGo.transform.SetParent(_root.transform, false);
            appGo.SetActive(false);
            _app = appGo.AddComponent<TryscapeApp>();
            _app.Configure(presenter, ghost, _panel, _tabletop);
            appGo.SetActive(true);
            _app.UseProjectStore(new Tryscape.Infrastructure.Persistence.ProjectStore(_storeRoot, MovingDayPack.CreateCatalog()));
            yield return null; // Start: panel binds to the app
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            UnityEngine.Object.Destroy(_root);
            yield return null;
            if (_storeRoot != null && System.IO.Directory.Exists(_storeRoot))
                System.IO.Directory.Delete(_storeRoot, true);
        }

        /// <summary>
        /// D03.6 / AC14 / AC15 through the real panel and the real Interaction SDK grab:
        /// <list type="number">
        /// <item>rehearse, keep A, drag the console, rehearse, keep B, then compare and inspect both;</item>
        /// <item>zoom, then save;</item>
        /// <item>destroy the whole app and build a new one over the same storage folder;</item>
        /// <item>reopen, then inspect and compare again.</item>
        /// </list>
        /// The "restart" is a new app object graph in the same Editor process, not a process kill on device.
        /// </summary>
        [UnityTest]
        public IEnumerator CompareTwoAttemptsSaveRestartAndReopen()
        {
            yield return CreateExample();
            _panel.Press("Rehearse");
            Assert.AreEqual(EvaluationOutcome.BlockedInModel, _app.LatestRun.Evidence.Outcome);
            _panel.Press("KeepA");
            Assert.AreEqual("A: rev 0", _panel.LabelOf("KeepA"));
            Assert.IsFalse(_panel.IsInteractable("Compare"), "comparison needs both attempts");
            var aHash = _app.Attempts.A.Run.Evidence.EvidenceHash();

            yield return DragConsole(RightHand, World(2500, 2500), World(2512, 790));
            Assert.AreEqual(new ModelPose(2500, 800, 0), ConsolePose, _app.Status);
            StringAssert.Contains("OUT OF DATE", _app.ResultText, "the live result is stale after the edit");
            Assert.AreEqual(aHash, _app.Attempts.A.Run.Evidence.EvidenceHash(), "Attempt A is unchanged by the edit");
            _panel.Press("Rehearse");
            Assert.AreEqual(EvaluationOutcome.ClearInModel, _app.LatestRun.Evidence.Outcome);
            _panel.Press("KeepB");

            _panel.Press("Compare");
            Assert.AreEqual(AppMode.Comparing, _app.Mode);
            var card = _app.ComparisonText;
            StringAssert.Contains("Attempt A: rev 0, Direct approach, Blocked in this model, 4.30 m route (obstruction: the console table)", card);
            StringAssert.Contains("Attempt B: rev 1, Direct approach, Clear in this model, 4.30 m route", card);
            StringAssert.Contains("Changed: Console table moved from (2.50, 2.50) m to (2.50, 0.80) m.", card);
            StringAssert.Contains("Preparation: Before delivery, move the console table out of the direct approach, as in Attempt B.", card);
            StringAssert.Contains("Floor footprint only", card);

            _panel.Press("InspectA");
            var (ax, az) = ModelSpaceMapping.ToLocal(_app.Session.Current.Room, 2500, 2500);
            Assert.That(Vector3.Distance(Console.transform.localPosition, new Vector3(ax, 0, az)), Is.LessThan(1e-5f), "A's snapshot is shown");
            Assert.IsNull(_app.Drag, "snapshots are read-only: no drags while inspecting");
            Assert.IsTrue(_app.Highlight.IsShown, "A's obstruction is highlighted");
            _panel.Press("CompareReplay");
            Assert.IsTrue(_app.Playback.IsPlaying);
            yield return null;
            Assert.AreEqual(new ModelPose(2500, 800, 0), ConsolePose, "inspection never changes the live scene");
            _panel.Press("InspectLive");
            var (lx, lz) = ModelSpaceMapping.ToLocal(_app.Session.Current.Room, 2500, 800);
            Assert.That(Vector3.Distance(Console.transform.localPosition, new Vector3(lx, 0, lz)), Is.LessThan(1e-5f));
            _panel.Press("CompareBack");
            Assert.AreEqual(AppMode.Editing, _app.Mode);

            _panel.Press("ZoomIn"); // presentation setting: step 2 -> 3
            Assert.AreEqual(3, _tabletop.ZoomStep);
            _panel.Press("Save");
            StringAssert.Contains("Saved", _app.Status);
            var projectId = _app.Session.Current.ProjectId;
            var liveHash = Tryscape.Core.Canonical.SceneCanonicalizer.SceneHash(_app.Session.Current);
            var runA = _app.Attempts.A.Run.Result.RunId;
            var runB = _app.Attempts.B.Run.Result.RunId;
            var bHash = _app.Attempts.B.Run.Evidence.EvidenceHash();

            // "Restart": the whole app is destroyed and rebuilt over the same storage folder.
            UnityEngine.Object.Destroy(_root);
            yield return null;
            yield return BuildApp();
            Assert.IsNull(_app.Session);
            Assert.AreEqual(2, _tabletop.ZoomStep, "a new app starts at the default zoom");
            _panel.Press("OpenSaved");
            Assert.AreEqual(AppMode.Projects, _app.Mode);
            StringAssert.Contains("Moving Day 6.0 x 5.0 m", _app.ProjectsText);
            StringAssert.Contains("2 saved attempts", _app.ProjectsText);
            _panel.Press("ProjectOpen");
            Assert.AreEqual(AppMode.Editing, _app.Mode, _app.Status);
            Assert.AreEqual(projectId, _app.Session.Current.ProjectId);
            Assert.AreEqual(1, _app.Session.Current.Revision);
            Assert.AreEqual(liveHash, Tryscape.Core.Canonical.SceneCanonicalizer.SceneHash(_app.Session.Current));
            Assert.AreEqual(new ModelPose(2500, 800, 0), ConsolePose);
            Assert.AreEqual(3, _tabletop.ZoomStep, "saved presentation setting restored");
            Assert.AreEqual(runA, _app.Attempts.A.Run.Result.RunId);
            Assert.AreEqual(runB, _app.Attempts.B.Run.Result.RunId);
            Assert.AreEqual(aHash, _app.Attempts.A.Run.Evidence.EvidenceHash());
            Assert.AreEqual(bHash, _app.Attempts.B.Run.Evidence.EvidenceHash());
            yield return null;
            _panel.Press("Compare");
            StringAssert.Contains("Preparation: Before delivery, move the console table", _app.ComparisonText);
            _panel.Press("InspectA");
            Assert.That(Vector3.Distance(Console.transform.localPosition, new Vector3(ax, 0, az)), Is.LessThan(1e-5f));
            _panel.Press("CompareBack");
            _panel.Press("Reset"); // the saved creation baseline is still the reset target
            Assert.AreEqual(new ModelPose(2500, 2500, 0), ConsolePose);
            Assert.AreEqual(2, _app.Session.Current.Revision, "reset is a new revision");

            _panel.Press("Menu");
            _panel.Press("OpenSaved");
            _panel.Press("ProjectDelete");
            Assert.AreEqual("Confirm delete", _panel.LabelOf("ProjectDelete"));
            Assert.AreEqual(1, _app.SavedProjects.Count, "the first press only arms the delete");
            _panel.Press("ProjectDelete");
            Assert.AreEqual(0, _app.SavedProjects.Count);
            Assert.IsFalse(System.IO.Directory.Exists(System.IO.Path.Combine(_storeRoot, "projects", projectId.ToString("D"))));
        }

        [UnityTest]
        public IEnumerator FailedOpenLeavesTheCurrentProjectUntouched()
        {
            yield return CreateExample();
            _panel.Press("Rehearse");
            _panel.Press("KeepA");
            _panel.Press("Save");
            var saved = System.IO.Path.Combine(_storeRoot, "projects", _app.Session.Current.ProjectId.ToString("D"), "project.json");
            System.IO.File.WriteAllText(saved, "{\"schema_version\":\"1.0.0\"}"); // damaged; there is no backup yet
            yield return CreateExample(app => _panel.Press("WidthPlus")); // a different, unsaved current project
            _panel.Press("Rehearse");
            _panel.Press("KeepA");
            var current = _app.Session.Current;
            Assert.IsNotNull(_app.Attempts.A);
            var attempts = _app.Attempts.A;
            _panel.Press("Menu");
            _panel.Press("OpenSaved");
            _panel.Press("ProjectOpen");
            StringAssert.Contains("damaged", _app.Status);
            Assert.AreSame(current, _app.Session.Current, "the current project is preserved");
            Assert.AreSame(attempts, _app.Attempts.A);
            Assert.IsTrue(System.IO.File.Exists(saved + ".corrupt"), "damaged bytes preserved for diagnosis");
        }

        IEnumerator CreateExample(Action<TryscapeApp> adjust = null)
        {
            _panel.Press("CreateWithHands");
            Assert.AreEqual(AppMode.CreatingWithHands, _app.Mode);
            adjust?.Invoke(_app);
            _panel.Press("ConfirmCreate");
            Assert.AreEqual(AppMode.Editing, _app.Mode, _app.Status);
            yield return null; // instantiated prefabs run Awake/Start (Grabbable initialises transformers)
            yield return null;
        }

        EntityView Console => _app.Presenter.Views.Values.Single(v => v.Editable);
        ModelPose ConsolePose => _app.Session.Current.FindEntity(Console.EntityId).Pose;

        /// <summary>World position of a model point on the tabletop (what a hand would reach for).</summary>
        Vector3 World(int xMm, int zMm)
        {
            var (x, z) = ModelSpaceMapping.ToLocal(_app.Session.Current.Room, xMm, zMm);
            return _app.Presenter.transform.TransformPoint(new Vector3(x, 0f, z));
        }

        IEnumerator DragConsole(int handId, Vector3 fromWorld, Vector3 toWorld, PointerEventType end = PointerEventType.Unselect)
        {
            var grabbable = Console.GetComponent<Grabbable>();
            Assert.IsNotNull(grabbable, "console prefab must carry an Interaction SDK Grabbable");
            var rotation = Quaternion.identity;
            grabbable.ProcessPointerEvent(new PointerEvent(handId, PointerEventType.Hover, new Pose(fromWorld, rotation)));
            grabbable.ProcessPointerEvent(new PointerEvent(handId, PointerEventType.Select, new Pose(fromWorld, rotation)));
            yield return null;
            for (var i = 1; i <= 5; i++)
            {
                var p = Vector3.Lerp(fromWorld, toWorld, i / 5f);
                grabbable.ProcessPointerEvent(new PointerEvent(handId, PointerEventType.Move, new Pose(p, rotation)));
                yield return null;
            }
            grabbable.ProcessPointerEvent(new PointerEvent(handId, end, new Pose(toWorld, rotation)));
            yield return null;
        }

        [UnityTest]
        public IEnumerator HandsOnlyCreationUsesTheReviewedDimensions()
        {
            yield return CreateExample(app =>
            {
                _panel.Press("WidthPlus");     // 6.0 -> 6.5 m
                _panel.Press("WidthPlusFine"); // 6.6 m
                _panel.Press("DepthPlus");     // 5.0 -> 5.5 m
            });
            Assert.AreEqual(6600, _app.Session.Current.Room.WidthMm);
            Assert.AreEqual(5500, _app.Session.Current.Room.DepthMm);
            Assert.AreEqual(Provenance.Illustrative, _app.Session.Current.Provenance);
            Assert.AreEqual(2, _app.Presenter.Views.Count);
            Assert.AreEqual(0, _app.Session.Current.Revision);
            // The rendered console sits at the model pose (2500, 2500) mapped into the 6.6 x 5.5 m room.
            var (x, z) = ModelSpaceMapping.ToLocal(_app.Session.Current.Room, 2500, 2500);
            Assert.That(Vector3.Distance(Console.transform.localPosition, new Vector3(x, 0, z)), Is.LessThan(1e-5f));
        }

        [UnityTest]
        public IEnumerator RoomDimensionsAreClampedToTheSupportedRange()
        {
            _panel.Press("CreateWithHands");
            for (var i = 0; i < 10; i++)
                _panel.Press("WidthPlus");
            Assert.AreEqual(8000, _app.Draft.RoomWidthMm);
            Assert.IsTrue(_panel.IsInteractable("WidthMinus"), "the - control stays enabled");
            Assert.IsFalse(_panel.IsInteractable("WidthPlus"), "the + control must be disabled at the limit");
            yield return null;
        }

        [UnityTest]
        public IEnumerator GrabAndReleaseOnClearFloorCommitsOneSnappedMove()
        {
            yield return CreateExample();
            yield return DragConsole(RightHand, World(2500, 2500), World(2512, 790));
            Assert.AreEqual(1, _app.Session.Current.Revision, _app.Status);
            Assert.AreEqual(new ModelPose(2500, 800, 0), ConsolePose);
            var (x, z) = ModelSpaceMapping.ToLocal(_app.Session.Current.Room, 2500, 800);
            Assert.That(Vector3.Distance(Console.transform.localPosition, new Vector3(x, 0, z)), Is.LessThan(1e-5f),
                "view must snap to the committed pose");
            Assert.IsFalse(_app.Ghost.gameObject.activeSelf, "preview hidden after release");
        }

        [UnityTest]
        public IEnumerator ReleaseOverTheSofaIsRejectedAndTheConsoleReturns()
        {
            yield return CreateExample();
            var before = _app.Session.Current;
            yield return DragConsole(RightHand, World(2500, 2500), World(1500, 2500));
            Assert.AreSame(before, _app.Session.Current, "rejected placement must not change state");
            Assert.AreEqual(new ModelPose(2500, 2500, 0), ConsolePose);
            StringAssert.Contains("Not placed", _app.Status);
            var (x, z) = ModelSpaceMapping.ToLocal(_app.Session.Current.Room, 2500, 2500);
            Assert.That(Vector3.Distance(Console.transform.localPosition, new Vector3(x, 0, z)), Is.LessThan(1e-5f));
        }

        [UnityTest]
        public IEnumerator InvalidPreviewIsShownWithTextNotJustColour()
        {
            yield return CreateExample();
            var grabbable = Console.GetComponent<Grabbable>();
            var from = World(2500, 2500);
            grabbable.ProcessPointerEvent(new PointerEvent(RightHand, PointerEventType.Hover, new Pose(from, Quaternion.identity)));
            grabbable.ProcessPointerEvent(new PointerEvent(RightHand, PointerEventType.Select, new Pose(from, Quaternion.identity)));
            yield return null;
            grabbable.ProcessPointerEvent(new PointerEvent(RightHand, PointerEventType.Move, new Pose(World(1500, 2500), Quaternion.identity)));
            yield return null;
            Assert.AreEqual(DragState.Dragging, _app.Drag.State, "drag must have started");
            Assert.IsTrue(_app.Ghost.gameObject.activeSelf);
            Assert.IsTrue(_app.Ghost.CurrentlyInvalid);
            StringAssert.Contains("sofa", _app.Ghost.CurrentText);
            grabbable.ProcessPointerEvent(new PointerEvent(RightHand, PointerEventType.Cancel, new Pose(from, Quaternion.identity)));
            yield return null;
        }

        [UnityTest]
        public IEnumerator InteractorCancelNeverCommits()
        {
            yield return CreateExample();
            yield return DragConsole(RightHand, World(2500, 2500), World(2500, 800), PointerEventType.Cancel);
            Assert.AreEqual(0, _app.Session.Current.Revision);
            Assert.AreEqual(new ModelPose(2500, 2500, 0), ConsolePose);
        }

        [UnityTest]
        public IEnumerator SecondHandCannotCommitAnotherHandsDrag()
        {
            yield return CreateExample();
            var grabbable = Console.GetComponent<Grabbable>();
            var from = World(2500, 2500);
            grabbable.ProcessPointerEvent(new PointerEvent(RightHand, PointerEventType.Hover, new Pose(from, Quaternion.identity)));
            grabbable.ProcessPointerEvent(new PointerEvent(RightHand, PointerEventType.Select, new Pose(from, Quaternion.identity)));
            yield return null;
            Assert.AreEqual(DragState.Dragging, _app.Drag.State, "right hand must own an active drag");
            grabbable.ProcessPointerEvent(new PointerEvent(LeftHand, PointerEventType.Hover, new Pose(from, Quaternion.identity)));
            grabbable.ProcessPointerEvent(new PointerEvent(LeftHand, PointerEventType.Select, new Pose(from, Quaternion.identity)));
            grabbable.ProcessPointerEvent(new PointerEvent(LeftHand, PointerEventType.Unselect, new Pose(World(2500, 800), Quaternion.identity)));
            yield return null;
            Assert.AreEqual(0, _app.Session.Current.Revision, "the non-owning hand must not commit");
            grabbable.ProcessPointerEvent(new PointerEvent(RightHand, PointerEventType.Cancel, new Pose(from, Quaternion.identity)));
            yield return null;
        }

        [UnityTest]
        public IEnumerator FocusLossDuringDragCancelsWithoutCommit()
        {
            yield return CreateExample();
            var grabbable = Console.GetComponent<Grabbable>();
            var from = World(2500, 2500);
            grabbable.ProcessPointerEvent(new PointerEvent(RightHand, PointerEventType.Hover, new Pose(from, Quaternion.identity)));
            grabbable.ProcessPointerEvent(new PointerEvent(RightHand, PointerEventType.Select, new Pose(from, Quaternion.identity)));
            grabbable.ProcessPointerEvent(new PointerEvent(RightHand, PointerEventType.Move, new Pose(World(2500, 800), Quaternion.identity)));
            yield return null;
            Assert.AreEqual(DragState.Dragging, _app.Drag.State, "a drag must be active before focus loss");
            _app.SendMessage("OnApplicationFocus", false);
            Assert.AreEqual(DragState.Idle, _app.Drag.State);
            grabbable.ProcessPointerEvent(new PointerEvent(RightHand, PointerEventType.Unselect, new Pose(World(2500, 800), Quaternion.identity)));
            yield return null;
            Assert.AreEqual(0, _app.Session.Current.Revision, "a release after focus loss must not commit");
        }

        [UnityTest]
        public IEnumerator UndoRedoResetThroughThePanelUseNewRevisions()
        {
            yield return CreateExample();
            yield return DragConsole(RightHand, World(2500, 2500), World(2500, 800));  // rev 1
            _panel.Press("Rotate");                                                    // rev 2: yaw 90
            Assert.AreEqual(90, ConsolePose.YawDeg);
            _panel.Press("ToggleRoute");                                               // rev 3: northern
            Assert.AreEqual(MovingDayPack.NorthRouteId, _app.Session.Current.ActiveRouteId);
            _panel.Press("Undo");                                                      // rev 4: direct
            Assert.AreEqual(MovingDayPack.DirectRouteId, _app.Session.Current.ActiveRouteId);
            _panel.Press("Undo");                                                      // rev 5: yaw 0
            Assert.AreEqual(new ModelPose(2500, 800, 0), ConsolePose);
            Assert.IsTrue(_panel.IsInteractable("Undo"));
            Assert.IsTrue(_panel.IsInteractable("Redo"));
            _panel.Press("Redo");                                                      // rev 6: yaw 90
            Assert.AreEqual(90, ConsolePose.YawDeg);
            _panel.Press("Reset");                                                     // rev 7: confirmed scene
            Assert.AreEqual(7, _app.Session.Current.Revision);
            Assert.IsTrue(_app.Session.Current.ContentEquals(_app.Session.Confirmed));
            Assert.AreEqual(new ModelPose(2500, 2500, 0), ConsolePose);
            Assert.IsFalse(_panel.IsInteractable("Undo"));
            yield return null;
            var (x, z) = ModelSpaceMapping.ToLocal(_app.Session.Current.Room, 2500, 2500);
            Assert.That(Vector3.Distance(Console.transform.localPosition, new Vector3(x, 0, z)), Is.LessThan(1e-5f));
        }

        [UnityTest]
        public IEnumerator TabletopRescaleAndRecentreDoNotChangeTheModel()
        {
            yield return CreateExample();
            var before = _app.Session.Current;
            _panel.Press("ZoomIn");
            _panel.Press("ZoomIn");
            _tabletop.transform.position += new Vector3(0.3f, 0.1f, -0.2f);
            yield return null;
            Assert.AreSame(before, _app.Session.Current, "presentation changes must not touch the scene");
            Assert.AreEqual(0.15f, _tabletop.CurrentScale, 1e-6f);

            // A drag to the same model point at a different scale commits the same model pose.
            yield return DragConsole(RightHand, World(2500, 2500), World(2500, 800));
            Assert.AreEqual(new ModelPose(2500, 800, 0), ConsolePose);
            _panel.Press("ZoomOut");
            _panel.Press("ZoomOut");
            _panel.Press("ZoomOut");
            Assert.AreEqual(new ModelPose(2500, 800, 0), ConsolePose);
            Assert.AreEqual(1, _app.Session.Current.Revision);
        }

        [UnityTest]
        public IEnumerator UnavailableFeaturesSayWhyInsteadOfPretending()
        {
            // Voice creation now exists, but its entry point only shows the processing notice: no recording,
            // microphone access or scene before the user explicitly allows it.
            _panel.Press("SpeakSituation");
            Assert.AreEqual(AppMode.RecordingVoice, _app.Mode);
            Assert.IsFalse(_app.AudioConsentGiven);
            Assert.AreEqual(Tryscape.Application.Creation.RecorderState.Idle, _app.Recorder.State);
            StringAssert.Contains("sent to the Tryscape creation service", _app.Status);
            Assert.IsNull(_app.Session, "voice entry must not create a scene");
            _panel.Press("Menu");
            // "Open saved" now lists local projects; this test uses an empty temporary store, never the real data folder.
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tryscape-empty-" + Guid.NewGuid().ToString("N"));
            _app.UseProjectStore(new Tryscape.Infrastructure.Persistence.ProjectStore(root, MovingDayPack.CreateCatalog()));
            _panel.Press("OpenSaved");
            Assert.AreEqual(AppMode.Projects, _app.Mode);
            StringAssert.Contains("No saved projects", _app.Status);
            Assert.IsNull(_app.Session);
            Assert.IsFalse(System.IO.Directory.Exists(root), "listing an empty store writes nothing");
            yield return null;
        }

        [UnityTest]
        public IEnumerator RecreatingReplacesTheSceneOnlyAfterConfirmation()
        {
            yield return CreateExample();
            var first = _app.Session;
            var firstScene = first.Current.SceneId;
            _panel.Press("Menu");
            _panel.Press("CreateWithHands");
            _panel.Press("BackToWelcome");
            Assert.AreSame(first, _app.Session, "backing out of creation keeps the existing scene");
            _panel.Press("ResumeEditing");
            Assert.AreEqual(AppMode.Editing, _app.Mode);
            yield return CreateExample(app => _panel.Press("DepthPlus"));
            Assert.AreNotEqual(firstScene, _app.Session.Current.SceneId);
            Assert.AreEqual(5500, _app.Session.Current.Room.DepthMm);
            Assert.AreEqual(1, _app.Presenter.RoomRoot.parent.Cast<Transform>().Count(t => t.name == "Room"),
                "the previous room is disposed after the new one commits");
        }
        // ---------------------------------------------------------------- Phase 2: rehearse, replay, explain

        EntityView Sofa => _app.Presenter.Views.Values.Single(v => v.Role == EntityRole.Payload);

        void AssertSofaAt(int xMm, int zMm, string message)
        {
            var (x, z) = ModelSpaceMapping.ToLocal(_app.Session.Current.Room, xMm, zMm);
            var p = Sofa.transform.localPosition;
            Assert.That(Vector2.Distance(new Vector2(p.x, p.z), new Vector2(x, z)), Is.LessThan(1e-4f), $"{message}: sofa at {p}");
        }

        [UnityTest]
        public IEnumerator RehearseExplainsTheObstructionAndAHandMoveClearsIt()
        {
            yield return CreateExample();
            _panel.Press("Rehearse");
            var first = _app.LatestRun;
            Assert.IsNotNull(first, _app.Status);
            Assert.AreEqual(EvaluationOutcome.BlockedInModel, first.Evidence.Outcome);
            Assert.AreEqual(Console.EntityId, first.Evidence.FirstViolation.EntityIds.Single());
            Assert.IsTrue(_app.Playback.IsPlaying, "playback starts after the result is fixed");
            StringAssert.Contains("Blocked in this model", _app.ResultText);
            StringAssert.Contains("console table obstructs", _app.ResultText);
            StringAssert.Contains("Height, turning and lifting were not assessed", _app.ResultText);

            _app.Playback.Seek(1000);
            Assert.IsTrue(_app.Playback.IsFinished);
            AssertSofaAt(1275, 2500, "blocked playback stops at the modelled contact centre, not inside the console");
            Assert.IsTrue(_app.Highlight.IsShown);
            Assert.AreEqual(Console.EntityId, _app.Highlight.ShownBlocker);
            Assert.AreEqual((Rational.FromInteger(1275), Rational.FromInteger(2500)), _app.Highlight.ShownContact);

            yield return DragConsole(RightHand, World(2500, 2500), World(2512, 790));
            Assert.AreEqual(new ModelPose(2500, 800, 0), ConsolePose, _app.Status);
            Assert.IsTrue(_app.IsResultStale, "the edit invalidates the live result");
            StringAssert.Contains("OUT OF DATE", _app.ResultText);
            Assert.IsFalse(_app.Highlight.IsShown, "stale contact highlight is removed");
            AssertSofaAt(1000, 2500, "the payload returns to its committed start after an edit");
            _panel.Press("PlayPause");
            Assert.IsFalse(_app.Playback.HasTimeline, "a stale result cannot be replayed as if current");

            _panel.Press("Rehearse");
            var cleared = _app.LatestRun;
            Assert.AreEqual(EvaluationOutcome.ClearInModel, cleared.Evidence.Outcome);
            Assert.AreEqual(1, cleared.Evidence.SceneRevision);
            StringAssert.Contains("Clear in this model", _app.ResultText);
            _app.Playback.Seek(1000);
            AssertSofaAt(4000, 3800, "clear playback reaches the destination");
            Assert.IsFalse(_app.Highlight.IsShown);

            _panel.Press("Undo");
            _panel.Press("Rehearse");
            Assert.AreEqual(first.Evidence.FirstViolation, _app.LatestRun.Evidence.FirstViolation, "undo restores the original constraint outcome");
            Assert.AreEqual(EvaluationOutcome.BlockedInModel, first.Evidence.Outcome, "earlier runs stay immutable");
        }

        [UnityTest]
        public IEnumerator MovingTheConsoleNorthBlocksTheNorthernRoute()
        {
            yield return CreateExample();
            yield return DragConsole(RightHand, World(2500, 2500), World(2500, 4000));
            Assert.AreEqual(new ModelPose(2500, 4000, 0), ConsolePose, _app.Status);
            _panel.Press("ToggleRoute");
            Assert.AreEqual(MovingDayPack.NorthRouteId, _app.Session.Current.ActiveRouteId);
            _panel.Press("Rehearse");
            var v = _app.LatestRun.Evidence.FirstViolation;
            Assert.IsNotNull(v, "F04: the moved console blocks the Northern approach");
            Assert.AreEqual(1, v.SegmentIndex);
            Assert.AreEqual(Console.EntityId, v.EntityIds.Single());
            _panel.Press("ToggleRoute");
            _panel.Press("Rehearse");
            Assert.AreEqual(EvaluationOutcome.ClearInModel, _app.LatestRun.Evidence.Outcome, "Direct stays clear");
        }

        [UnityTest]
        public IEnumerator TabletopZoomAndRecentreDoNotChangeEvaluation()
        {
            yield return CreateExample();
            _panel.Press("Rehearse");
            var before = _app.LatestRun.Evidence;
            _panel.Press("ZoomIn");
            _panel.Press("ZoomIn");
            _tabletop.transform.position += new Vector3(-0.25f, 0.05f, 0.3f);
            _panel.Press("Recenter");
            _panel.Press("ZoomOut");
            yield return null;
            Assert.IsFalse(_app.IsResultStale, "presentation changes do not create a revision");
            _panel.Press("Rehearse");
            CollectionAssert.AreEqual(before.CanonicalBytes(), _app.LatestRun.Evidence.CanonicalBytes(),
                "identical deterministic evidence after zoom/recentre");
            _app.Playback.Seek(1000);
            AssertSofaAt(1275, 2500, "stop position is in model space whatever the tabletop scale");
        }

        [UnityTest]
        public IEnumerator RealFramePlaybackStopsAtTheContactAndCanBePausedAndReplayed()
        {
            yield return CreateExample();
            _panel.Press("Rehearse");
            Assert.AreEqual(1.1, _app.Playback.DurationSeconds, 1e-9, "22 ticks at 20 Hz");
            yield return null;
            _panel.Press("PlayPause"); // pause
            Assert.IsFalse(_app.Playback.IsPlaying);
            var pausedAt = _app.Playback.ElapsedSeconds;
            yield return null;
            yield return null;
            Assert.AreEqual(pausedAt, _app.Playback.ElapsedSeconds, "paused playback does not advance");
            _panel.Press("PlayPause"); // resume
            var deadline = Time.realtimeSinceStartup + 10f;
            while (!_app.Playback.IsFinished && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.IsTrue(_app.Playback.IsFinished, "playback finished through real frame updates");
            AssertSofaAt(1275, 2500, "frame-driven playback stops at the same exact contact centre");
            Assert.IsTrue(_app.Highlight.IsShown);

            _panel.Press("PlayPause"); // replay from start
            Assert.IsTrue(_app.Playback.IsPlaying);
            Assert.IsFalse(_app.Highlight.IsShown, "highlight waits for the replay to reach the contact");
        }
    }
}
