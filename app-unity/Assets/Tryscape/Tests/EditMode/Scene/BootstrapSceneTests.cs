using System.Linq;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using Tryscape.Unity;
using Tryscape.Unity.MixedReality;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Tryscape.Tests.EditMode.Scene
{
    /// <summary>
    /// Inspects the generated bootstrap scene (SceneBuilder output) for the passthrough, MRUK and gaze wiring that
    /// cannot run without a headset (S11, AC33-AC35). Checks structure only; device behaviour stays a Quest gate.
    /// </summary>
    public sealed class BootstrapSceneTests
    {
        const string ScenePath = "Assets/Tryscape/Scenes/Bootstrap.unity";
        const string ConsolePrefab = "Assets/Tryscape/Content/MovingDay/Prefabs/console_standard_greybox.prefab";

        [OneTimeSetUp]
        public void Open() => EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        static T[] All<T>() where T : Object => Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        static Object Field(Object component, string name) => new SerializedObject(component).FindProperty(name)?.objectReferenceValue;

        [Test]
        public void PassthroughIsOnWithAnUnderlayATransparentCameraAndAHiddenVirtualRoomSwitch()
        {
            var manager = All<OVRManager>().Single();
            Assert.IsTrue(manager.isInsightPassthroughEnabled);
            Assert.AreEqual(1, All<OVRPassthroughLayer>().Length);
            var mr = All<MixedRealityController>().Single();
            var camera = (Camera)Field(mr, "_camera");
            Assert.IsNotNull(camera);
            Assert.AreEqual(CameraClearFlags.SolidColor, camera.clearFlags);
            Assert.AreEqual(0f, camera.backgroundColor.a);
            Assert.IsNotNull(Field(mr, "_passthroughLayer"));
            Assert.IsNotNull(Field(mr, "_tabletop"));
            var virtualRoom = (GameObject)Field(mr, "_virtualEnvironment");
            Assert.IsNotNull(virtualRoom);
            Assert.IsNotNull(virtualRoom.transform.Find("Floor"), "the virtual floor is switched with passthrough");
            var app = All<TryscapeApp>().Single();
            Assert.AreSame(mr, Field(app, "_mixedReality"));
        }

        [Test]
        public void NoPermissionIsRequestedAtStartupAndRoomDataLoadsOnDemand()
        {
            var manager = new SerializedObject(All<OVRManager>().Single());
            foreach (var field in new[]
                     {
                         "requestScenePermissionOnStartup", "requestEyeTrackingPermissionOnStartup",
                         "requestRecordAudioPermissionOnStartup", "requestPassthroughCameraAccessPermissionOnStartup",
                     })
                Assert.IsFalse(manager.FindProperty(field).boolValue, field);
            var mruk = All<MRUK>().Single();
            Assert.IsFalse(mruk.SceneSettings.LoadSceneOnStartup, "the app asks for room data itself, with a fallback");
            Assert.AreEqual(MRUK.SceneDataSource.Device, mruk.SceneSettings.DataSource);
        }

        [Test]
        public void GazeIsWiredForThePanelAndTheConsoleWithRayFallback()
        {
            var eyeGaze = All<EyeGaze>().Single(g => g.GetType() == typeof(EyeGaze));
            Assert.AreSame(eyeGaze, Field(All<GazeController>().Single(), "_eyeGaze"));
            Assert.That(All<GazeInteractor>().Length, Is.GreaterThanOrEqualTo(2), "one gaze interactor per hand");
            Assert.That(All<GazeInteractable>().Length, Is.GreaterThanOrEqualTo(1), "panel canvas is gaze-selectable");
            Assert.That(All<ActiveStateTagSetFilter>().Length, Is.GreaterThanOrEqualTo(1), "hand rays fall back when gaze is not tracked");

            var console = AssetDatabase.LoadAssetAtPath<GameObject>(ConsolePrefab);
            Assert.IsNotNull(console);
            Assert.IsNotNull(console.GetComponentInChildren<GazeInteractable>(true), "console is gaze-grabbable");
            Assert.IsNotNull(console.GetComponentInChildren<RayInteractable>(true), "console keeps its ray grab");
        }

        [Test]
        public void PanelOffersVoiceHandsAndTheMixedRealitySettings()
        {
            var names = All<Button>().Select(b => b.name).ToList();
            foreach (var id in new[]
                     {
                         "SpeakSituation", "CreateWithHands", "Settings", "TogglePassthrough", "PlaceOnSurface", "SetUpSpace",
                         "PlaceFloating", "ToggleGaze",
                     })
                Assert.Contains(id, names);
        }
    }
}
