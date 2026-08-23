#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace OceanViz3.Editor
{
    /// <summary>
    /// Stores the species and locations that should be included in a trimmed OceanViz build.
    /// Selected locations are passed directly to BuildPipeline.BuildPlayer so excluded
    /// location scenes do not contribute scene dependencies to sharedassets.
    /// </summary>
    [CreateAssetMenu(fileName = "OceanViz Build Content Preset", menuName = "OceanViz/Build Content Preset")]
    public class OceanVizBuildContentPreset : ScriptableObject
    {
        public List<string> selectedDynamicEntityNames = new List<string>();
        public List<string> selectedStaticEntityNames = new List<string>();
        public List<string> selectedLocationNames = new List<string>();
    }
}
#endif
