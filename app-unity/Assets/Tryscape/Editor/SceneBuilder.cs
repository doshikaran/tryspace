using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Tryscape.Editor
{
    /// <summary>
    /// Generates the bootstrap scene from code so no scene YAML is hand-edited (technical-spec S04.2).
    /// The XR coordinator (A) owns this file; feature owners supply prefabs/components that this builder wires in.
    /// Batch: -batchmode -quit -executeMethod Tryscape.Editor.SceneBuilder.BuildBootstrapBatch
    /// </summary>
    public static class SceneBuilder
    {
        const string CameraRigPrefab = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
        const string InteractionRigPrefab =
            "Packages/com.meta.xr.sdk.interaction.ovr/Runtime/Prefabs/OVRComprehensiveInteractionRig.prefab";

        [MenuItem("Tryscape/Rebuild Bootstrap Scene")]
        public static void BuildBootstrap() => Build();

        /// <summary>Batch entry: run with -batchmode -quit. Throws on failure so Unity exits with code 1.</summary>
        public static void BuildBootstrapBatch() => Build();

        static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var light = new GameObject("Directional Light");
            var l = light.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 1.1f;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var cameraRig = InstantiatePrefab(CameraRigPrefab, null);
            cameraRig.name = "OVRCameraRig";
            var interactionRig = InstantiatePrefab(InteractionRigPrefab, cameraRig.transform);
            interactionRig.name = "OVRComprehensiveInteractionRig";
            WireInteractionRig(cameraRig, interactionRig);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(2f, 1f, 2f);

            ExperienceBuilder.Build();

            if (!AssetDatabase.IsValidFolder("Assets/Tryscape/Scenes"))
                AssetDatabase.CreateFolder("Assets/Tryscape", "Scenes");
            if (!EditorSceneManager.SaveScene(scene, BuildCli.BootstrapScene))
                throw new InvalidOperationException("Failed to save bootstrap scene");
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(BuildCli.BootstrapScene, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"[Tryscape] Bootstrap scene written to {BuildCli.BootstrapScene}");
        }

        /// <summary>
        /// Sets the scene references Meta's ComprehensiveRigExample sets as overrides (camera rig, player origin and
        /// eyes, eye anchors, centre-eye camera). Fails the build if any of these required references stays unresolved.
        /// </summary>
        static void WireInteractionRig(GameObject cameraRigGo, GameObject interactionRig)
        {
            var cameraRig = cameraRigGo.GetComponent<OVRCameraRig>() ?? throw new InvalidOperationException("OVRCameraRig missing");
            var centerEye = cameraRigGo.transform.Find("TrackingSpace/CenterEyeAnchor")?.GetComponent<Camera>()
                            ?? throw new InvalidOperationException("CenterEyeAnchor camera missing");
            var leftEye = cameraRigGo.transform.Find("TrackingSpace/LeftEyeAnchor") ?? throw new InvalidOperationException("LeftEyeAnchor missing");
            var rightEye = cameraRigGo.transform.Find("TrackingSpace/RightEyeAnchor") ?? throw new InvalidOperationException("RightEyeAnchor missing");
            var assigned = 0;
            foreach (var component in interactionRig.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null)
                    continue;
                var serialized = new SerializedObject(component);
                var changed = false;
                changed |= Assign(serialized, "_ovrCameraRig", cameraRig, ref assigned);
                changed |= Assign(serialized, "_playerOrigin", cameraRigGo.transform, ref assigned);
                changed |= Assign(serialized, "_centerEyeCamera", centerEye, ref assigned);
                changed |= Assign(serialized, "_playerEyes", centerEye.transform, ref assigned);
                changed |= Assign(serialized, "_leftEyeAnchor", leftEye, ref assigned);
                changed |= Assign(serialized, "_rightEyeAnchor", rightEye, ref assigned);
                if (changed)
                    serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            Debug.Log($"[Tryscape] Interaction rig references wired: {assigned}");
            if (assigned == 0)
                throw new InvalidOperationException("No interaction rig references were wired; Meta SDK layout changed?");
        }

        static bool Assign(SerializedObject serialized, string field, UnityEngine.Object value, ref int assigned)
        {
            var property = serialized.FindProperty(field);
            if (property == null || property.propertyType != SerializedPropertyType.ObjectReference ||
                property.objectReferenceValue != null)
                return false;
            property.objectReferenceValue = value;
            assigned++;
            return true;
        }

        static GameObject InstantiatePrefab(string path, Transform parent)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                throw new InvalidOperationException($"Prefab not found: {path}");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            if (instance == null)
                throw new InvalidOperationException($"Could not instantiate {path}");
            return instance;
        }
    }
}
