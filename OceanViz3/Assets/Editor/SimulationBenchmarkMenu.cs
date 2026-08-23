#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using OceanViz3.Benchmarking;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace OceanViz3.Editor
{
    public static class SimulationBenchmarkMenu
    {
        private const string BenchmarkConfigPath = "Benchmarks/simulation-cpu-baseline.json";
        private const string HoverStressConfigPath = "Benchmarks/simulation-hover-stress.json";
        private const string BenchmarkResultsMarkdownPath = "Benchmarks/SimulationCpuBenchmarkResults.md";
        private const string BenchmarkBuildFolder = "Builds/OceanVizSimulationBenchmark";
        private const string BenchmarkResultJsonPath = "Builds/OceanVizSimulationBenchmark/latest-result.json";
        private const string BenchmarkPlayerLogPath = "Builds/OceanVizSimulationBenchmark/latest-player.log";
        private const int EditorProcessTimeoutSeconds = 35;

        [MenuItem("Tools/OceanViz/Run Simulation CPU Benchmark")]
        public static void RunSimulationCpuBenchmark()
        {
            RunBenchmark(BenchmarkConfigPath);
        }

        [MenuItem("Tools/OceanViz/Run Entity Hover Stress Benchmark")]
        public static void RunEntityHoverStressBenchmark()
        {
            RunBenchmark(HoverStressConfigPath);
        }

        private static void RunBenchmark(string configPath)
        {
            try
            {
                Debug.Log("[SimulationBenchmark] Building benchmark player.");
                string playerPath = BuildBenchmarkPlayer();
                Debug.Log("[SimulationBenchmark] Running benchmark player. Unity may be busy until the run finishes.");
                RunBenchmarkPlayer(playerPath, configPath);
                SimulationBenchmarkResult result = ReadResult();
                AppendMarkdownResult(result);
                if (result.timedOut || result.measurementFrames <= 0 || result.averageFrameMilliseconds <= 0.0)
                {
                    throw new InvalidOperationException("Simulation benchmark did not produce a valid measurement. Final state: " + result.finalState + ". Dynamic entities: " + result.dynamicEntityCount + ", static entities: " + result.staticEntityCount + ".");
                }

                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog(
                    "OceanViz Benchmark",
                    "Simulation CPU benchmark finished.\n\nAverage FPS: " + Format(GetAverageFramesPerSecond(result)) + "\nAverage frame: " + Format(result.averageFrameMilliseconds) + " ms\nAverage process CPU/frame: " + Format(result.averageProcessCpuMillisecondsPerFrame) + " ms\nDynamic requested/active/total: " + result.requestedDynamicEntityCount + "/" + result.dynamicEntityCount + "/" + result.dynamicEntityTotalCount + "\nStatic requested/active/total: " + result.requestedStaticEntityCount + "/" + result.staticEntityCount + "/" + result.staticEntityTotalCount + "\n\nSaved to " + BenchmarkResultsMarkdownPath,
                    "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("OceanViz Benchmark Failed", exception.Message, "OK");
            }
        }

        private static string BuildBenchmarkPlayer()
        {
            Directory.CreateDirectory(BenchmarkBuildFolder);

            BuildTarget buildTarget = EditorUserBuildSettings.activeBuildTarget;
            string outputPath = OceanVizBuildContentBuilder.GetBuildOutputPath(BenchmarkBuildFolder, buildTarget);
            string[] scenePaths = GetBenchmarkScenePaths();

            BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions();
            buildPlayerOptions.scenes = scenePaths;
            buildPlayerOptions.locationPathName = outputPath;
            buildPlayerOptions.target = buildTarget;
            buildPlayerOptions.options = BuildOptions.None;

            BuildReport report = BuildPipeline.BuildPlayer(buildPlayerOptions);
            if (report == null)
            {
                throw new InvalidOperationException("Simulation benchmark build failed before Unity created a build report.");
            }

            bool buildSucceeded = report.summary.result == BuildResult.Succeeded;
            Debug.Assert(buildSucceeded, "Simulation benchmark build failed with result " + report.summary.result + ".");

            if (!buildSucceeded)
            {
                throw new InvalidOperationException("Simulation benchmark build failed with result " + report.summary.result + ".");
            }

            return outputPath;
        }

        private static string[] GetBenchmarkScenePaths()
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

        private static void RunBenchmarkPlayer(string playerPath, string configPath)
        {
            string absoluteConfigPath = Path.GetFullPath(configPath);
            string absoluteResultPath = Path.GetFullPath(BenchmarkResultJsonPath);
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
                Quote(Path.GetFullPath(BenchmarkPlayerLogPath)) +
                " -oceanvizBenchmark -benchmarkConfig " +
                Quote(absoluteConfigPath) +
                " -benchmarkOutput " +
                Quote(absoluteResultPath);
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = false;
            startInfo.RedirectStandardError = false;
            startInfo.CreateNoWindow = true;

            using (Process process = Process.Start(startInfo))
            {
                Debug.Assert(process != null, "Failed to start simulation benchmark player.");
                if (process == null)
                {
                    throw new InvalidOperationException("Failed to start simulation benchmark player.");
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                while (!process.HasExited)
                {
                    if (stopwatch.Elapsed.TotalSeconds > EditorProcessTimeoutSeconds)
                    {
                        process.Kill();
                        throw new TimeoutException("Simulation benchmark player exceeded " + EditorProcessTimeoutSeconds + " seconds. Last log: " + ReadLastLogLine());
                    }

                    System.Threading.Thread.Sleep(250);
                }

                Debug.Log("[SimulationBenchmark Player] Last log: " + ReadLastLogLine());

                if (process.ExitCode != 0)
                {
                    string message = "Simulation benchmark player failed with exit code " + process.ExitCode + ". Last benchmark log: " + ReadLastBenchmarkLogLine();
                    if (File.Exists(absoluteResultPath))
                    {
                        SimulationBenchmarkResult failedResult = ReadResult();
                        if (failedResult.timedOut)
                        {
                            message = "Simulation benchmark timed out in " + failedResult.finalState + " after " + failedResult.maxRunSeconds + " seconds.";
                        }
                    }
                    throw new InvalidOperationException(message);
                }
            }

            if (!File.Exists(absoluteResultPath))
            {
                throw new FileNotFoundException("Simulation benchmark result was not created.", absoluteResultPath);
            }
        }

        private static SimulationBenchmarkResult ReadResult()
        {
            string resultJson = File.ReadAllText(BenchmarkResultJsonPath);
            SimulationBenchmarkResult result = JsonUtility.FromJson<SimulationBenchmarkResult>(resultJson);
            Debug.Assert(result != null, "Simulation benchmark result could not be parsed.");

            if (result == null)
            {
                throw new InvalidOperationException("Simulation benchmark result could not be parsed.");
            }

            return result;
        }

        private static void AppendMarkdownResult(SimulationBenchmarkResult result)
        {
            EnsureMarkdownSchema();
            bool fileExists = File.Exists(BenchmarkResultsMarkdownPath);
            StringBuilder builder = new StringBuilder();

            if (!fileExists)
            {
                builder.AppendLine("# Simulation CPU Benchmark Results");
                builder.AppendLine();
                builder.AppendLine("Run from `Tools/OceanViz/Run Simulation CPU Benchmark`.");
                builder.AppendLine();
                builder.AppendLine("| Date UTC | Benchmark | Location | Views | Avg FPS | Dynamic requested | Dynamic active/total | Dynamic culled | Static requested | Static active/total | Static culled | Avg frame ms | P95 frame ms | Worst frame ms | Avg process CPU/frame ms |");
                builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            }

            builder.Append("| ");
            builder.Append(result.dateUtc);
            builder.Append(" | ");
            builder.Append(EscapeMarkdown(result.benchmarkName));
            builder.Append(" | ");
            builder.Append(EscapeMarkdown(result.locationName));
            builder.Append(" | ");
            builder.Append(result.viewsCount);
            builder.Append(" | ");
            builder.Append(Format(GetAverageFramesPerSecond(result)));
            builder.Append(" | ");
            builder.Append(result.requestedDynamicEntityCount);
            builder.Append(" | ");
            builder.Append(result.dynamicEntityCount);
            builder.Append("/");
            builder.Append(result.dynamicEntityTotalCount);
            builder.Append(" | ");
            builder.Append(result.dynamicEntityDisabledOrCulledCount);
            builder.Append(" | ");
            builder.Append(result.requestedStaticEntityCount);
            builder.Append(" | ");
            builder.Append(result.staticEntityCount);
            builder.Append("/");
            builder.Append(result.staticEntityTotalCount);
            builder.Append(" | ");
            builder.Append(result.staticEntityDisabledOrCulledCount);
            builder.Append(" | ");
            builder.Append(Format(result.averageFrameMilliseconds));
            builder.Append(" | ");
            builder.Append(Format(result.percentile95FrameMilliseconds));
            builder.Append(" | ");
            builder.Append(Format(result.worstFrameMilliseconds));
            builder.Append(" | ");
            builder.Append(Format(result.averageProcessCpuMillisecondsPerFrame));
            if (result.timedOut)
            {
                builder.Append(" timed out in ");
                builder.Append(EscapeMarkdown(result.finalState));
            }
            builder.AppendLine(" |");

            File.AppendAllText(BenchmarkResultsMarkdownPath, builder.ToString(), Encoding.UTF8);

            Debug.Log("[SimulationBenchmark] Result written to " + BenchmarkResultsMarkdownPath);
            Debug.Log("[SimulationBenchmark] Average FPS: " + Format(GetAverageFramesPerSecond(result)) + ". Average frame: " + Format(result.averageFrameMilliseconds) + " ms. Average process CPU/frame: " + Format(result.averageProcessCpuMillisecondsPerFrame) + " ms.");
        }

        private static void EnsureMarkdownSchema()
        {
            if (!File.Exists(BenchmarkResultsMarkdownPath))
            {
                return;
            }

            string text = File.ReadAllText(BenchmarkResultsMarkdownPath);
            if (text.Contains("Avg FPS"))
            {
                return;
            }

            string archiveName = "SimulationCpuBenchmarkResults-legacy-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".md";
            string archivePath = Path.Combine(
                Path.GetDirectoryName(BenchmarkResultsMarkdownPath),
                archiveName);

            File.WriteAllText(archivePath, text, Encoding.UTF8);

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("# Simulation CPU Benchmark Results");
            builder.AppendLine();
            builder.AppendLine("Run from `Tools/OceanViz/Run Simulation CPU Benchmark`.");
            builder.AppendLine();
            builder.AppendLine("Older rows were moved to `" + archiveName + "` when FPS-first columns were added.");
            builder.AppendLine();
            builder.AppendLine("| Date UTC | Benchmark | Location | Views | Avg FPS | Dynamic requested | Dynamic active/total | Dynamic culled | Static requested | Static active/total | Static culled | Avg frame ms | P95 frame ms | Worst frame ms | Avg process CPU/frame ms |");
            builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            File.WriteAllText(BenchmarkResultsMarkdownPath, builder.ToString(), Encoding.UTF8);
        }

        private static double GetAverageFramesPerSecond(SimulationBenchmarkResult result)
        {
            if (result.averageFramesPerSecond > 0.0)
            {
                return result.averageFramesPerSecond;
            }

            if (result.averageFrameMilliseconds <= 0.0)
            {
                return 0.0;
            }

            return 1000.0 / result.averageFrameMilliseconds;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string ReadLastLogLine()
        {
            if (!File.Exists(BenchmarkPlayerLogPath))
            {
                return "no player log found";
            }

            string[] lines = File.ReadAllLines(BenchmarkPlayerLogPath);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (!string.IsNullOrEmpty(lines[i]))
                {
                    return lines[i];
                }
            }

            return "player log was empty";
        }

        private static string ReadLastBenchmarkLogLine()
        {
            if (!File.Exists(BenchmarkPlayerLogPath))
            {
                return "no player log found";
            }

            string[] lines = File.ReadAllLines(BenchmarkPlayerLogPath);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (string.IsNullOrEmpty(lines[i]))
                {
                    continue;
                }

                if (lines[i].Contains("[SimulationBenchmark]"))
                {
                    return lines[i];
                }
            }

            return ReadLastLogLine();
        }

        private static string EscapeMarkdown(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("|", "\\|");
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
#endif
