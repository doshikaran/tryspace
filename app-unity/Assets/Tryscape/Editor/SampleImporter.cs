using System;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEngine;

namespace Tryscape.Editor
{
    /// <summary>
    /// Imports Meta Interaction SDK example scenes for toolchain verification (M0 simulator gate). The imported
    /// folder is git-ignored; Tryscape scenes never depend on it.
    /// Batch: -batchmode -quit -executeMethod Tryscape.Editor.SampleImporter.ImportInteractionExamples
    /// </summary>
    public static class SampleImporter
    {
        const string Package = "com.meta.xr.sdk.interaction.ovr";
        const string SampleName = "Example Scenes";

        [MenuItem("Tryscape/Import Interaction SDK Example Scenes")]
        public static void ImportInteractionExamples()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForPackageName(Package)
                       ?? throw new InvalidOperationException($"{Package} is not installed");
            var sample = Sample.FindByPackage(Package, info.version).FirstOrDefault(s => s.displayName == SampleName);
            if (string.IsNullOrEmpty(sample.displayName))
                throw new InvalidOperationException($"Sample '{SampleName}' not found in {Package}@{info.version}");
            if (!sample.isImported && !sample.Import(Sample.ImportOptions.HideImportWindow | Sample.ImportOptions.OverridePreviousImports))
                throw new InvalidOperationException("Sample import failed");
            AssetDatabase.Refresh();
            Debug.Log($"[Tryscape] Imported '{SampleName}' from {Package}@{info.version} to {sample.importPath}");
        }
    }
}
