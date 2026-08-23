#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using OceanViz3.Benchmarking;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace OceanViz3.Editor
{
    public static class AbstractBoidDeterminismMenu
    {
        private const string BuildFolder = "Builds/OceanVizAbstractBoidDeterminism";
        private const string RunAResultPath = "Builds/OceanVizAbstractBoidDeterminism/run-a-result.json";
        private const string RunBResultPath = "Builds/OceanVizAbstractBoidDeterminism/run-b-result.json";
        private const string RunALogPath = "Builds/OceanVizAbstractBoidDeterminism/run-a-player.log";
        private const string RunBLogPath = "Builds/OceanVizAbstractBoidDeterminism/run-b-player.log";
        private const int DefaultTotalSteps = 3600;
        private const int DefaultIntervalSteps = 600;
        private const int ProcessTimeoutSeconds = 140;

        [MenuItem("Tools/OceanViz/Run Abstract Boid Determinism Test")]
        public static void RunAbstractBoidDeterminismTest()
        {
            try
            {
                Debug.Log("[AbstractBoidDeterminism] Building test player.");
                string playerPath = BuildPlayer();

                Debug.Log("[AbstractBoidDeterminism] Running first player launch.");
                RunPlayer(playerPath, RunAResultPath, RunALogPath);
                AbstractBoidDeterminismResult runA = ReadResult(RunAResultPath);

                Debug.Log("[AbstractBoidDeterminism] Running second player launch.");
                RunPlayer(playerPath, RunBResultPath, RunBLogPath);
                AbstractBoidDeterminismResult runB = ReadResult(RunBResultPath);

                string mismatch = CompareResults(runA, runB);
                AssetDatabase.Refresh();

                if (!string.IsNullOrEmpty(mismatch))
                {
                    throw new InvalidOperationException(mismatch);
                }

                string finalHash = string.Empty;
                if (runA.snapshotCount > 0)
                {
                    finalHash = runA.snapshots[runA.snapshotCount - 1].hash;
                }

                EditorUtility.DisplayDialog(
                    "Abstract Boid Determinism",
                    "Determinism test passed.\n\nSteps: " + runA.totalSteps +
                    "\nSnapshots: " + runA.snapshotCount +
                    "\nBoids: " + runA.finalBoidCount +
                    "\nFinal hash: " + finalHash,
                    "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Abstract Boid Determinism Failed", exception.Message, "OK");
            }
        }

        private static string BuildPlayer()
        {
            Directory.CreateDirectory(BuildFolder);

            BuildTarget buildTarget = EditorUserBuildSettings.activeBuildTarget;
            string outputPath = OceanVizBuildContentBuilder.GetBuildOutputPath(BuildFolder, buildTarget);

            BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions();
            buildPlayerOptions.scenes = GetScenePaths();
            buildPlayerOptions.locationPathName = outputPath;
            buildPlayerOptions.target = buildTarget;
            buildPlayerOptions.options = BuildOptions.None;

            BuildReport report = BuildPipeline.BuildPlayer(buildPlayerOptions);
            if (report == null)
            {
                throw new InvalidOperationException("Abstract boid determinism build failed before Unity created a build report.");
            }

            bool buildSucceeded = report.summary.result == BuildResult.Succeeded;
            Debug.Assert(buildSucceeded, "Abstract boid determinism build failed with result " + report.summary.result + ".");
            if (!buildSucceeded)
            {
                throw new InvalidOperationException("Abstract boid determinism build failed with result " + report.summary.result + ".");
            }

            return outputPath;
        }

        private static string[] GetScenePaths()
        {
            List<string> scenePaths = new List<string>();
            scenePaths.Add(OceanVizBuildContentCatalog.MainScenePath);

            List<string> locationNames = OceanVizBuildContentCatalog.GetLocationFolderNames();
            foreach (string locationName in locationNames)
            {
                string scenePath = OceanVizBuildContentCatalog.GetLocationScenePath(locationName);
                if (!string.IsNullOrEmpty(scenePath))
                {
                    scenePaths.Add(scenePath);
                }
            }

            return scenePaths.ToArray();
        }

        private static void RunPlayer(string playerPath, string resultPath, string logPath)
        {
            string absoluteResultPath = Path.GetFullPath(resultPath);
            string resultDirectory = Path.GetDirectoryName(absoluteResultPath);
            if (!string.IsNullOrEmpty(resultDirectory))
            {
                Directory.CreateDirectory(resultDirectory);
            }

            if (File.Exists(absoluteResultPath))
            {
                File.Delete(absoluteResultPath);
            }

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = Path.GetFullPath(playerPath);
            startInfo.Arguments =
                "-batchmode -nographics -logFile " +
                Quote(Path.GetFullPath(logPath)) +
                " -oceanvizBoidDeterminism -boidDeterminismOutput " +
                Quote(absoluteResultPath) +
                " -boidDeterminismSteps " +
                DefaultTotalSteps +
                " -boidDeterminismIntervalSteps " +
                DefaultIntervalSteps;
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = false;
            startInfo.RedirectStandardError = false;
            startInfo.CreateNoWindow = true;

            using (Process process = Process.Start(startInfo))
            {
                Debug.Assert(process != null, "Failed to start abstract boid determinism player.");
                if (process == null)
                {
                    throw new InvalidOperationException("Failed to start abstract boid determinism player.");
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                while (!process.HasExited)
                {
                    if (stopwatch.Elapsed.TotalSeconds > ProcessTimeoutSeconds)
                    {
                        process.Kill();
                        throw new TimeoutException("Abstract boid determinism player exceeded " + ProcessTimeoutSeconds + " seconds. Last log: " + ReadLastLogLine(logPath));
                    }

                    System.Threading.Thread.Sleep(250);
                }

                Debug.Log("[AbstractBoidDeterminism Player] Last log: " + ReadLastLogLine(logPath));

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("Abstract boid determinism player failed with exit code " + process.ExitCode + ". Last log: " + ReadLastLogLine(logPath));
                }
            }

            if (!File.Exists(absoluteResultPath))
            {
                throw new FileNotFoundException("Abstract boid determinism result was not created.", absoluteResultPath);
            }
        }

        private static AbstractBoidDeterminismResult ReadResult(string path)
        {
            string resultJson = File.ReadAllText(path);
            AbstractBoidDeterminismResult result = JsonUtility.FromJson<AbstractBoidDeterminismResult>(resultJson);
            Debug.Assert(result != null, "Abstract boid determinism result could not be parsed.");
            if (result == null)
            {
                throw new InvalidOperationException("Abstract boid determinism result could not be parsed: " + path);
            }

            if (result.timedOut)
            {
                throw new TimeoutException("Abstract boid determinism run timed out: " + result.failureMessage);
            }

            return result;
        }

        private static string CompareResults(AbstractBoidDeterminismResult runA, AbstractBoidDeterminismResult runB)
        {
            if (runA.totalSteps != runB.totalSteps)
            {
                return "Total step count differed: " + runA.totalSteps + " vs " + runB.totalSteps + ".";
            }

            if (runA.intervalSteps != runB.intervalSteps)
            {
                return "Snapshot interval differed: " + runA.intervalSteps + " vs " + runB.intervalSteps + ".";
            }

            if (runA.snapshotCount != runB.snapshotCount)
            {
                return "Snapshot count differed: " + runA.snapshotCount + " vs " + runB.snapshotCount + ".";
            }

            if (runA.finalBoidCount != runB.finalBoidCount)
            {
                return "Final boid count differed: " + runA.finalBoidCount + " vs " + runB.finalBoidCount + ".";
            }

            for (int i = 0; i < runA.snapshotCount; i++)
            {
                AbstractBoidSnapshotResult snapshotA = runA.snapshots[i];
                AbstractBoidSnapshotResult snapshotB = runB.snapshots[i];
                if (snapshotA.step != snapshotB.step)
                {
                    return "Snapshot step differed at index " + i + ": " + snapshotA.step + " vs " + snapshotB.step + ".";
                }

                if (!string.Equals(snapshotA.hash, snapshotB.hash, StringComparison.Ordinal))
                {
                    return "Snapshot hash differed at step " + snapshotA.step +
                           ". Run A: " + snapshotA.hash +
                           ", run B: " + snapshotB.hash +
                           ". Tracked prey A position: " + snapshotA.trackedPosition +
                           ", run B position: " + snapshotB.trackedPosition + ".";
                }
            }

            return string.Empty;
        }

        private static string ReadLastLogLine(string path)
        {
            if (!File.Exists(path))
            {
                return "(log file missing)";
            }

            string[] lines = File.ReadAllLines(path);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    return lines[i];
                }
            }

            return "(log file empty)";
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
#endif
