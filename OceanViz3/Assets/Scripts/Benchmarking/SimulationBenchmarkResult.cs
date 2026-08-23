using System;

namespace OceanViz3.Benchmarking
{
    /// <summary>
    /// Machine-readable output for one simulation benchmark run.
    /// </summary>
    [Serializable]
    public class SimulationBenchmarkResult
    {
        public string benchmarkName;
        public string unityVersion;
        public string dateUtc;
        public string machineName;
        public string operatingSystem;
        public string processorType;
        public int processorCount;
        public string configPath;
        public string locationName;
        public int viewsCount;
        public int setupSettleFrames;
        public int warmupFrames;
        public int measurementFrames;
        public int maxRunSeconds;
        public bool usedAutomaticCameraTrack;
        public bool exercisedEntityHover;
        public bool timedOut;
        public string finalState;
        public int requestedDynamicEntityCount;
        public int requestedStaticEntityCount;
        public int dynamicEntityCount;
        public int dynamicEntityTotalCount;
        public int dynamicEntityDisabledOrCulledCount;
        public int staticEntityCount;
        public int staticEntityTotalCount;
        public int staticEntityDisabledOrCulledCount;
        public double averageFramesPerSecond;
        public double averageFrameMilliseconds;
        public double percentile95FrameMilliseconds;
        public double worstFrameMilliseconds;
        public double averageProcessCpuMillisecondsPerFrame;
        public double totalProcessCpuMilliseconds;
        public double totalWallClockMilliseconds;
    }
}
