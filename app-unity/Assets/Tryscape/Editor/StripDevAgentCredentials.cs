using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Tryscape.Editor
{
    /// <summary>
    /// Meta XR Core's DevAgentBuildProcessor (callbackOrder 1) injects the editor machine's LAN address and the
    /// Remote Agent access token into Resources/DevAgentSettings for every Android build, including release builds.
    /// Tryscape does not use DevAgent, and secrets must not ship in APK resources (CLAUDE.md), so this processor runs
    /// afterwards and blanks both serialized values. Meta's post-processor restores the editor-side values afterwards.
    /// Uses the public SerializedObject API; fails the build if the expected fields are missing (SDK changed).
    /// </summary>
    public sealed class StripDevAgentCredentials : IPreprocessBuildWithReport
    {
        const string AssetName = "DevAgentSettings";
        static readonly string[] SensitiveFields = { "accessToken", "serverAddress" };

        public int callbackOrder => 100;

        public void OnPreprocessBuild(BuildReport report)
        {
            var stripped = Strip();
            Debug.Log($"[Tryscape] DevAgent credential strip: {stripped} asset(s) cleared");
        }

        public static int Strip()
        {
            var count = 0;
            foreach (var guid in AssetDatabase.FindAssets(AssetName + " t:ScriptableObject"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("/" + AssetName + ".asset", StringComparison.Ordinal))
                    continue;
                var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                var serialized = new SerializedObject(asset);
                foreach (var field in SensitiveFields)
                {
                    var property = serialized.FindProperty(field);
                    if (property == null || property.propertyType != SerializedPropertyType.String)
                        throw new BuildFailedException($"DevAgent field '{field}' not found in {path}; review Meta SDK change");
                    property.stringValue = string.Empty;
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssetIfDirty(asset);
                count++;
            }
            return count;
        }
    }
}
