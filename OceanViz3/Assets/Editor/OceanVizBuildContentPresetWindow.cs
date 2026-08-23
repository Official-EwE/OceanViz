#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace OceanViz3.Editor
{
    public class OceanVizBuildContentPresetWindow : EditorWindow
    {
        private OceanVizBuildContentPreset preset;
        private List<string> dynamicEntityNames = new List<string>();
        private List<string> staticEntityNames = new List<string>();
        private List<string> locationNames = new List<string>();
        private Vector2 scrollPosition;

        [MenuItem("Tools/OceanViz/Build Content Presets")]
        public static void ShowWindow()
        {
            OceanVizBuildContentPresetWindow window = GetWindow<OceanVizBuildContentPresetWindow>("Build Content Presets");
            window.RefreshCatalog();
            window.Show();
        }

        private void OnEnable()
        {
            RefreshCatalog();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                preset = (OceanVizBuildContentPreset)EditorGUILayout.ObjectField("Preset", preset, typeof(OceanVizBuildContentPreset), false);
                if (EditorGUI.EndChangeCheck())
                {
                    Repaint();
                }

                if (GUILayout.Button("New", GUILayout.Width(70)))
                {
                    CreatePresetAsset();
                }

                if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                {
                    RefreshCatalog();
                }
            }

            if (preset == null)
            {
                EditorGUILayout.HelpBox("Create or assign an OceanViz build content preset.", MessageType.Info);
                return;
            }

            EnsurePresetLists();
            EditorGUILayout.HelpBox(
                "Trimmed builds use Main plus only the selected location scenes for BuildPipeline.BuildPlayer. The shared Build Profiles scene list is not changed.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All"))
                {
                    SetAllSelections(true);
                }

                if (GUILayout.Button("Clear All"))
                {
                    SetAllSelections(false);
                }

                if (GUILayout.Button("Save"))
                {
                    SavePreset();
                }
            }

            List<string> validationErrors = OceanVizBuildContentCatalog.Validate(preset);
            if (validationErrors.Count > 0)
            {
                EditorGUILayout.HelpBox(string.Join("\n", validationErrors), MessageType.Error);
            }

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            DrawSelectionSection("Locations", locationNames, preset.selectedLocationNames);
            DrawSelectionSection("Dynamic Species", dynamicEntityNames, preset.selectedDynamicEntityNames);
            DrawSelectionSection("Static Species", staticEntityNames, preset.selectedStaticEntityNames);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(validationErrors.Count > 0))
            {
                if (GUILayout.Button("Build Trimmed Player", GUILayout.Height(32)))
                {
                    BuildTrimmedPlayer();
                }
            }
        }

        private void RefreshCatalog()
        {
            dynamicEntityNames = OceanVizBuildContentCatalog.GetDynamicEntityFolderNames();
            staticEntityNames = OceanVizBuildContentCatalog.GetStaticEntityFolderNames();
            locationNames = OceanVizBuildContentCatalog.GetLocationFolderNames();
        }

        private void CreatePresetAsset()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Create OceanViz Build Content Preset",
                "OceanViz Build Content Preset",
                "asset",
                "Choose where to save the build content preset.");

            if (string.IsNullOrEmpty(path))
            {
                Debug.Log("OceanViz build content preset creation cancelled.");
                return;
            }

            OceanVizBuildContentPreset newPreset = CreateInstance<OceanVizBuildContentPreset>();
            newPreset.selectedDynamicEntityNames = new List<string>(dynamicEntityNames);
            newPreset.selectedStaticEntityNames = new List<string>(staticEntityNames);
            newPreset.selectedLocationNames = new List<string>(locationNames);

            AssetDatabase.CreateAsset(newPreset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            preset = newPreset;
            Selection.activeObject = preset;
        }

        private void DrawSelectionSection(string title, List<string> availableNames, List<string> selectedNames)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select " + title))
                {
                    AddAll(availableNames, selectedNames);
                    SavePreset();
                }

                if (GUILayout.Button("Clear " + title))
                {
                    selectedNames.Clear();
                    SavePreset();
                }
            }

            foreach (string availableName in availableNames)
            {
                bool wasSelected = selectedNames.Contains(availableName);
                bool isSelected = EditorGUILayout.ToggleLeft(availableName, wasSelected);

                if (isSelected != wasSelected)
                {
                    SetSelected(selectedNames, availableName, isSelected);
                    SavePreset();
                }
            }
        }

        private void SetAllSelections(bool selected)
        {
            if (selected)
            {
                AddAll(dynamicEntityNames, preset.selectedDynamicEntityNames);
                AddAll(staticEntityNames, preset.selectedStaticEntityNames);
                AddAll(locationNames, preset.selectedLocationNames);
            }
            else
            {
                preset.selectedDynamicEntityNames.Clear();
                preset.selectedStaticEntityNames.Clear();
                preset.selectedLocationNames.Clear();
            }

            SavePreset();
        }

        private void AddAll(List<string> sourceNames, List<string> targetNames)
        {
            foreach (string sourceName in sourceNames)
            {
                if (!targetNames.Contains(sourceName))
                {
                    targetNames.Add(sourceName);
                }
            }

            targetNames.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private void SetSelected(List<string> selectedNames, string name, bool selected)
        {
            if (selected)
            {
                if (!selectedNames.Contains(name))
                {
                    selectedNames.Add(name);
                    selectedNames.Sort(StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                selectedNames.Remove(name);
            }
        }

        private void BuildTrimmedPlayer()
        {
            OceanVizBuildContentCatalog.ValidateOrThrow(preset);

            string outputFolder = EditorUtility.SaveFolderPanel("Choose OceanViz Build Output Folder", string.Empty, Application.productName);
            if (string.IsNullOrEmpty(outputFolder))
            {
                Debug.Log("OceanViz trimmed build cancelled.");
                return;
            }

            try
            {
                OceanVizBuildContentBuilder.Build(preset, outputFolder);
                EditorUtility.DisplayDialog("OceanViz Build", "Trimmed build completed.", "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("OceanViz Build Failed", exception.Message, "OK");
            }
        }

        private void SavePreset()
        {
            EnsurePresetLists();
            EditorUtility.SetDirty(preset);
            AssetDatabase.SaveAssets();
        }

        private void EnsurePresetLists()
        {
            if (preset.selectedDynamicEntityNames == null)
            {
                preset.selectedDynamicEntityNames = new List<string>();
            }

            if (preset.selectedStaticEntityNames == null)
            {
                preset.selectedStaticEntityNames = new List<string>();
            }

            if (preset.selectedLocationNames == null)
            {
                preset.selectedLocationNames = new List<string>();
            }
        }
    }
}
#endif
