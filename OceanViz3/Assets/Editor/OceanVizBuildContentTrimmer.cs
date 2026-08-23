#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OceanViz3.Editor
{
    public static class OceanVizBuildContentTrimmer
    {
        public static void TrimBuiltStreamingAssets(string builtStreamingAssetsPath, OceanVizBuildContentPreset preset)
        {
            OceanVizBuildContentCatalog.ValidateOrThrow(preset);
            ValidateBuildStreamingAssetsPath(builtStreamingAssetsPath);

            TrimChildFolders(
                Path.Combine(builtStreamingAssetsPath, "DynamicEntities"),
                preset.selectedDynamicEntityNames);

            TrimChildFolders(
                Path.Combine(builtStreamingAssetsPath, "StaticEntities"),
                preset.selectedStaticEntityNames);

            TrimChildFolders(
                Path.Combine(builtStreamingAssetsPath, "Locations"),
                preset.selectedLocationNames);

            RewriteEntityProperties(
                Path.Combine(builtStreamingAssetsPath, "DynamicEntities", "entity_properties.json"),
                preset.selectedDynamicEntityNames);

            RewriteEntityProperties(
                Path.Combine(builtStreamingAssetsPath, "StaticEntities", "entity_properties.json"),
                preset.selectedStaticEntityNames);
        }

        public static void RewriteEntityProperties(string jsonPath, List<string> selectedNames)
        {
            Debug.Assert(File.Exists(jsonPath), "OceanVizBuildContentTrimmer: Missing entity_properties.json at " + jsonPath + ".");
            if (!File.Exists(jsonPath))
            {
                throw new FileNotFoundException("Missing entity_properties.json.", jsonPath);
            }

            JArray sourcePresets = OceanVizBuildContentCatalog.ReadPresetArray(jsonPath);
            HashSet<string> selectedNameSet = new HashSet<string>(selectedNames);
            JArray filteredPresets = new JArray();

            foreach (JToken sourcePreset in sourcePresets)
            {
                JToken nameToken = sourcePreset["name"];
                bool hasName = nameToken != null;
                Debug.Assert(hasName, "OceanVizBuildContentTrimmer: JSON preset without a name in " + jsonPath + ".");

                if (hasName && selectedNameSet.Contains(nameToken.Value<string>()))
                {
                    filteredPresets.Add(sourcePreset.DeepClone());
                }
            }

            using (StreamWriter streamWriter = new StreamWriter(jsonPath))
            using (JsonTextWriter jsonWriter = new JsonTextWriter(streamWriter))
            {
                jsonWriter.Indentation = 4;
                jsonWriter.IndentChar = ' ';
                filteredPresets.WriteTo(jsonWriter);
            }
        }

        private static void TrimChildFolders(string parentPath, List<string> selectedNames)
        {
            Debug.Assert(Directory.Exists(parentPath), "OceanVizBuildContentTrimmer: Missing build folder " + parentPath + ".");
            if (!Directory.Exists(parentPath))
            {
                throw new DirectoryNotFoundException(parentPath);
            }

            HashSet<string> selectedNameSet = new HashSet<string>(selectedNames);
            string[] childDirectories = Directory.GetDirectories(parentPath);

            foreach (string childDirectory in childDirectories)
            {
                string childName = Path.GetFileName(childDirectory);

                if (!selectedNameSet.Contains(childName))
                {
                    Directory.Delete(childDirectory, true);
                }
            }
        }

        private static void ValidateBuildStreamingAssetsPath(string builtStreamingAssetsPath)
        {
            Debug.Assert(!string.IsNullOrEmpty(builtStreamingAssetsPath), "OceanVizBuildContentTrimmer: Built StreamingAssets path is required.");
            if (string.IsNullOrEmpty(builtStreamingAssetsPath))
            {
                throw new ArgumentException("Built StreamingAssets path is required.", nameof(builtStreamingAssetsPath));
            }

            string fullBuiltPath = Path.GetFullPath(builtStreamingAssetsPath);
            string fullSourcePath = Path.GetFullPath(OceanVizBuildContentCatalog.StreamingAssetsPath);

            bool isSourceStreamingAssets = string.Equals(fullBuiltPath, fullSourcePath, StringComparison.OrdinalIgnoreCase);
            bool isInsideSourceStreamingAssets = fullBuiltPath.StartsWith(fullSourcePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            Debug.Assert(!isSourceStreamingAssets && !isInsideSourceStreamingAssets, "OceanVizBuildContentTrimmer: Refusing to trim source StreamingAssets at " + fullBuiltPath + ".");
            if (isSourceStreamingAssets || isInsideSourceStreamingAssets)
            {
                throw new InvalidOperationException("Refusing to trim source StreamingAssets at " + fullBuiltPath + ".");
            }

            Debug.Assert(Directory.Exists(fullBuiltPath), "OceanVizBuildContentTrimmer: Built StreamingAssets folder does not exist: " + fullBuiltPath + ".");
            if (!Directory.Exists(fullBuiltPath))
            {
                throw new DirectoryNotFoundException(fullBuiltPath);
            }
        }
    }
}
#endif
