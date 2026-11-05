using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Tryscape.Editor
{
    /// <summary>
    /// Batch build entry points (technical-spec S19.1). Exits non-zero on failure and writes a build manifest with
    /// the artifact hash, commit and toolchain. Never contains signing secrets: development builds use Unity's
    /// debug keystore; release signing is injected by secure tooling later.
    ///
    /// Unity -batchmode -quit -projectPath app-unity -buildTarget Android
    ///       -executeMethod Tryscape.Editor.BuildCli.BuildAndroid [-tryscapeOutput path/to.apk] [-tryscapeRelease]
    /// </summary>
    public static class BuildCli
    {
        public const string BootstrapScene = "Assets/Tryscape/Scenes/Bootstrap.unity";

        /// <summary>Batch entry: run with -batchmode -quit. Throws on failure so Unity exits with code 1.</summary>
        public static void BuildAndroid()
        {
            if (!BuildAndroidInternal())
                throw new BuildFailedException("Tryscape Android build failed; see log above");
        }

        static bool BuildAndroidInternal()
        {
            var repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            var release = HasArg("-tryscapeRelease");
            var output = GetArg("-tryscapeOutput") ??
                         Path.Combine(repoRoot, "artifacts", "builds", release ? "tryscape-release.apk" : "tryscape-dev.apk");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            if (!File.Exists(Path.Combine(repoRoot, "app-unity", BootstrapScene)))
            {
                Debug.LogError($"[Tryscape] Missing bootstrap scene {BootstrapScene}");
                return false;
            }

            if (!ConfigureNdk())
                return false;

            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
            EditorUserBuildSettings.buildAppBundle = false;

            var options = new BuildPlayerOptions
            {
                scenes = new[] { BootstrapScene },
                locationPathName = output,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = release ? BuildOptions.None : BuildOptions.Development,
            };

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log($"[Tryscape] Build result={summary.result} errors={summary.totalErrors} " +
                      $"warnings={summary.totalWarnings} size={summary.totalSize} time={summary.totalTime}");
            if (summary.result != BuildResult.Succeeded || !File.Exists(output))
                return false;

            WriteManifest(repoRoot, output, release, summary);
            return true;
        }

        /// <summary>
        /// Optional machine override: TRYSCAPE_ANDROID_NDK_ROOT points at a complete NDK (must contain
        /// source.properties). Needed when the Hub's embedded NDK folder is incomplete. Persists in EditorPrefs.
        /// </summary>
        static bool ConfigureNdk()
        {
            var ndk = Environment.GetEnvironmentVariable("TRYSCAPE_ANDROID_NDK_ROOT");
            if (!string.IsNullOrEmpty(ndk))
            {
                if (!File.Exists(Path.Combine(ndk, "source.properties")))
                {
                    Debug.LogError($"[Tryscape] TRYSCAPE_ANDROID_NDK_ROOT is not a valid NDK: {ndk}");
                    return false;
                }
                UnityEditor.Android.AndroidExternalToolsSettings.ndkRootPath = ndk;
            }
            Debug.Log($"[Tryscape] Android NDK: {UnityEditor.Android.AndroidExternalToolsSettings.ndkRootPath} " +
                      $"(revision {NdkRevision()})");
            return true;
        }

        static string NdkRevision()
        {
            var props = Path.Combine(UnityEditor.Android.AndroidExternalToolsSettings.ndkRootPath ?? "", "source.properties");
            if (!File.Exists(props))
                return "unknown";
            foreach (var line in File.ReadAllLines(props))
                if (line.StartsWith("Pkg.Revision"))
                    return line.Split('=')[1].Trim();
            return "unknown";
        }

        static void WriteManifest(string repoRoot, string apkPath, bool release, BuildSummary summary)
        {
            string sha;
            using (var stream = File.OpenRead(apkPath))
            using (var hasher = SHA256.Create())
                sha = string.Concat(hasher.ComputeHash(stream).Select(b => b.ToString("x2")));

            var commit = RunGit(repoRoot, "rev-parse HEAD");
            var dirty = !string.IsNullOrEmpty(RunGit(repoRoot, "status --porcelain"));
            var json = new StringBuilder();
            json.Append("{\n");
            Field(json, "schema_version", "1.0.0");
            Field(json, "artifact", Path.GetFileName(apkPath));
            Field(json, "artifact_sha256", sha);
            json.Append($"  \"artifact_bytes\": {new FileInfo(apkPath).Length},\n");
            Field(json, "build_type", release ? "release" : "development");
            Field(json, "commit", commit);
            json.Append($"  \"working_tree_dirty\": {(dirty ? "true" : "false")},\n");
            Field(json, "unity_version", Application.unityVersion);
            Field(json, "android_ndk_revision", NdkRevision());
            Field(json, "application_id", PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android));
            Field(json, "bundle_version", PlayerSettings.bundleVersion);
            Field(json, "scripting_backend", PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android).ToString());
            Field(json, "architectures", PlayerSettings.Android.targetArchitectures.ToString());
            Field(json, "min_sdk", ((int)PlayerSettings.Android.minSdkVersion).ToString());
            Field(json, "target_sdk", ((int)PlayerSettings.Android.targetSdkVersion).ToString());
            Field(json, "build_result", summary.result.ToString());
            Field(json, "built_at_utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), last: true);
            json.Append("}\n");
            File.WriteAllText(apkPath + ".manifest.json", json.ToString());
        }

        static void Field(StringBuilder sb, string key, string value, bool last = false)
        {
            var escaped = (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
            sb.Append($"  \"{key}\": \"{escaped}\"{(last ? "" : ",")}\n");
        }

        static string RunGit(string workingDirectory, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                var output = process!.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(10000);
                return process.ExitCode == 0 ? output : "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        static bool HasArg(string name) => Environment.GetCommandLineArgs().Contains(name);

        static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
