#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace OceanViz3.Editor
{
    public static class OceanVizBuildContentBuilder
    {
        public static BuildReport Build(OceanVizBuildContentPreset preset, string outputFolder)
        {
            OceanVizBuildContentCatalog.ValidateOrThrow(preset);

            Debug.Assert(!string.IsNullOrEmpty(outputFolder), "OceanVizBuildContentBuilder: Output folder is required.");
            if (string.IsNullOrEmpty(outputFolder))
            {
                throw new ArgumentException("Output folder is required.", nameof(outputFolder));
            }

            Directory.CreateDirectory(outputFolder);

            BuildTarget buildTarget = EditorUserBuildSettings.activeBuildTarget;
            string outputPath = GetBuildOutputPath(outputFolder, buildTarget);
            string[] scenePaths = OceanVizBuildContentCatalog.GetBuildScenePaths(preset).ToArray();
            Debug.Assert(scenePaths.Length > 1, "OceanVizBuildContentBuilder: Expected Main scene plus at least one selected location scene.");
            Debug.Log("OceanViz build content preset scene list:\n" + string.Join("\n", scenePaths));

            BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions();
            buildPlayerOptions.scenes = scenePaths;
            buildPlayerOptions.locationPathName = outputPath;
            buildPlayerOptions.target = buildTarget;
            buildPlayerOptions.options = BuildOptions.None;

            BuildReport report = BuildPipeline.BuildPlayer(buildPlayerOptions);
            bool buildSucceeded = report.summary.result == BuildResult.Succeeded;
            Debug.Assert(buildSucceeded, "OceanVizBuildContentBuilder: Build failed with result " + report.summary.result + ".");

            if (!buildSucceeded)
            {
                throw new InvalidOperationException("Build failed with result " + report.summary.result + ".");
            }

            string builtStreamingAssetsPath = GetBuiltStreamingAssetsPath(outputPath, outputFolder, buildTarget);
            OceanVizBuildContentTrimmer.TrimBuiltStreamingAssets(builtStreamingAssetsPath, preset);

            Debug.Log("OceanViz build content preset build completed: " + outputPath);
            return report;
        }

        public static string GetBuildOutputPath(string outputFolder, BuildTarget buildTarget)
        {
            string productName = Application.productName;
            Debug.Assert(!string.IsNullOrEmpty(productName), "OceanVizBuildContentBuilder: Application.productName is required.");

            if (buildTarget == BuildTarget.StandaloneWindows || buildTarget == BuildTarget.StandaloneWindows64)
            {
                return Path.Combine(outputFolder, productName + ".exe");
            }

            if (buildTarget == BuildTarget.StandaloneLinux64)
            {
                return Path.Combine(outputFolder, productName + ".x86_64");
            }

            if (buildTarget == BuildTarget.StandaloneOSX)
            {
                return Path.Combine(outputFolder, productName + ".app");
            }

            return Path.Combine(outputFolder, productName);
        }

        public static string GetBuiltStreamingAssetsPath(string outputPath, string outputFolder, BuildTarget buildTarget)
        {
            string productName = Application.productName;

            if (buildTarget == BuildTarget.StandaloneOSX)
            {
                return Path.Combine(outputPath, "Contents", "Resources", "Data", "StreamingAssets");
            }

            if (buildTarget == BuildTarget.StandaloneWindows ||
                buildTarget == BuildTarget.StandaloneWindows64 ||
                buildTarget == BuildTarget.StandaloneLinux64)
            {
                return Path.Combine(outputFolder, productName + "_Data", "StreamingAssets");
            }

            return Path.Combine(outputFolder, productName + "_Data", "StreamingAssets");
        }
    }
}
#endif
