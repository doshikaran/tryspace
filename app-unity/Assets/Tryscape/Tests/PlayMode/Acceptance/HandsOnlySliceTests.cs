using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using Oculus.Interaction;
using Tryscape.Application.Editing;
using Tryscape.Content.MovingDay;
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
        };

        GameObject _root;
        TryscapeApp _app;
        PanelController _panel;
        TabletopController _tabletop;

        [UnitySetUp]
        public IEnumerator SetUp()
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
                return go.GetComponent<UnityEngine.UI.Button>();
            }).ToArray();
            _panel.Configure(null, null, null, null, null, null, buttons);

            var appGo = new GameObject("App");
            appGo.transform.SetParent(_root.transform, false);
            appGo.SetActive(false);
            _app = appGo.AddComponent<TryscapeApp>();
            _app.Configure(presenter, ghost, _panel, _tabletop);
            appGo.SetActive(true);
            yield return null; // Start: panel binds to the app
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            UnityEngine.Object.Destroy(_root);
            yield return null;
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
            _panel.Press("SpeakSituation");
            Assert.AreEqual(AppMode.Welcome, _app.Mode);
            Assert.IsNull(_app.Session, "voice entry must not create a scene in this build");
            _panel.Press("OpenSaved");
            Assert.IsNull(_app.Session);
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
    }
}
