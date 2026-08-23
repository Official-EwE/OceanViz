using System;

namespace OceanViz3.Benchmarking
{
    /// <summary>
    /// Defines one automated simulation benchmark run.
    /// The benchmark runner uses this to set up the scene, warm up, measure CPU cost, and write results.
    /// </summary>
    [Serializable]
    public class SimulationBenchmarkConfig
    {
        public string benchmarkName = "Simulation CPU Benchmark";
        public string locationName = "Testing";
        public int viewsCount = 1;
        public int setupSettleFrames = 240;
        public int warmupFrames = 300;
        public int measurementFrames = 900;
        public int maxRunSeconds = 30;
        public bool useAutomaticCameraTrack = false;
        public bool exerciseEntityHover = false;
        public BenchmarkGroupConfig[] groups = new BenchmarkGroupConfig[0];

        public void EnsureDefaults()
        {
            if (string.IsNullOrEmpty(benchmarkName))
            {
                benchmarkName = "Simulation CPU Benchmark";
            }

            if (string.IsNullOrEmpty(locationName))
            {
                locationName = "Testing";
            }

            if (viewsCount < 1)
            {
                viewsCount = 1;
            }

            if (viewsCount > 4)
            {
                viewsCount = 4;
            }

            if (setupSettleFrames < 1)
            {
                setupSettleFrames = 1;
            }

            if (warmupFrames < 1)
            {
                warmupFrames = 1;
            }

            if (measurementFrames < 1)
            {
                measurementFrames = 1;
            }

            if (maxRunSeconds < 1)
            {
                maxRunSeconds = 30;
            }

            if (groups == null)
            {
                groups = new BenchmarkGroupConfig[0];
            }
        }
    }

    [Serializable]
    public class BenchmarkGroupConfig
    {
        public string presetName = string.Empty;
        public string groupName = string.Empty;
        public float population = 0.1f;
        public string[] overrideHabitats = new string[0];
    }
}
