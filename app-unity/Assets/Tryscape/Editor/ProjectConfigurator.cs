using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;

namespace Tryscape.Editor
{
    /// <summary>
    /// Applies the Tryscape XR/Android baseline through public Unity, XR Management, OpenXR and Meta setup APIs.
    /// Idempotent. Batch: -executeMethod Tryscape.Editor.ProjectConfigurator.ConfigureBatch (no -quit; exits itself).
    /// </summary>
    public static class ProjectConfigurator
    {
        // Provisional: not yet registered with a Meta Horizon application (technical-spec S21.1).
        public const string ApplicationId = "com.tryscape.app";
        const string OpenXRLoader = "UnityEngine.XR.OpenXR.OpenXRLoader";
        const int BatchTimeoutSeconds = 600;

        static readonly string[] AndroidFeatures =
        {
            "com.unity.openxr.feature.metaquest",
            "com.meta.openxr.feature.metaxr",
            "com.unity.openxr.feature.input.handtracking",
            "com.unity.openxr.feature.input.metahandtrackingaim",
            "com.unity.openxr.feature.input.oculustouch",
            // Eye-tracked gaze (S11, AC35) and controller proximity; enabled by Meta project setup, pinned here.
            "com.unity.openxr.feature.input.eyetracking",
            "com.meta.openxr.feature.input.oculustouch.proximity",
        };

        static readonly string[] StandaloneFeatures =
        {
            "com.meta.openxr.feature.metaxr",
            "com.unity.openxr.feature.input.handtracking",
            "com.unity.openxr.feature.input.metahandtrackingaim",
            "com.unity.openxr.feature.input.oculustouch",
        };

        [MenuItem("Tryscape/Configure Project (XR + Android)")]
        public static void ConfigureInteractive()
        {
            ApplySettings();
            RunMetaFixAll().ContinueWith(t => Debug.Log($"[Tryscape] Meta FixAll finished: {t.Status}"),
                TaskScheduler.FromCurrentSynchronizationContext());
        }

        public static void ConfigureBatch()
        {
            var started = EditorApplication.timeSinceStartup;
            try
            {
                ApplySettings();
            }
            catch (Exception e)
            {
                Debug.LogError($"[Tryscape] Configuration failed: {e}");
                EditorApplication.Exit(1);
                return;
            }

            var fix = RunMetaFixAll();
            void Poll()
            {
                if (fix.IsCompleted)
                {
                    EditorApplication.update -= Poll;
                    // Re-apply our explicit choices after Meta's rules so the result is deterministic.
                    ApplySettings();
                    AssetDatabase.SaveAssets();
                    Debug.Log($"[Tryscape] Configuration complete; Meta FixAll status={fix.Status}");
                    EditorApplication.Exit(fix.IsFaulted ? 1 : 0);
                }
                else if (EditorApplication.timeSinceStartup - started > BatchTimeoutSeconds)
                {
                    EditorApplication.update -= Poll;
                    Debug.LogError("[Tryscape] Configuration timed out waiting for Meta FixAll");
                    EditorApplication.Exit(1);
                }
            }
            EditorApplication.update += Poll;
        }

