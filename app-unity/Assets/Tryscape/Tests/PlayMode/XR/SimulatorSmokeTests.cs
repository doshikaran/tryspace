using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace Tryscape.Tests.PlayMode.XR
{
    /// <summary>
    /// M0 toolchain gate: the Interaction SDK hand rig runs against the Meta XR Simulator OpenXR runtime.
    /// Only meaningful with the simulator runtime configured (scripts/run_xrsim_smoke.sh sets the environment);
    /// otherwise the tests are reported as Ignored, never as passed.
    ///
    /// What this proves: OpenXR session on the simulator runtime, simulated hand data reaching Interaction SDK
    /// hands, and enabled hand interactors in the rig. What it does not prove: a person completing a grab or a
    /// poke with simulated hands (manual check) or real-device hand tracking quality.
    /// </summary>
    [Category("XRSimulator")]
    public sealed class SimulatorSmokeTests
    {
        const string BootstrapScene = "Assets/Tryscape/Scenes/Bootstrap.unity";

        const float XrStartTimeoutSeconds = 45f;
        const float HandTimeoutSeconds = 30f;

        [UnitySetUp]
        public IEnumerator RequireSimulator()
        {
            var runtimeJson = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON") ?? "";
            if (!runtimeJson.EndsWith("meta_openxr_simulator.json", StringComparison.Ordinal))
                Assert.Ignore("Meta XR Simulator runtime not configured (XR_RUNTIME_JSON); run scripts/run_xrsim_smoke.sh");
            yield break;
        }

        [UnityTest]
        public IEnumerator BootstrapRigTracksSimulatedHands() => RunScene(BootstrapScene);

        [UnityTest]
        public IEnumerator InteractionSdkHandGrabSampleTracksSimulatedHands()
        {
#if UNITY_EDITOR
            // Located via the AssetDatabase: Meta's sample import path contains non-ASCII characters.
            var path = UnityEditor.AssetDatabase.FindAssets("HandGrabExamples t:Scene", new[] { "Assets/Samples" })
                .Select(UnityEditor.AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith("/Example Scenes/HandGrabExamples.unity", StringComparison.Ordinal));
            if (path == null)
                Assert.Ignore("Interaction SDK example scenes not imported; run Tryscape.Editor.SampleImporter.ImportInteractionExamples");
            return RunScene(path);
#else
            Assert.Ignore("Editor-only smoke test");
            return null;
#endif
        }

        static IEnumerator RunScene(string scenePath)
        {
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                scenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
            Assert.Ignore("Editor-only smoke test");
            yield break;
#endif
            var deadline = Time.realtimeSinceStartup + XrStartTimeoutSeconds;
            while (!XrRunning() && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.IsTrue(XrRunning(), "XR loader did not start on the simulator runtime");
            StringAssert.Contains("Simulator", OpenXRRuntime.name, "Active OpenXR runtime is not the Meta XR Simulator");

            Hand[] hands = Array.Empty<Hand>();
            deadline = Time.realtimeSinceStartup + HandTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                hands = UnityEngine.Object.FindObjectsByType<Hand>(FindObjectsInactive.Exclude);
                if (hands.Any(h => h.IsTrackedDataValid))
                    break;
                yield return null;
            }

            var tracked = hands.Where(h => h.IsTrackedDataValid).Select(h => h.Handedness.ToString()).ToArray();
            var interactors = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude)
                .Where(b => b.isActiveAndEnabled && b is IInteractorView)
                .Select(b => b.GetType().Name)
                .GroupBy(n => n)
                .Select(g => $"{g.Key}x{g.Count()}")
                .ToArray();

            Debug.Log($"[Tryscape][XRSIM] scene={scenePath} runtime=\"{OpenXRRuntime.name}\" " +
                      $"runtime_version={OpenXRRuntime.version} api={OpenXRRuntime.apiVersion} " +
                      $"hands_found={hands.Length} tracked=[{string.Join(",", tracked)}] " +
                      $"interactors=[{string.Join(",", interactors)}]");

            Assert.IsNotEmpty(tracked, "No Interaction SDK hand reported valid tracked data from the simulator");
            Assert.IsNotEmpty(interactors, "No enabled interactors found in the rig");
        }

        static bool XrRunning()
        {
            var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
            return manager != null && manager.isInitializationComplete && manager.activeLoader != null;
        }
    }
}
