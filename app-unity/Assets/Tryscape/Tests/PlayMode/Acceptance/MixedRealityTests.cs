using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Tryscape.Application.Placement;
using Tryscape.Content.MovingDay;
using Tryscape.Core.Canonical;
using Tryscape.Unity;
using Tryscape.Unity.Experience;
using Tryscape.Unity.MixedReality;
using Tryscape.Unity.Presentation;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tryscape.Tests.PlayMode.Acceptance
{
    /// <summary>
    /// Passthrough and real-surface placement through the real panel (AC33/AC34 wiring). Room data comes from a fixed
    /// fake source: this proves the ranking, fallback, stale-scan and presentation-only behaviour, not MRUK on a device.
    /// The head is a camera at (0, 1.2, 0) looking along +z, with the floor at y = 0.
    /// </summary>
    [Category("Experience")]
    public sealed class MixedRealityTests
    {
        const string RegistryPath = "Assets/Tryscape/Content/MovingDay/MovingDayPrefabRegistry.asset";
        const string PassthroughOffKey = "tryscape.passthrough.off";

        static readonly string[] PanelIds =
        {
            "CreateWithHands", "ConfirmCreate", "Rehearse", "Settings", "SettingsBack", "RotateViewRight", "ResetView",
            "TogglePassthrough", "PlaceOnSurface", "SetUpSpace", "PlaceFloating", "ToggleGaze", "PauseAll", "ReloadSaved",
        };

        sealed class FakeRoom : ISurfaceSource
        {
            public readonly Queue<TaskCompletionSource<SurfaceScan>> Pending = new Queue<TaskCompletionSource<SurfaceScan>>();
            public SurfaceScan Next;
            public bool Hold;
            public int Calls;
            public bool LastAllowSpaceSetup;

            public Task<SurfaceScan> ScanAsync(bool allowSpaceSetup)
            {
                Calls++;
                LastAllowSpaceSetup = allowSpaceSetup;
                if (!Hold)
                    return Task.FromResult(Next);
                var tcs = new TaskCompletionSource<SurfaceScan>();
                Pending.Enqueue(tcs);
                return tcs.Task;
            }
        }

        static SurfaceCandidate Surface(string id, string label, float x, float y, float z) =>
            new SurfaceCandidate(id, label, x, y, z, 1.0, 1.0, 0.6);

        static SurfaceScan Room(params SurfaceCandidate[] candidates) => new SurfaceScan(SurfaceScanStatus.Found, candidates, 0.0);

        GameObject _root;
        TryscapeApp _app;
        PanelController _panel;
        TabletopController _tabletop;
        MixedRealityController _mr;
        GameObject _virtualRoom;
        Camera _camera;
        FakeRoom _room;
        string _storeRoot;

        [SetUp]
        public void ClearPrefs()
        {
            PlayerPrefs.DeleteKey(PassthroughOffKey);
            _storeRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tryscape-mr-" + Guid.NewGuid().ToString("N"));
            _room = new FakeRoom
            {
                Next = Room(
                    Surface("desk", "TABLE", 0f, 0.74f, 0.7f),
                    Surface("side-table", "TABLE", 0.9f, 0.6f, 0.9f),
                    Surface("coffee-table", "TABLE", 0f, 0.35f, 0.5f)), // too low for seated reach
            };
        }

        IEnumerator BuildApp()
        {
            _root = new GameObject("MR Test Root");
            var head = new GameObject("Head", typeof(Camera));
            head.tag = "MainCamera";
            head.transform.SetParent(_root.transform, false);
            head.transform.position = new Vector3(0f, 1.2f, 0f);
            head.transform.rotation = Quaternion.identity;
            _camera = head.GetComponent<Camera>();
            _virtualRoom = new GameObject("Virtual Environment");
            _virtualRoom.transform.SetParent(_root.transform, false);

            var tabletop = new GameObject("Tabletop");
            tabletop.transform.SetParent(_root.transform, false);
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
                label.transform.SetParent(go.transform, false);
                return go.GetComponent<UnityEngine.UI.Button>();
            }).ToArray();
            _panel.Configure(null, null, null, null, null, null, buttons);

            var appGo = new GameObject("App");
            appGo.transform.SetParent(_root.transform, false);
            appGo.SetActive(false);
            _app = appGo.AddComponent<TryscapeApp>();
            _app.Configure(presenter, ghost, _panel, _tabletop);
            _mr = appGo.AddComponent<MixedRealityController>();
            _mr.Configure(null, _camera, _virtualRoom, _tabletop); // no OVR passthrough layer outside a headset
            _mr.UseSurfaceSource(_room);
            var gaze = appGo.AddComponent<GazeController>(); // no EyeGaze: gaze is reported unavailable
            _app.ConfigureMixedReality(_mr, gaze);
            appGo.SetActive(true);
            _app.UseProjectStore(new Tryscape.Infrastructure.Persistence.ProjectStore(_storeRoot, MovingDayPack.CreateCatalog()));
            yield return null; // Start: passthrough on, first scan
            yield return null; // scan continuation
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            UnityEngine.Object.Destroy(_root);
            yield return null;
            PlayerPrefs.DeleteKey(PassthroughOffKey);
            if (_storeRoot != null && System.IO.Directory.Exists(_storeRoot))
                System.IO.Directory.Delete(_storeRoot, true);
        }

        static void AssertNear(Vector3 expected, Vector3 actual, string message)
        {
            Assert.That(Vector3.Distance(expected, actual), Is.LessThan(1e-4f), $"{message}: expected {expected}, got {actual}");
        }

        [UnityTest]
        public IEnumerator StartPlacesTheTabletopOnTheNearestDeskAndPlacementNeverChangesResults()
        {
            yield return BuildApp();
            Assert.AreEqual(1, _room.Calls, "one scan at startup");
            Assert.IsFalse(_room.LastAllowSpaceSetup, "startup never opens Space Setup");
            Assert.AreEqual(PlacementMode.Surface, _mr.Placement);
            Assert.AreEqual(2, _mr.SurfaceCount, "the low coffee table is not offered");
            AssertNear(new Vector3(0f, 0.76f, 0.7f), _tabletop.transform.position, "on the desk, 2 cm above it");
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(_tabletop.transform.eulerAngles.y, 0f)), Is.LessThan(0.01f), "faces away from the user");

            _panel.Press("CreateWithHands");
            _panel.Press("ConfirmCreate");
            yield return null;
            var hash = SceneCanonicalizer.SceneHash(_app.Session.Current);
            _panel.Press("Rehearse");
            var evidence = _app.LatestRun.Evidence.EvidenceHash();

            _panel.Press("Settings");
            StringAssert.Contains("table", _app.SettingsText);
            Assert.AreEqual("Next surface", _panel.LabelOf("PlaceOnSurface"));
            _panel.Press("PlaceOnSurface");
            AssertNear(new Vector3(0.9f, 0.62f, 0.9f), _tabletop.transform.position, "moved to the side table");
            var expectedYaw = Mathf.Atan2(0.9f, 0.9f) * Mathf.Rad2Deg; // 45 degrees: from the head towards the side table
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(_tabletop.transform.eulerAngles.y, expectedYaw)), Is.LessThan(0.01f));
            _panel.Press("PlaceOnSurface");
            AssertNear(new Vector3(0f, 0.76f, 0.7f), _tabletop.transform.position, "wraps back to the desk");

            _panel.Press("RotateViewRight");
            _panel.Press("ResetView");
            AssertNear(new Vector3(0f, 0.76f, 0.7f), _tabletop.transform.position, "reset view keeps the surface");
            Assert.AreEqual(0, _tabletop.YawDeg);

            _panel.Press("SettingsBack");
            Assert.AreEqual(hash, SceneCanonicalizer.SceneHash(_app.Session.Current), "placement is presentation only");
            _panel.Press("Rehearse");
            Assert.AreEqual(evidence, _app.LatestRun.Evidence.EvidenceHash(), "same result wherever the tabletop sits");
        }

        [UnityTest]
        public IEnumerator DeclinedPermissionOrNoRoomFallsBackToFloatingWithAPlainReason()
        {
            _room.Next = SurfaceScan.Failed(SurfaceScanStatus.NoPermission);
            yield return BuildApp();
            Assert.AreEqual(PlacementMode.Manual, _mr.Placement);
            AssertNear(new Vector3(0f, 0.75f, 0.55f), _tabletop.transform.position, "floating at seated height in front");
            StringAssert.Contains("permission", _app.SettingsText);

            _room.Next = SurfaceScan.Failed(SurfaceScanStatus.NoRoom);
            _panel.Press("Settings");
            _panel.Press("PlaceOnSurface");
            yield return null;
            StringAssert.Contains("no room set up", _app.SettingsText);
            Assert.AreEqual(PlacementMode.Manual, _mr.Placement);

            _room.Next = Room(Surface("desk", "TABLE", 0f, 0.74f, 0.7f));
            _panel.Press("SetUpSpace");
            yield return null;
            Assert.IsTrue(_room.LastAllowSpaceSetup, "only the explicit button may open Space Setup");
            Assert.AreEqual(PlacementMode.Surface, _mr.Placement);

            _panel.Press("CreateWithHands");
            Assert.IsNotNull(_app, "hand journey still available");
        }

        [UnityTest]
        public IEnumerator FloatingWhileAScanIsPendingDiscardsTheLateResult()
        {
            _room.Hold = true;
            yield return BuildApp();
            Assert.IsTrue(_mr.Scanning);
            _panel.Press("Settings");
            Assert.IsFalse(_panel.IsInteractable("PlaceOnSurface"), "no second scan while one is running");
            _panel.Press("PlaceFloating");
            var floating = _tabletop.transform.position;
            _room.Pending.Dequeue().SetResult(Room(Surface("desk", "TABLE", 0f, 0.74f, 0.7f)));
            yield return null;
            yield return null;
            Assert.AreEqual(PlacementMode.Manual, _mr.Placement, "late scan ignored");
            AssertNear(floating, _tabletop.transform.position, "tabletop did not jump to the desk");
        }

        [UnityTest]
        public IEnumerator PassthroughIsTheDefaultAndTheVirtualRoomIsItsRememberedAlternative()
        {
            yield return BuildApp();
            Assert.IsTrue(_mr.PassthroughOn, "passthrough by default (AC33)");
            Assert.IsFalse(_virtualRoom.activeSelf, "virtual floor hidden so the real room shows");
            Assert.AreEqual(0f, _camera.backgroundColor.a, "camera clears to transparent");

            _panel.Press("Settings");
            Assert.AreEqual("Passthrough off", _panel.LabelOf("TogglePassthrough"));
            _panel.Press("TogglePassthrough");
            Assert.IsFalse(_mr.PassthroughOn);
            Assert.IsTrue(_virtualRoom.activeSelf);
            Assert.AreEqual(1f, _camera.backgroundColor.a, "opaque virtual background");
            Assert.AreEqual("Passthrough on", _panel.LabelOf("TogglePassthrough"));

            // Restart: the choice is remembered on this device.
            UnityEngine.Object.Destroy(_root);
            yield return null;
            yield return BuildApp();
            Assert.IsFalse(_mr.PassthroughOn, "preference survives a restart");
            _panel.Press("Settings");
            _panel.Press("TogglePassthrough");
            Assert.IsTrue(_mr.PassthroughOn);
            Assert.IsFalse(_virtualRoom.activeSelf);
        }

        /// <summary>The panel is placed from the real head pose (eye-level origin: head at y = 1.2 here), not fixed world coordinates.</summary>
        [UnityTest]
        public IEnumerator PanelIsPlacedInFrontOfTheHeadAfterStartAndOnResetView()
        {
            yield return BuildApp();
            for (var i = 0; i < 12; i++)
                yield return null;
            var panel = _panel.transform;
            AssertNear(new Vector3(-0.32f, 1.2f - 0.12f, 0.55f), panel.position, "front-left, just below eye level");
            var toPanel = Vector3.ProjectOnPlane(panel.position - _camera.transform.position, Vector3.up).normalized;
            Assert.That(Vector3.Dot(panel.forward, toPanel), Is.GreaterThan(0.999f), "turned to face the user");
            Assert.That(Mathf.Abs(panel.forward.y), Is.LessThan(1e-4f), "kept upright, not tilted");

            _camera.transform.SetPositionAndRotation(new Vector3(2f, 0.1f, -1f), Quaternion.Euler(0f, 180f, 0f)); // turned around, lower
            _panel.Press("Settings");
            _panel.Press("ResetView");
            AssertNear(new Vector3(2f + 0.32f, 0.1f - 0.12f, -1f - 0.55f), panel.position, "follows the head on reset");
        }

        [UnityTest]
        public IEnumerator GazeWithoutAnEyeGazeSourceIsReportedHonestlyAndHidden()
        {
            yield return BuildApp();
            _panel.Press("Settings");
            Assert.IsFalse(_app.Gaze.Wired);
            Assert.IsFalse(_panel.IsInteractable("ToggleGaze"), "no gaze toggle when there is no gaze source");
            StringAssert.Contains("Gaze: not available", _app.SettingsText);
        }
    }
}