        /// <summary>Asserts the baseline without changing anything. Run with -quit; throws (exit 1) on any mismatch.</summary>
        public static void VerifyBatch()
        {
            var problems = Verify();
            foreach (var p in problems)
                Debug.LogError($"[Tryscape] VERIFY FAIL: {p}");
            // Evidence file for scripts/unity_batch.sh --artifact: written only after verification ran to the end.
            var report = System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "..", "artifacts", "unity", "verify-config.json"));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(report)!);
            System.IO.File.WriteAllText(report, "{\"problems\":" + problems.Count + ",\"problem_list\":[" +
                string.Join(",", problems.ConvertAll(p => "\"" + p.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]}\n");
            Debug.Log($"[Tryscape] Verify complete: {problems.Count} problem(s)");
            if (problems.Count > 0)
                throw new InvalidOperationException($"{problems.Count} configuration problem(s); see log");
        }

        public static List<string> Verify()
        {
            var problems = new List<string>();
            void Check(bool ok, string what)
            {
                if (!ok) problems.Add(what);
            }

            var android = NamedBuildTarget.Android;
            Check(PlayerSettings.GetApplicationIdentifier(android) == ApplicationId, "application id");
            Check(PlayerSettings.GetScriptingBackend(android) == ScriptingImplementation.IL2CPP, "IL2CPP backend");
            Check(PlayerSettings.Android.targetArchitectures == AndroidArchitecture.ARM64, "ARM64 only");
            Check(PlayerSettings.Android.minSdkVersion == AndroidSdkVersions.AndroidApiLevel32, "min SDK 32");
            Check((int)PlayerSettings.Android.targetSdkVersion == 34, "target SDK 34");
            var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            Check(!PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android) && apis.Length == 1 &&
                  apis[0] == GraphicsDeviceType.Vulkan, "Vulkan only");
            Check(PlayerSettings.colorSpace == ColorSpace.Linear, "linear color space");
            Check(PlayerSettings.defaultInterfaceOrientation == UIOrientation.LandscapeLeft, "landscape-left orientation");
            // Plain HTTP only to the developer machine in development builds (S13: release accepts allowlisted HTTPS only).
            Check(PlayerSettings.insecureHttpOption == InsecureHttpOption.DevelopmentOnly, "insecure HTTP development builds only");
            Check(OVRProjectConfig.CachedProjectConfig.handTrackingSupport ==
                  OVRProjectConfig.HandTrackingSupport.ControllersAndHands, "hand tracking enabled");
            var meta = OVRProjectConfig.CachedProjectConfig;
            Check(meta.insightPassthroughSupport == OVRProjectConfig.FeatureSupport.Supported, "passthrough supported");
            Check(meta.sceneSupport == OVRProjectConfig.FeatureSupport.Supported, "scene support");
            Check(meta.anchorSupport == OVRProjectConfig.AnchorSupport.Enabled, "anchor support");
            Check(meta.eyeTrackingSupport == OVRProjectConfig.FeatureSupport.Supported, "eye tracking supported");
            foreach (var group in new[] { BuildTargetGroup.Android, BuildTargetGroup.Standalone })
                Check(XRPackageMetadataStore.IsLoaderAssigned(OpenXRLoader, group), $"OpenXR loader for {group}");
            foreach (var (group, ids) in new[]
                     {
                         (BuildTargetGroup.Android, AndroidFeatures), (BuildTargetGroup.Standalone, StandaloneFeatures),
                     })
            foreach (var id in ids)
            {
                var feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(group, id);
                Check(feature != null && feature.enabled, $"OpenXR feature {id} on {group}");
            }
            var androidSpeech = AssetImporter.GetAtPath(AndroidSpeechPlugin) as PluginImporter;
            Check(androidSpeech != null && androidSpeech.GetCompatibleWithPlatform(BuildTarget.Android) &&
                  !androidSpeech.GetCompatibleWithEditor(), "speech plugin ships for Android only");
            var editorSpeech = AssetImporter.GetAtPath(EditorSpeechPlugin) as PluginImporter;
            Check(editorSpeech != null && editorSpeech.GetCompatibleWithEditor() &&
                  !editorSpeech.GetCompatibleWithPlatform(BuildTarget.Android), "macOS speech plugin is Editor only");
            Check(RemoveOrphanedOpenXRSubAssets(apply: false) == 0, "no orphaned OpenXR settings sub-assets");
            return problems;
        }

        const string OpenXRPackageSettingsPath = "Assets/XR/Settings/OpenXR Package Settings.asset";

        /// <summary>
        /// Headless first import can leave unreferenced duplicate OpenXRSettings (and their feature sub-assets) in the
        /// package settings file. They are invisible to the editor UI but make every Android build re-serialize the
        /// asset differently. Remove every sub-asset not reachable from the package settings root.
        /// </summary>
        public static int RemoveOrphanedOpenXRSubAssets(bool apply)
        {
            var all = AssetDatabase.LoadAllAssetsAtPath(OpenXRPackageSettingsPath);
            var main = AssetDatabase.LoadMainAssetAtPath(OpenXRPackageSettingsPath);
            if (main == null || all.Length == 0)
                return 0;
            var reachable = new HashSet<UnityEngine.Object>();
            var stack = new Stack<UnityEngine.Object>();
            stack.Push(main);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == null || !reachable.Add(current))
                    continue;
                var iterator = new SerializedObject(current).GetIterator();
                while (iterator.Next(true))
                    if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue != null &&
                        AssetDatabase.GetAssetPath(iterator.objectReferenceValue) == OpenXRPackageSettingsPath)
                        stack.Push(iterator.objectReferenceValue);
            }
            var orphans = new List<UnityEngine.Object>();
            foreach (var asset in all)
                if (asset != null && !reachable.Contains(asset))
                    orphans.Add(asset);
            if (apply && orphans.Count > 0)
            {
                foreach (var orphan in orphans)
                    AssetDatabase.RemoveObjectFromAsset(orphan);
                EditorUtility.SetDirty(main);
                AssetDatabase.SaveAssets();
                Debug.Log($"[Tryscape] Removed {orphans.Count} orphaned OpenXR sub-asset(s)");
            }
            return orphans.Count;
        }

        static Task RunMetaFixAll()
        {
            return Task.WhenAll(
                OVRProjectSetup.FixAllAsync(BuildTargetGroup.Android),
                OVRProjectSetup.FixAllAsync(BuildTargetGroup.Standalone));
        }

        const string AndroidSpeechPlugin = "Assets/Plugins/Android/arm64-v8a/libtryscape_whisper.so";
        const string EditorSpeechPlugin = "Assets/Plugins/macOS/libtryscape_whisper.dylib";

        /// <summary>On-device speech (D12): the arm64 library ships only in the Android player; the macOS one only loads in the Editor.</summary>
        static void ConfigureSpeechPlugins()
        {
            if (AssetImporter.GetAtPath(AndroidSpeechPlugin) is PluginImporter android)
            {
                android.SetCompatibleWithAnyPlatform(false);
                android.SetCompatibleWithEditor(false);
                android.SetCompatibleWithPlatform(BuildTarget.Android, true);
                android.SetPlatformData(BuildTarget.Android, "CPU", "ARM64");
                android.SaveAndReimport();
            }
            if (AssetImporter.GetAtPath(EditorSpeechPlugin) is PluginImporter editor)
            {
                editor.SetCompatibleWithAnyPlatform(false);
                editor.SetCompatibleWithPlatform(BuildTarget.Android, false);
                editor.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, false);
                editor.SetCompatibleWithEditor(true);
                editor.SetEditorData("OS", "OSX");
                editor.SetEditorData("CPU", "ARM64");
                editor.SaveAndReimport();
            }
        }

        static void ApplySettings()
        {
            PlayerSettings.companyName = "Tryscape";
            PlayerSettings.productName = "Tryscape";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly;

            var android = NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(android, ApplicationId);
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)34;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });

            // Hands-first: hand tracking enabled (Meta default is ControllersOnly). Controllers stay optional.
            var metaConfig = OVRProjectConfig.CachedProjectConfig;
            metaConfig.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
            metaConfig.handTrackingFrequency = OVRProjectConfig.HandTrackingFrequency.HIGH;
            // Mixed reality (S11, D12): passthrough by default, MRUK scene data and anchors, optional eye-tracked gaze.
            // "Supported", not "Required": a headset or user without these features still runs the hand journey.
            metaConfig.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Supported;
            metaConfig.sceneSupport = OVRProjectConfig.FeatureSupport.Supported;
            metaConfig.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
            metaConfig.eyeTrackingSupport = OVRProjectConfig.FeatureSupport.Supported;
            OVRProjectConfig.CommitProjectConfig(metaConfig);

            ConfigureSpeechPlugins();

            AssignOpenXR(BuildTargetGroup.Android);
            AssignOpenXR(BuildTargetGroup.Standalone);
            EnableFeatures(BuildTargetGroup.Android, AndroidFeatures);
            EnableFeatures(BuildTargetGroup.Standalone, StandaloneFeatures);
            AssetDatabase.SaveAssets();
            RemoveOrphanedOpenXRSubAssets(apply: true);
        }

        static void AssignOpenXR(BuildTargetGroup group)
        {
            var perTarget = GetOrCreatePerBuildTargetSettings();
            if (!perTarget.HasSettingsForBuildTarget(group))
                perTarget.CreateDefaultSettingsForBuildTarget(group);
            if (!perTarget.HasManagerSettingsForBuildTarget(group))
                perTarget.CreateDefaultManagerSettingsForBuildTarget(group);

            var general = perTarget.SettingsForBuildTarget(group);
            general.InitManagerOnStart = true;
            var manager = perTarget.ManagerSettingsForBuildTarget(group);
            if (!XRPackageMetadataStore.IsLoaderAssigned(OpenXRLoader, group) &&
                !XRPackageMetadataStore.AssignLoader(manager, OpenXRLoader, group))
                throw new InvalidOperationException($"Could not assign OpenXR loader for {group}");
            EditorUtility.SetDirty(perTarget);
        }

        static XRGeneralSettingsPerBuildTarget GetOrCreatePerBuildTargetSettings()
        {
            if (EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey,
                    out XRGeneralSettingsPerBuildTarget existing) && existing != null)
                return existing;

            var guids = AssetDatabase.FindAssets("t:XRGeneralSettingsPerBuildTarget");
            if (guids.Length > 0)
            {
                var found = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(
                    AssetDatabase.GUIDToAssetPath(guids[0]));
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, found, true);
                return found;
            }

            const string folder = "Assets/XR";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", "XR");
            var created = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(created, folder + "/XRGeneralSettingsPerBuildTarget.asset");
            AssetDatabase.SaveAssets();
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, created, true);
            return created;
        }

        static void EnableFeatures(BuildTargetGroup group, IEnumerable<string> featureIds)
        {
            foreach (var id in featureIds)
            {
                var feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(group, id);
                if (feature == null)
                    throw new InvalidOperationException($"OpenXR feature '{id}' not available for {group}");
                if (!feature.enabled)
                {
                    feature.enabled = true;
                    EditorUtility.SetDirty(feature);
                }
            }
        }
    }
}
