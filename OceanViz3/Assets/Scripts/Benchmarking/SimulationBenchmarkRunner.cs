using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Entities;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace OceanViz3.Benchmarking
{
    /// <summary>
    /// Runs automated simulation CPU benchmarks in player builds.
    /// Trigger with -oceanvizBenchmark and optional -benchmarkConfig / -benchmarkOutput command-line arguments.
    /// </summary>
    public class SimulationBenchmarkRunner : MonoBehaviour
    {
        private const string BenchmarkFlag = "-oceanvizBenchmark";
        private const string BenchmarkConfigArg = "-benchmarkConfig";
        private const string BenchmarkOutputArg = "-benchmarkOutput";

        private enum BenchmarkState
        {
            WaitingForMainScene,
            ApplyingSetup,
            WaitingForLocation,
            SettlingSetup,
            WarmingUp,
            Measuring,
            Finished
        }

        private SimulationBenchmarkConfig config;
        private string configPath;
        private string outputPath;
        private BenchmarkState state;
        private MainScene mainScene;
        private int stateFrame;
        private int measuredFrameCount;
        private bool populationsApplied;
        private bool setupSettleStarted;
        private int requestedDynamicEntityCount;
        private int requestedStaticEntityCount;
        private double previousFrameTime;
        private TimeSpan previousCpuTime;
        private double[] measuredFrameMilliseconds;
        private double totalFrameMilliseconds;
        private double totalCpuMilliseconds;
        private double worstFrameMilliseconds;
        private Stopwatch wallClockStopwatch;
        private Stopwatch totalRunStopwatch;
        private Process currentProcess;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void StartFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            bool benchmarkRequested = HasArgument(args, BenchmarkFlag);
            if (!benchmarkRequested)
            {
                return;
            }

            GameObject runnerObject = new GameObject("Simulation Benchmark Runner");
            DontDestroyOnLoad(runnerObject);
            SimulationBenchmarkRunner runner = runnerObject.AddComponent<SimulationBenchmarkRunner>();
            runner.Initialize(args);
        }

        private static bool HasArgument(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetArgumentValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return string.Empty;
        }

        private void Initialize(string[] args)
        {
            configPath = GetArgumentValue(args, BenchmarkConfigArg);
            outputPath = GetArgumentValue(args, BenchmarkOutputArg);

            if (string.IsNullOrEmpty(outputPath))
            {
                outputPath = Path.Combine(Application.persistentDataPath, "simulation-cpu-benchmark-result.json");
            }

            config = LoadConfig(configPath);
            config.EnsureDefaults();
            measuredFrameMilliseconds = new double[config.measurementFrames];
            state = BenchmarkState.WaitingForMainScene;
            currentProcess = Process.GetCurrentProcess();
            wallClockStopwatch = new Stopwatch();
            totalRunStopwatch = Stopwatch.StartNew();

            Debug.Log("[SimulationBenchmark] Started. Config: " + configPath);
            Debug.Log("[SimulationBenchmark] Output: " + outputPath);
        }

        private SimulationBenchmarkConfig LoadConfig(string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                Debug.Assert(File.Exists(path), "Benchmark config file does not exist: " + path);
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    SimulationBenchmarkConfig loadedConfig = JsonUtility.FromJson<SimulationBenchmarkConfig>(json);
                    Debug.Assert(loadedConfig != null, "Benchmark config could not be parsed: " + path);
                    if (loadedConfig != null)
                    {
                        return loadedConfig;
                    }
                }
            }

            Debug.LogWarning("[SimulationBenchmark] No config file was provided. Using built-in defaults.");
            return new SimulationBenchmarkConfig();
        }

        private void Update()
        {
            if (state == BenchmarkState.Finished)
            {
                return;
            }

            UpdateSyntheticEntityHover();

            if (totalRunStopwatch.Elapsed.TotalSeconds > config.maxRunSeconds)
            {
                FinishTimedOutBenchmark();
                return;
            }

            if (state == BenchmarkState.WaitingForMainScene)
            {
                WaitForMainScene();
                return;
            }

            if (state == BenchmarkState.ApplyingSetup)
            {
                ApplySetup();
                return;
            }

            if (state == BenchmarkState.WaitingForLocation)
            {
                WaitForBenchmarkLocation();
                return;
            }

            if (state == BenchmarkState.SettlingSetup)
            {
                AdvanceSettling();
                return;
            }

            if (state == BenchmarkState.WarmingUp)
            {
                AdvanceWarmup();
                return;
            }

            if (state == BenchmarkState.Measuring)
            {
                AdvanceMeasurement();
            }
        }

        private void UpdateSyntheticEntityHover()
        {
            if (!config.exerciseEntityHover || mainScene == null || mainScene.simulationModeManager == null)
            {
                return;
            }

            int cycleFrame = (stateFrame + measuredFrameCount) % 240;
            float horizontal = Mathf.Lerp(0.1f, 0.9f, cycleFrame / 239.0f);
            Vector2 screenPosition = new Vector2(
                horizontal * Mathf.Max(1, Screen.width),
                0.5f * Mathf.Max(1, Screen.height));
            mainScene.simulationModeManager.SetSyntheticEntityHoverForBenchmark(true, screenPosition);
        }

        private void WaitForMainScene()
        {
            if (!MainScene.IsReady || !LocationScript.IsReady || GroupPresetsManager.Instance == null || !GroupPresetsManager.Instance.IsReady || !EntityLibraryIsReady())
            {
                return;
            }

            mainScene = FindFirstObjectByType<MainScene>();
            if (mainScene == null || mainScene.simulationAPI == null)
            {
                return;
            }

            Debug.Log("[SimulationBenchmark] Main scene is ready. Applying setup.");
            state = BenchmarkState.ApplyingSetup;
        }

        private void ApplySetup()
        {
            if (mainScene.currentLocationName != config.locationName)
            {
                Debug.Log("[SimulationBenchmark] Switching location to " + config.locationName + ".");
                mainScene.UnloadLocation();
                mainScene.LoadLocation(config.locationName);
                state = BenchmarkState.WaitingForLocation;
                return;
            }

            ApplySceneContentSetup();
        }

        private void WaitForBenchmarkLocation()
        {
            if (!MainScene.IsReady || !LocationScript.IsReady || mainScene.currentLocationName != config.locationName || !EntityLibraryIsReady())
            {
                return;
            }

            Debug.Log("[SimulationBenchmark] Benchmark location is ready.");
            ApplySceneContentSetup();
        }

        private bool EntityLibraryIsReady()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                return false;
            }

            EntityQuery query = world.EntityManager.CreateEntityQuery(typeof(EntityLibrary));
            int entityLibraryCount = query.CalculateEntityCount();
            query.Dispose();
            return entityLibraryCount > 0;
        }

        private void ApplySceneContentSetup()
        {
            mainScene.SetViewCountAndUpdateGUIState(config.viewsCount);
            ActivateAutomaticCameraTrackIfRequested();
            requestedDynamicEntityCount = 0;
            requestedStaticEntityCount = 0;
            populationsApplied = false;
            setupSettleStarted = false;

            for (int i = 0; i < config.groups.Length; i++)
            {
                BenchmarkGroupConfig group = config.groups[i];
                Debug.Assert(group != null, "Benchmark group config is null.");
                if (group == null)
                {
                    continue;
                }

                bool hasPresetName = !string.IsNullOrEmpty(group.presetName);
                Debug.Assert(hasPresetName, "Benchmark group presetName is required.");
                if (!hasPresetName)
                {
                    continue;
                }

                string resolvedGroupName = group.groupName;
                if (string.IsNullOrEmpty(resolvedGroupName))
                {
                    resolvedGroupName = group.presetName;
                }

                DynamicEntityPreset fixedDynamicPreset;
                bool hasFixedDynamicPreset = FixedBenchmarkDynamicPresets.TryCreate(group.presetName, out fixedDynamicPreset);
                if (hasFixedDynamicPreset)
                {
                    mainScene.simulationModeManager.SpawnDynamicBenchmarkPreset(fixedDynamicPreset, resolvedGroupName, group.overrideHabitats);
                    Debug.Log("[SimulationBenchmark] Spawned fixed benchmark dynamic preset: " + group.presetName + ".");
                }
                else
                {
                    DynamicEntityPreset dynamicPreset = GroupPresetsManager.Instance.GetDynamicPresetByName(group.presetName);
                    bool presetIsDynamic = dynamicPreset != null;
                    Debug.Assert(!presetIsDynamic, "Benchmark dynamic preset must be hardcoded: " + group.presetName);
                    if (presetIsDynamic)
                    {
                        continue;
                    }

                    bool hasHabitats = group.overrideHabitats != null && group.overrideHabitats.Length > 0;
                    StaticEntityPreset staticPreset = GroupPresetsManager.Instance.GetStaticPresetByName(group.presetName);
                    Debug.Assert(staticPreset != null, "Benchmark preset was not found as dynamic or static: " + group.presetName);
                    if (staticPreset != null)
                    {
                        if (hasHabitats)
                        {
                            _ = mainScene.simulationModeManager.SpawnStaticPresetInHabitats(group.presetName, resolvedGroupName, group.overrideHabitats);
                        }
                        else
                        {
                            _ = mainScene.SpawnStaticPreset(group.presetName, resolvedGroupName);
                        }
                    }
                }
            }

            stateFrame = 0;
            state = BenchmarkState.SettlingSetup;
            Debug.Log("[SimulationBenchmark] Setup queued. Settling for " + config.setupSettleFrames + " frames.");
        }

        private void ActivateAutomaticCameraTrackIfRequested()
        {
            if (!config.useAutomaticCameraTrack)
            {
                return;
            }

            Debug.Assert(mainScene.currentLocationScript != null, "Benchmark automatic camera requires a current location.");
            Debug.Assert(mainScene.currentLocationScript.dollyCart != null, "Benchmark automatic camera requires LocationScript.dollyCart.");
            Debug.Assert(mainScene.simulationModeManager != null, "Benchmark automatic camera requires SimulationModeManager.");

            if (mainScene.currentLocationScript == null || mainScene.currentLocationScript.dollyCart == null || mainScene.simulationModeManager == null)
            {
                Debug.LogError("[SimulationBenchmark] Automatic camera track requested, but the current location has no dolly cart.");
                return;
            }

            mainScene.simulationModeManager.ActivateAutomaticCameraMode();
            Debug.Log("[SimulationBenchmark] Automatic camera track enabled.");
        }

        private void AdvanceSettling()
        {
            if (!BenchmarkSetupIsReady())
            {
                stateFrame++;
                LogProgress("Waiting for benchmark setup", stateFrame, 0);
                return;
            }

            if (!setupSettleStarted)
            {
                Debug.Log("[SimulationBenchmark] Benchmark setup is ready. Starting settle frame count.");
                stateFrame = 0;
                setupSettleStarted = true;
            }

            stateFrame++;
            LogProgress("Settling setup", stateFrame, config.setupSettleFrames);

            if (stateFrame < config.setupSettleFrames)
            {
                return;
            }

            stateFrame = 0;
            state = BenchmarkState.WarmingUp;
            Debug.Log("[SimulationBenchmark] Warmup started for " + config.warmupFrames + " frames.");
        }

        private bool BenchmarkSetupIsReady()
        {
            if (!MainScene.IsReady || !LocationScript.IsReady || mainScene == null || mainScene.simulationModeManager == null)
            {
                return false;
            }

            ApplyBenchmarkPopulationsIfNeeded();

            int configuredGroupCount = config.groups.Length;
            int loadedGroupCount = mainScene.simulationModeManager.dynamicEntitiesGroups.Count + mainScene.simulationModeManager.staticEntitiesGroups.Count;
            if (loadedGroupCount < configuredGroupCount)
            {
                return false;
            }

            for (int i = 0; i < mainScene.simulationModeManager.dynamicEntitiesGroups.Count; i++)
            {
                DynamicEntitiesGroup group = mainScene.simulationModeManager.dynamicEntitiesGroups[i];
                if (group == null || !group.IsReady)
                {
                    return false;
                }
            }

            for (int i = 0; i < mainScene.simulationModeManager.staticEntitiesGroups.Count; i++)
            {
                StaticEntitiesGroup group = mainScene.simulationModeManager.staticEntitiesGroups[i];
                if (group == null || !group.IsReady || !group.IsPopulationStreamingReady())
                {
                    return false;
                }
            }

            if (requestedDynamicEntityCount > 0)
            {
                EntityCountSummary dynamicEntityCount = CountEntities(typeof(BoidUnique));
                if (dynamicEntityCount.ActiveCount == 0)
                {
                    return false;
                }
            }

            if (requestedStaticEntityCount > 0)
            {
                EntityCountSummary staticEntityCount = CountEntities(typeof(StaticEntityShared));
                if (staticEntityCount.ActiveCount == 0)
                {
                    return false;
                }
            }

            return true;
        }

        private void ApplyBenchmarkPopulationsIfNeeded()
        {
            if (populationsApplied)
            {
                return;
            }

            int appliedCount = 0;
            requestedDynamicEntityCount = 0;
            requestedStaticEntityCount = 0;
            for (int i = 0; i < config.groups.Length; i++)
            {
                BenchmarkGroupConfig configGroup = config.groups[i];
                string resolvedGroupName = configGroup.groupName;
                if (string.IsNullOrEmpty(resolvedGroupName))
                {
                    resolvedGroupName = configGroup.presetName;
                }

                DynamicEntitiesGroup dynamicGroup = mainScene.simulationModeManager.dynamicEntitiesGroups.Find(group => group.name == resolvedGroupName);
                if (dynamicGroup != null)
                {
                    int targetPopulation = Mathf.RoundToInt(dynamicGroup.dynamicEntityPreset.maxPopulation * Mathf.Clamp01(configGroup.population));
                    dynamicGroup.SetPopulationAndUpdateGUIState(targetPopulation);
                    requestedDynamicEntityCount += CountRequestedDynamicEntities(dynamicGroup, targetPopulation);
                    appliedCount++;
                    continue;
                }

                StaticEntitiesGroup staticGroup = mainScene.simulationModeManager.staticEntitiesGroups.Find(group => group.name == resolvedGroupName);
                if (staticGroup != null)
                {
                    int targetPopulation = Mathf.RoundToInt(staticGroup.staticEntityPreset.maxPopulation * Mathf.Clamp01(configGroup.population));
                    staticGroup.SetPopulationAndUpdateGUIState(targetPopulation);
                    requestedStaticEntityCount += CountRequestedStaticEntities(staticGroup, targetPopulation);
                    appliedCount++;
                }
            }

            if (appliedCount == config.groups.Length)
            {
                populationsApplied = true;
                Debug.Log("[SimulationBenchmark] Benchmark populations applied. Requested dynamic entities: " + requestedDynamicEntityCount + ", requested static entities: " + requestedStaticEntityCount + ".");
            }
        }

        private int CountRequestedDynamicEntities(DynamicEntitiesGroup group, int targetPopulationPerSchool)
        {
            Debug.Assert(group != null, "Counting requested dynamic entities requires a group.");
            if (group == null)
            {
                return 0;
            }

            int schoolCount = 0;
            if (group.boidSchoolStructs != null)
            {
                schoolCount = group.boidSchoolStructs.Count;
            }

            int requestedCount = targetPopulationPerSchool * schoolCount;
            Debug.Log("[SimulationBenchmark] Dynamic group '" + group.name + "' requested " + targetPopulationPerSchool + " per boid school across " + schoolCount + " schools, total " + requestedCount + ".");
            return requestedCount;
        }

        private int CountRequestedStaticEntities(StaticEntitiesGroup group, int targetPopulation)
        {
            Debug.Assert(group != null, "Counting requested static entities requires a group.");
            if (group == null)
            {
                return 0;
            }

            int groupEntityCount = 0;
            if (group.staticEntitiesGroupStructs != null)
            {
                groupEntityCount = group.staticEntitiesGroupStructs.Count;
            }

            int requestedCount = targetPopulation * groupEntityCount;
            Debug.Log("[SimulationBenchmark] Static group '" + group.name + "' requested " + targetPopulation + " per static group across " + groupEntityCount + " groups, total " + requestedCount + ".");
            return requestedCount;
        }

        private void AdvanceWarmup()
        {
            stateFrame++;
            LogProgress("Warming up", stateFrame, config.warmupFrames);

            if (stateFrame < config.warmupFrames)
            {
                return;
            }

            BeginMeasurement();
        }

        private void BeginMeasurement()
        {
            measuredFrameCount = 0;
            totalFrameMilliseconds = 0.0;
            totalCpuMilliseconds = 0.0;
            worstFrameMilliseconds = 0.0;
            previousFrameTime = Time.realtimeSinceStartupAsDouble;
            currentProcess.Refresh();
            previousCpuTime = currentProcess.TotalProcessorTime;
            wallClockStopwatch.Restart();
            state = BenchmarkState.Measuring;
            Debug.Log("[SimulationBenchmark] Measuring " + config.measurementFrames + " frames.");
        }

        private void AdvanceMeasurement()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            double frameMs = (now - previousFrameTime) * 1000.0;
            previousFrameTime = now;

            currentProcess.Refresh();
            TimeSpan currentCpuTime = currentProcess.TotalProcessorTime;
            double cpuMs = (currentCpuTime - previousCpuTime).TotalMilliseconds;
            previousCpuTime = currentCpuTime;

            measuredFrameMilliseconds[measuredFrameCount] = frameMs;
            totalFrameMilliseconds += frameMs;
            totalCpuMilliseconds += cpuMs;

            if (frameMs > worstFrameMilliseconds)
            {
                worstFrameMilliseconds = frameMs;
            }

            measuredFrameCount++;
            LogProgress("Measuring", measuredFrameCount, config.measurementFrames);

            if (measuredFrameCount < config.measurementFrames)
            {
                return;
            }

            FinishBenchmark();
        }

        private void FinishBenchmark()
        {
            wallClockStopwatch.Stop();
            SimulationBenchmarkResult result = BuildResult(false);
            WriteResult(result);
            LogSummary(result);
            state = BenchmarkState.Finished;

            if (Application.isBatchMode)
            {
                Application.Quit(0);
            }
        }

        private void FinishTimedOutBenchmark()
        {
            wallClockStopwatch.Stop();
            SimulationBenchmarkResult result = BuildResult(true);
            WriteResult(result);
            Debug.LogError("[SimulationBenchmark] Timed out after " + config.maxRunSeconds + " seconds in state " + state + ".");
            state = BenchmarkState.Finished;

            if (Application.isBatchMode)
            {
                Application.Quit(2);
            }
        }

        private SimulationBenchmarkResult BuildResult(bool timedOut)
        {
            SimulationBenchmarkResult result = new SimulationBenchmarkResult();
            result.benchmarkName = config.benchmarkName;
            result.unityVersion = Application.unityVersion;
            result.dateUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            result.machineName = Environment.MachineName;
            result.operatingSystem = SystemInfo.operatingSystem;
            result.processorType = SystemInfo.processorType;
            result.processorCount = SystemInfo.processorCount;
            result.configPath = configPath;
            result.locationName = config.locationName;
            result.viewsCount = config.viewsCount;
            result.setupSettleFrames = config.setupSettleFrames;
            result.warmupFrames = config.warmupFrames;
            result.measurementFrames = config.measurementFrames;
            result.maxRunSeconds = config.maxRunSeconds;
            result.usedAutomaticCameraTrack = config.useAutomaticCameraTrack;
            result.exercisedEntityHover = config.exerciseEntityHover;
            result.timedOut = timedOut;
            result.finalState = state.ToString();
            result.requestedDynamicEntityCount = requestedDynamicEntityCount;
            result.requestedStaticEntityCount = requestedStaticEntityCount;
            EntityCountSummary dynamicCounts = CountEntities(typeof(BoidUnique));
            EntityCountSummary staticCounts = CountEntities(typeof(StaticEntityShared));
            result.dynamicEntityCount = dynamicCounts.ActiveCount;
            result.dynamicEntityTotalCount = dynamicCounts.TotalCount;
            result.dynamicEntityDisabledOrCulledCount = dynamicCounts.DisabledOrCulledCount;
            result.staticEntityCount = staticCounts.ActiveCount;
            result.staticEntityTotalCount = staticCounts.TotalCount;
            int streamedOutStaticCount = requestedStaticEntityCount - staticCounts.TotalCount;
            if (streamedOutStaticCount < 0)
            {
                streamedOutStaticCount = 0;
            }
            result.staticEntityDisabledOrCulledCount =
                staticCounts.DisabledOrCulledCount + streamedOutStaticCount;
            if (measuredFrameCount > 0)
            {
                result.averageFrameMilliseconds = totalFrameMilliseconds / measuredFrameCount;
                result.averageFramesPerSecond = CalculateFramesPerSecond(result.averageFrameMilliseconds);
                result.percentile95FrameMilliseconds = CalculatePercentile95(measuredFrameMilliseconds, measuredFrameCount);
                result.averageProcessCpuMillisecondsPerFrame = totalCpuMilliseconds / measuredFrameCount;
            }
            else
            {
                result.averageFramesPerSecond = 0.0;
                result.averageFrameMilliseconds = 0.0;
                result.percentile95FrameMilliseconds = 0.0;
                result.averageProcessCpuMillisecondsPerFrame = 0.0;
            }
            result.worstFrameMilliseconds = worstFrameMilliseconds;
            result.totalProcessCpuMilliseconds = totalCpuMilliseconds;
            result.totalWallClockMilliseconds = wallClockStopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        private struct EntityCountSummary
        {
            public int ActiveCount;
            public int TotalCount;
            public int DisabledOrCulledCount;
        }

        private EntityCountSummary CountEntities(Type componentType)
        {
            EntityCountSummary summary = new EntityCountSummary();
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                return summary;
            }

            EntityQuery activeQuery = world.EntityManager.CreateEntityQuery(componentType);
            summary.ActiveCount = activeQuery.CalculateEntityCount();
            activeQuery.Dispose();

            EntityQueryDesc totalQueryDesc = new EntityQueryDesc();
            totalQueryDesc.All = new ComponentType[] { componentType };
            totalQueryDesc.Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IgnoreComponentEnabledState;
            EntityQuery totalQuery = world.EntityManager.CreateEntityQuery(totalQueryDesc);
            summary.TotalCount = totalQuery.CalculateEntityCount();
            totalQuery.Dispose();

            summary.DisabledOrCulledCount = summary.TotalCount - summary.ActiveCount;
            if (summary.DisabledOrCulledCount < 0)
            {
                summary.DisabledOrCulledCount = 0;
            }

            return summary;
        }

        private double CalculatePercentile95(double[] values, int count)
        {
            double[] sortedValues = new double[count];
            Array.Copy(values, sortedValues, count);
            Array.Sort(sortedValues);

            int index = (int)Math.Ceiling(count * 0.95) - 1;
            if (index < 0)
            {
                index = 0;
            }

            if (index >= count)
            {
                index = count - 1;
            }

            return sortedValues[index];
        }

        private double CalculateFramesPerSecond(double averageFrameMilliseconds)
        {
            if (averageFrameMilliseconds <= 0.0)
            {
                return 0.0;
            }

            return 1000.0 / averageFrameMilliseconds;
        }

        private void WriteResult(SimulationBenchmarkResult result)
        {
            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonUtility.ToJson(result, true);
            File.WriteAllText(outputPath, json, Encoding.UTF8);
        }

        private void LogSummary(SimulationBenchmarkResult result)
        {
            Debug.Log(
                "[SimulationBenchmark] Finished. Avg FPS " + Format(result.averageFramesPerSecond) +
                ", avg frame " + Format(result.averageFrameMilliseconds) +
                " ms, p95 " + Format(result.percentile95FrameMilliseconds) +
                " ms, worst " + Format(result.worstFrameMilliseconds) +
                " ms, avg process CPU/frame " + Format(result.averageProcessCpuMillisecondsPerFrame) +
                " ms. Dynamic requested/active/total: " + result.requestedDynamicEntityCount + "/" + result.dynamicEntityCount + "/" + result.dynamicEntityTotalCount +
                ", static requested/active/total: " + result.requestedStaticEntityCount + "/" + result.staticEntityCount + "/" + result.staticEntityTotalCount + ".");
        }

        private void LogProgress(string label, int current, int total)
        {
            if (current == total || current % 60 == 0)
            {
                if (total > 0)
                {
                    Debug.Log("[SimulationBenchmark] " + label + ": " + current + "/" + total);
                }
                else
                {
                    Debug.Log("[SimulationBenchmark] " + label + ": " + current + " frames");
                }
            }
        }

        private string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
