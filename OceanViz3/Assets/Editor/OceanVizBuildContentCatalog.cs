#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace OceanViz3.Editor
{
    public static class OceanVizBuildContentCatalog
    {
        public const string MainScenePath = "Assets/Scenes/Main/Main.unity";
        public const string StreamingAssetsPath = "Assets/StreamingAssets";
        public const string DynamicEntitiesPath = StreamingAssetsPath + "/DynamicEntities";
        public const string StaticEntitiesPath = StreamingAssetsPath + "/StaticEntities";
        public const string LocationsPath = StreamingAssetsPath + "/Locations";
        public const string DynamicEntityPropertiesPath = DynamicEntitiesPath + "/entity_properties.json";
        public const string StaticEntityPropertiesPath = StaticEntitiesPath + "/entity_properties.json";
        private const string LocationScenesRoot = "Assets/Scenes/Locations";

        public static List<string> GetDynamicEntityFolderNames()
        {
            return GetDirectoryNames(DynamicEntitiesPath);
        }

        public static List<string> GetStaticEntityFolderNames()
        {
            return GetDirectoryNames(StaticEntitiesPath);
        }

        public static List<string> GetLocationFolderNames()
        {
            return GetDirectoryNames(LocationsPath);
        }

        public static HashSet<string> ReadDynamicJsonPresetNames()
        {
            return ReadJsonPresetNames(DynamicEntityPropertiesPath);
        }

        public static HashSet<string> ReadStaticJsonPresetNames()
        {
            return ReadJsonPresetNames(StaticEntityPropertiesPath);
        }

        public static string GetLocationScenePath(string locationName)
        {
            return GetLocationScenePath(locationName, true);
        }

        public static string GetLocationScenePath(string locationName, bool emitAssertions)
        {
            string[] sceneGuids = AssetDatabase.FindAssets(locationName + " t:Scene", new[] { LocationScenesRoot });
            List<string> matchingScenePaths = new List<string>();

            foreach (string sceneGuid in sceneGuids)
            {
                string scenePath = AssetDatabase.GUIDToAssetPath(sceneGuid);
                string sceneName = Path.GetFileNameWithoutExtension(scenePath);

                if (sceneName == locationName)
                {
                    matchingScenePaths.Add(scenePath);
                }
            }

            if (emitAssertions)
            {
                Debug.Assert(
                    matchingScenePaths.Count == 1,
                    "OceanVizBuildContentCatalog: Expected one scene named " + locationName + " under " + LocationScenesRoot + ", found " + matchingScenePaths.Count + ".");
            }

            if (matchingScenePaths.Count != 1)
            {
                return string.Empty;
            }

            return matchingScenePaths[0];
        }

        public static List<string> GetBuildScenePaths(OceanVizBuildContentPreset preset)
        {
            List<string> scenePaths = new List<string>();
            scenePaths.Add(MainScenePath);

            foreach (string locationName in preset.selectedLocationNames)
            {
                scenePaths.Add(GetLocationScenePath(locationName));
            }

            return scenePaths;
        }

        public static void ValidateOrThrow(OceanVizBuildContentPreset preset)
        {
            List<string> errors = Validate(preset, true);

            if (errors.Count > 0)
            {
                string errorMessage = "OceanViz build content preset is invalid:\n" + string.Join("\n", errors);
                Debug.LogError(errorMessage);
                throw new InvalidOperationException(errorMessage);
            }
        }

        public static List<string> Validate(OceanVizBuildContentPreset preset)
        {
            return Validate(preset, false);
        }

        public static List<string> Validate(OceanVizBuildContentPreset preset, bool emitAssertions)
        {
            List<string> errors = new List<string>();

            bool hasPreset = preset != null;
            if (emitAssertions)
            {
                Debug.Assert(hasPreset, "OceanVizBuildContentCatalog: Build content preset is required.");
            }

            if (!hasPreset)
            {
                errors.Add("- Build content preset is required.");
                return errors;
            }

            ValidateRequiredFile(MainScenePath, "Main scene", errors, emitAssertions);
            ValidateRequiredFile(DynamicEntityPropertiesPath, "Dynamic entity_properties.json", errors, emitAssertions);
            ValidateRequiredFile(StaticEntityPropertiesPath, "Static entity_properties.json", errors, emitAssertions);
            ValidateRequiredDirectory(DynamicEntitiesPath, "DynamicEntities folder", errors, emitAssertions);
            ValidateRequiredDirectory(StaticEntitiesPath, "StaticEntities folder", errors, emitAssertions);
            ValidateRequiredDirectory(LocationsPath, "Locations folder", errors, emitAssertions);

            if (errors.Count > 0)
            {
                return errors;
            }

            HashSet<string> dynamicFolders = new HashSet<string>(GetDynamicEntityFolderNames());
            HashSet<string> staticFolders = new HashSet<string>(GetStaticEntityFolderNames());
            HashSet<string> locationFolders = new HashSet<string>(GetLocationFolderNames());
            HashSet<string> dynamicJsonPresets = ReadJsonPresetNames(DynamicEntityPropertiesPath, emitAssertions);
            HashSet<string> staticJsonPresets = ReadJsonPresetNames(StaticEntityPropertiesPath, emitAssertions);

            bool hasLocation = preset.selectedLocationNames.Count > 0;
            if (emitAssertions)
            {
                Debug.Assert(hasLocation, "OceanVizBuildContentCatalog: Select at least one location.");
            }

            if (!hasLocation)
            {
                errors.Add("- Select at least one location.");
            }

            ValidateSelections("dynamic entity", preset.selectedDynamicEntityNames, dynamicFolders, dynamicJsonPresets, errors, emitAssertions);
            ValidateSelections("static entity", preset.selectedStaticEntityNames, staticFolders, staticJsonPresets, errors, emitAssertions);
            ValidateLocationSelections(preset.selectedLocationNames, locationFolders, errors, emitAssertions);

            return errors;
        }

        public static JArray ReadPresetArray(string jsonPath)
        {
            string json = File.ReadAllText(jsonPath);
            JArray presets = JArray.Parse(json);
            Debug.Assert(presets != null, "OceanVizBuildContentCatalog: Could not parse JSON preset array at " + jsonPath + ".");
            return presets;
        }

        private static void ValidateSelections(
            string label,
            List<string> selectedNames,
            HashSet<string> folders,
            HashSet<string> jsonPresets,
            List<string> errors,
            bool emitAssertions)
        {
            foreach (string selectedName in selectedNames)
            {
                bool hasFolder = folders.Contains(selectedName);
                if (emitAssertions)
                {
                    Debug.Assert(hasFolder, "OceanVizBuildContentCatalog: Selected " + label + " has no folder: " + selectedName + ".");
                }

                if (!hasFolder)
                {
                    errors.Add("- Selected " + label + " has no folder: " + selectedName + ".");
                }

                bool hasJsonPreset = jsonPresets.Contains(selectedName);
                if (emitAssertions)
                {
                    Debug.Assert(hasJsonPreset, "OceanVizBuildContentCatalog: Selected " + label + " has no JSON preset: " + selectedName + ".");
                }

                if (!hasJsonPreset)
                {
                    errors.Add("- Selected " + label + " has no JSON preset: " + selectedName + ".");
                }
            }
        }

        private static void ValidateLocationSelections(
            List<string> selectedNames,
            HashSet<string> folders,
            List<string> errors,
            bool emitAssertions)
        {
            foreach (string selectedName in selectedNames)
            {
                bool hasFolder = folders.Contains(selectedName);
                if (emitAssertions)
                {
                    Debug.Assert(hasFolder, "OceanVizBuildContentCatalog: Selected location has no folder: " + selectedName + ".");
                }

                if (!hasFolder)
                {
                    errors.Add("- Selected location has no folder: " + selectedName + ".");
                }

                string locationPropertiesPath = Path.Combine(LocationsPath, selectedName, "location_properties.json");
                ValidateRequiredFile(locationPropertiesPath, "Location properties for " + selectedName, errors, emitAssertions);

                string scenePath = GetLocationScenePath(selectedName, emitAssertions);
                bool hasScene = !string.IsNullOrEmpty(scenePath);
                if (emitAssertions)
                {
                    Debug.Assert(hasScene, "OceanVizBuildContentCatalog: Selected location has no matching scene: " + selectedName + ".");
                }

                if (!hasScene)
                {
                    errors.Add("- Selected location has no matching scene: " + selectedName + ".");
                }
            }
        }

        private static void ValidateRequiredFile(string path, string label, List<string> errors, bool emitAssertions)
        {
            bool exists = File.Exists(path);
            if (emitAssertions)
            {
                Debug.Assert(exists, "OceanVizBuildContentCatalog: Missing " + label + " at " + path + ".");
            }

            if (!exists)
            {
                errors.Add("- Missing " + label + " at " + path + ".");
            }
        }

        private static void ValidateRequiredDirectory(string path, string label, List<string> errors, bool emitAssertions)
        {
            bool exists = Directory.Exists(path);
            if (emitAssertions)
            {
                Debug.Assert(exists, "OceanVizBuildContentCatalog: Missing " + label + " at " + path + ".");
            }

            if (!exists)
            {
                errors.Add("- Missing " + label + " at " + path + ".");
            }
        }

        private static List<string> GetDirectoryNames(string path)
        {
            Debug.Assert(Directory.Exists(path), "OceanVizBuildContentCatalog: Missing directory " + path + ".");

            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException(path);
            }

            string[] directories = Directory.GetDirectories(path);
            List<string> names = directories.Select(Path.GetFileName).ToList();
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private static HashSet<string> ReadJsonPresetNames(string jsonPath)
        {
            return ReadJsonPresetNames(jsonPath, true);
        }

        private static HashSet<string> ReadJsonPresetNames(string jsonPath, bool emitAssertions)
        {
            JArray presets = ReadPresetArray(jsonPath);
            HashSet<string> names = new HashSet<string>();

            foreach (JToken preset in presets)
            {
                JToken nameToken = preset["name"];
                bool hasName = nameToken != null && !string.IsNullOrEmpty(nameToken.Value<string>());
                if (emitAssertions)
                {
                    Debug.Assert(hasName, "OceanVizBuildContentCatalog: JSON preset without a name in " + jsonPath + ".");
                }

                if (hasName)
                {
                    names.Add(nameToken.Value<string>());
                }
            }

            return names;
        }
    }
}
#endif
