using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using Unity.Entities;
using Unity.VisualScripting;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Collections;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using System.Linq;
using Cinemachine;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Unity.Rendering;

namespace OceanViz3
{
    /// <summary>
    /// Represents a group whose per-view visibility and size can be edited through the shared Views Setup popup.
    /// </summary>
    internal interface IViewsSetupTarget
    {
        string DisplayName { get; }

        event Action ViewsSetupChanged;

        int[] GetViewVisibilityPercentagesCopy();
        float[] GetViewSizeMultipliersCopy();

        void SetViewVisibilityPercentage(int viewIndex, int value);
        void SetViewSizeMultiplier(int viewIndex, float value);
    }

    public class SimulationModeManager : AppModeManager
    {
        [Header("UI")]
        public GameObject mainGUIUIDocument;
        
        [Header("Scene Objects")]
        public GameObject cameraRig;
        
        // Fields moved from MainScene
        /// <summary>
        /// List of active views in the scene.
        /// </summary>
        public List<View> views = new List<View>();
        public List<float> turbidityPerView = new List<float> { 0.5f, 0.5f, 0.5f, 0.5f };
        
        /// <summary>
        /// List of active dynamic entity groups (e.g., fish schools) in the scene.
        /// </summary>
        [SerializeField]
        public List<DynamicEntitiesGroup> dynamicEntitiesGroups = new List<DynamicEntitiesGroup>();
        
        /// <summary>
        /// Unique identifier counter for dynamic entity groups.
        /// </summary>
        private int nextDynamicEntityGroupId = 0;
        private int nextStaticEntityGroupId = 0;
        
        /// <summary>
        /// List of active static entity groups (e.g., coral, seaweed) in the scene.
        /// </summary>
        [SerializeField]
        public List<StaticEntitiesGroup> staticEntitiesGroups = new List<StaticEntitiesGroup>();

        //// UI
        public VisualElement mainGui;
        private DropdownField addDynamicRowDropdownField;
        private DropdownField addStaticRowDropdownField;
        private DropdownField locationsDropdownField;
        private VisualElement addDynamicRowButton;
        private VisualElement addStaticRowButton;
        private EventCallback<ChangeEvent<string>> dynamicSpeciesChangeCallback;
        private EventCallback<ChangeEvent<string>> staticSpeciesChangeCallback;
        private readonly HashSet<string> availableDynamicSpeciesNames = new HashSet<string>();
        private readonly HashSet<string> availableStaticSpeciesNames = new HashSet<string>();
        public VisualTreeAsset DynamicGroupDataRow;
        public VisualTreeAsset StaticGroupDataRow;
        public VisualTreeAsset ViewsSetupPopup;

        private VisualElement viewsSetupPopupContainer;
        private VisualElement viewsSetupPopupRoot;
        private VisualElement viewsSetupPopupCard;
        private Label viewsSetupTitleLabel;
        private Button closeViewsSetupButton;
        private readonly List<SliderInt> viewsSetupSliders = new List<SliderInt>();
        private readonly List<Label> viewsSetupSliderValueLabels = new List<Label>();
        private readonly List<Slider> viewsSetupSizeSliders = new List<Slider>();
        private readonly List<Label> viewsSetupSizeSliderValueLabels = new List<Label>();
        private IViewsSetupTarget activeViewsSetupTarget;
        private Toggle lodDebugEnabledToggle;
        private DropdownField lodForceDropdownField;
        private Slider lod1DistanceSlider;
        private Slider lod2DistanceSlider;
        private Label lod1DistanceValueLabel;
        private Label lod2DistanceValueLabel;
        private Label lodDebugStatusLabel;
        private Button lodDebugHeaderButton;
        private VisualElement lodDebugContent;
        private bool lodDebugPanelOpen;
        private Entity lodDebugSettingsEntity;
        
        private EventCallback<ChangeEvent<string>> locationChangeCallback;

        private EntityManager entityManager;
        private bool isHudless;
        private bool isAutomaticCameraModeActive;
        private Button playSimulationButton;
        private Button pauseSimulationButton;
        private bool isSimulationPaused;
        private EntityHoverController entityHoverController;
        private SceneSetupFileDialogs sceneSetupFileDialogs;
        private bool useSyntheticEntityHover;
        private Vector2 syntheticEntityHoverScreenPosition;

        public override void Setup(MainScene mainScene)
        {
            base.Setup(mainScene);
            
            entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;

            mainGui = mainGUIUIDocument.GetComponent<UIDocument>().rootVisualElement;
            entityHoverController = new EntityHoverController();
            entityHoverController.Setup(mainScene, this, mainGui);
            sceneSetupFileDialogs = new SceneSetupFileDialogs(mainGui);

            // Main Menu Button
            var mainMenuButton = mainGui.Q<Button>("MainMenuButton");
            mainMenuButton.RegisterCallback<ClickEvent>((evt) => this.mainScene.ToggleMainMenu());

            SetupSimulationPlaybackControls();

            // LocationsDropdownField
            locationsDropdownField = mainGui.Q<DropdownField>("LocationsDropdownField");
            locationsDropdownField.choices.Clear();
            for (int i = 0; i < mainScene.locationNames.Count; i++)
            {
                locationsDropdownField.choices.Add(mainScene.locationNames[i]);
            }
            if (mainScene.locationNames.Count > 0)
            {
                locationsDropdownField.value = mainScene.currentLocationName;
            }
            locationChangeCallback = (evt) => OnLocationLocationDropdownFieldChanged(evt.newValue);
            locationsDropdownField.RegisterCallback(locationChangeCallback);
            
            //// Presets
            if (DynamicGroupDataRow == null){Debug.LogError("[SimulationModeManager] DynamicGroupDataRow is null");}
            if (StaticGroupDataRow == null){Debug.LogError("[SimulationModeManager] StaticGroupDataRow is null");}
            if (ViewsSetupPopup == null){Debug.LogError("[SimulationModeManager] ViewsSetupPopup is null");}

            SetupViewsSetupPopup();

            // Read StreamingAssets folder to populate the presets lists
            GroupPresetsManager.Instance.UpdatePresets();
            
            addDynamicRowDropdownField = mainGui.Q<DropdownField>("AddDynamicRowDropdownField");
            addStaticRowDropdownField = mainGui.Q<DropdownField>("AddStaticRowDropdownField");

            // Clear existing choices to avoid duplicates
            addDynamicRowDropdownField.choices.Clear();
            addStaticRowDropdownField.choices.Clear();

            // Populate the dropdowns with preset names
            foreach (DynamicEntityPreset dynamicEntityPreset in GroupPresetsManager.Instance.dynamicEntitiesPresetsList)
            {
                addDynamicRowDropdownField.choices.Add(dynamicEntityPreset.name);
            }
            foreach (StaticEntityPreset staticEntityPreset in GroupPresetsManager.Instance.staticEntitiesPresetsList)
            {
                addStaticRowDropdownField.choices.Add(staticEntityPreset.name);
            }

            addDynamicRowDropdownField.formatListItemCallback = FormatDynamicSpeciesChoice;
            addDynamicRowDropdownField.formatSelectedValueCallback = FormatDynamicSpeciesChoice;
            addStaticRowDropdownField.formatListItemCallback = FormatStaticSpeciesChoice;
            addStaticRowDropdownField.formatSelectedValueCallback = FormatStaticSpeciesChoice;

            // Set the default value of the dropdown to the first element of the choices list
            addDynamicRowDropdownField.value = addDynamicRowDropdownField.choices[0];
            addStaticRowDropdownField.value = addStaticRowDropdownField.choices[0];

            // AddRowButton
            addDynamicRowButton = mainGui.Q<Button>("AddDynamicRowButton");
            addDynamicRowButton.RegisterCallback<ClickEvent>((evt) => SpawnSelectedDynamicPreset());
            addStaticRowButton = mainGui.Q<Button>("AddStaticRowButton");
            addStaticRowButton.RegisterCallback<ClickEvent>((evt) => SpawnSelectedStaticPreset());
            dynamicSpeciesChangeCallback = (evt) => RefreshSpeciesAddButtonStates();
            staticSpeciesChangeCallback = (evt) => RefreshSpeciesAddButtonStates();
            addDynamicRowDropdownField.RegisterValueChangedCallback(dynamicSpeciesChangeCallback);
            addStaticRowDropdownField.RegisterValueChangedCallback(staticSpeciesChangeCallback);
            MeshHabitatSetupSystem.AvailabilityChanged -= OnMeshHabitatAvailabilityChanged;
            MeshHabitatSetupSystem.AvailabilityChanged += OnMeshHabitatAvailabilityChanged;
            RefreshSpeciesAvailabilityUI();

            // ViewCount buttons callbacks
            mainGui.Q<Button>("SetViews1").RegisterCallback<ClickEvent>((evt) => SetViewCountAndUpdateGUIState(1));
            mainGui.Q<Button>("SetViews2").RegisterCallback<ClickEvent>((evt) => SetViewCountAndUpdateGUIState(2));
            mainGui.Q<Button>("SetViews3").RegisterCallback<ClickEvent>((evt) => SetViewCountAndUpdateGUIState(3));
            mainGui.Q<Button>("SetViews4").RegisterCallback<ClickEvent>((evt) => SetViewCountAndUpdateGUIState(4));

            SetupLODDebugPanel();
            
            // Swim mode
            mainGui.Q<Button>("ActivateSwimModeButton").RegisterCallback<ClickEvent>((evt) => ActivateSwimMode());
            
            // Automatic camera mode
            mainGui.Q<Button>("ActivateAutomaticCameraModeButton").RegisterCallback<ClickEvent>((evt) => ActivateAutomaticCameraMode());

            // Save/Load scene setup
            Button saveSceneButton = mainGui.Q<Button>("SaveScene");
            Debug.Assert(saveSceneButton != null, "[SimulationModeManager] SaveScene button not found in UI.");
            if (saveSceneButton != null)
            {
                saveSceneButton.RegisterCallback<ClickEvent>((evt) => SaveSceneSetup());
            }

            Button loadSceneButton = mainGui.Q<Button>("LoadScene");
            Debug.Assert(loadSceneButton != null, "[SimulationModeManager] LoadScene button not found in UI.");
            if (loadSceneButton != null)
            {
                loadSceneButton.RegisterCallback<ClickEvent>((evt) => LoadSceneSetup());
            }
        }

        /// <summary>
        /// Connects the top-screen Play and Pause buttons to the shared Unity simulation clock.
        /// </summary>
        private void SetupSimulationPlaybackControls()
        {
            playSimulationButton = mainGui.Q<Button>("PlaySimulationButton");
            pauseSimulationButton = mainGui.Q<Button>("PauseSimulationButton");
            Debug.Assert(playSimulationButton != null, "[SimulationModeManager] PlaySimulationButton not found in UI.");
            Debug.Assert(pauseSimulationButton != null, "[SimulationModeManager] PauseSimulationButton not found in UI.");

            playSimulationButton.generateVisualContent += DrawPlaySimulationIcon;
            pauseSimulationButton.generateVisualContent += DrawPauseSimulationIcon;
            playSimulationButton.clicked += PlaySimulation;
            pauseSimulationButton.clicked += PauseSimulation;
            SetSimulationPaused(false);
        }

        private void DrawPlaySimulationIcon(MeshGenerationContext context)
        {
            Rect contentRect = playSimulationButton.contentRect;
            Vector2 center = contentRect.center;
            Painter2D painter = context.painter2D;
            painter.fillColor = Color.white;
            painter.BeginPath();
            painter.MoveTo(new Vector2(center.x - 3.5f, center.y - 5.0f));
            painter.LineTo(new Vector2(center.x + 4.5f, center.y));
            painter.LineTo(new Vector2(center.x - 3.5f, center.y + 5.0f));
            painter.ClosePath();
            painter.Fill();
        }

        private void DrawPauseSimulationIcon(MeshGenerationContext context)
        {
            Rect contentRect = pauseSimulationButton.contentRect;
            Vector2 center = contentRect.center;
            Painter2D painter = context.painter2D;
            painter.fillColor = Color.white;
            DrawFilledRectangle(painter, center.x - 4.5f, center.y - 5.0f, 3.0f, 10.0f);
            DrawFilledRectangle(painter, center.x + 1.5f, center.y - 5.0f, 3.0f, 10.0f);
        }

        private static void DrawFilledRectangle(Painter2D painter, float x, float y, float width, float height)
        {
            painter.BeginPath();
            painter.MoveTo(new Vector2(x, y));
            painter.LineTo(new Vector2(x + width, y));
            painter.LineTo(new Vector2(x + width, y + height));
            painter.LineTo(new Vector2(x, y + height));
            painter.ClosePath();
            painter.Fill();
        }

        private void PlaySimulation()
        {
            SetSimulationPaused(false);
        }

        private void PauseSimulation()
        {
            SetSimulationPaused(true);
        }

        private void SetSimulationPaused(bool paused)
        {
            isSimulationPaused = paused;
            if (isSimulationPaused)
            {
                Time.timeScale = 0.0f;
            }
            else
            {
                Time.timeScale = 1.0f;
            }

            playSimulationButton.SetEnabled(isSimulationPaused);
            pauseSimulationButton.SetEnabled(!isSimulationPaused);
        }

        private void SetupLODDebugPanel()
        {
            if (!ShouldShowLODDebugPanel())
            {
                return;
            }

            EnsureLODDebugSettingsEntity();

            VisualElement dataFeeder = mainGui.Q<VisualElement>("DataFeeder");
            Debug.Assert(dataFeeder != null, "[SimulationModeManager] DataFeeder not found in Simulation UI.");
            if (dataFeeder == null)
            {
                return;
            }

            LODDebugSettings settings = GetLODDebugSettings();

            VisualElement panel = new VisualElement();
            panel.name = "LODDebugPanel";
            panel.style.flexGrow = 0;
            panel.style.flexShrink = 0;
            panel.style.paddingTop = 4;
            panel.style.paddingRight = 4;
            panel.style.paddingBottom = 4;
            panel.style.paddingLeft = 4;
            panel.style.borderTopWidth = 1;
            panel.style.borderTopColor = new Color(0.12f, 0.12f, 0.12f, 1.0f);

            lodDebugHeaderButton = new Button(ToggleLODDebugPanel);
            lodDebugHeaderButton.style.unityFontStyleAndWeight = FontStyle.Bold;
            lodDebugHeaderButton.style.unityTextAlign = TextAnchor.MiddleCenter;
            panel.Add(lodDebugHeaderButton);

            lodDebugContent = new VisualElement();
            panel.Add(lodDebugContent);

            lodDebugEnabledToggle = new Toggle("Override LOD distances");
            lodDebugEnabledToggle.SetValueWithoutNotify(settings.DebugOverridesEnabled);
            lodDebugEnabledToggle.RegisterValueChangedCallback(OnLODDebugEnabledChanged);
            lodDebugContent.Add(lodDebugEnabledToggle);

            lodForceDropdownField = new DropdownField("Force");
            lodForceDropdownField.choices = new List<string> { "Auto", "LOD 0", "LOD 1", "LOD 2" };
            lodForceDropdownField.SetValueWithoutNotify(GetLODForceDropdownValue(settings.ForcedLOD));
            lodForceDropdownField.RegisterValueChangedCallback(OnLODForceDropdownChanged);
            lodDebugContent.Add(lodForceDropdownField);

            lod1DistanceSlider = new Slider("LOD1", 0.0f, 200.0f);
            lod1DistanceSlider.SetValueWithoutNotify(settings.LOD1Distance);
            lod1DistanceSlider.RegisterValueChangedCallback(OnLOD1DistanceChanged);
            lodDebugContent.Add(lod1DistanceSlider);

            lod1DistanceValueLabel = new Label();
            lodDebugContent.Add(lod1DistanceValueLabel);

            lod2DistanceSlider = new Slider("LOD2", 0.0f, 300.0f);
            lod2DistanceSlider.SetValueWithoutNotify(settings.LOD2Distance);
            lod2DistanceSlider.RegisterValueChangedCallback(OnLOD2DistanceChanged);
            lodDebugContent.Add(lod2DistanceSlider);

            lod2DistanceValueLabel = new Label();
            lodDebugContent.Add(lod2DistanceValueLabel);

            lodDebugStatusLabel = new Label();
            lodDebugStatusLabel.style.whiteSpace = WhiteSpace.Normal;
            lodDebugStatusLabel.style.fontSize = 10;
            lodDebugContent.Add(lodDebugStatusLabel);

            Button resetButton = new Button(ResetLODDebugSettings);
            resetButton.text = "Reset LOD Debug";
            lodDebugContent.Add(resetButton);

            Label note = new Label("Dynamic ECS boids only. Static LOD switching is not implemented.");
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.fontSize = 10;
            lodDebugContent.Add(note);

            RefreshLODDebugValueLabels(settings);
            SetLODDebugPanelOpen(false);
            dataFeeder.Add(panel);
        }

        private bool ShouldShowLODDebugPanel()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return true;
#else
            return false;
#endif
        }

        private void ToggleLODDebugPanel()
        {
            SetLODDebugPanelOpen(!lodDebugPanelOpen);
        }

        private void SetLODDebugPanelOpen(bool open)
        {
            lodDebugPanelOpen = open;

            if (lodDebugContent != null)
            {
                if (lodDebugPanelOpen)
                {
                    lodDebugContent.style.display = DisplayStyle.Flex;
                }
                else
                {
                    lodDebugContent.style.display = DisplayStyle.None;
                }
            }

            if (lodDebugHeaderButton != null)
            {
                if (lodDebugPanelOpen)
                {
                    lodDebugHeaderButton.text = "LOD Debug v";
                }
                else
                {
                    lodDebugHeaderButton.text = "LOD Debug >";
                }
            }
        }

        private void EnsureLODDebugSettingsEntity()
        {
            Debug.Assert(entityManager != null, "[SimulationModeManager] EntityManager is null; cannot setup LOD debug settings.");
            if (entityManager == null)
            {
                return;
            }

            if (lodDebugSettingsEntity != Entity.Null && entityManager.Exists(lodDebugSettingsEntity))
            {
                return;
            }

            EntityQuery query = entityManager.CreateEntityQuery(typeof(LODDebugSettings));
            int settingsCount = query.CalculateEntityCount();
            if (settingsCount == 0)
            {
                lodDebugSettingsEntity = entityManager.CreateEntity(typeof(LODDebugSettings));
                entityManager.SetName(lodDebugSettingsEntity, "LOD Debug Settings");
                entityManager.SetComponentData(lodDebugSettingsEntity, LODDebugSettings.CreateDefault());
                query.Dispose();
                return;
            }

            if (settingsCount == 1)
            {
                lodDebugSettingsEntity = query.GetSingletonEntity();
                query.Dispose();
                return;
            }

            Debug.LogError("[SimulationModeManager] Multiple LODDebugSettings singletons found.");
            Debug.Assert(false, "[SimulationModeManager] Multiple LODDebugSettings singletons found.");
            NativeArray<Entity> settingsEntities = query.ToEntityArray(Allocator.Temp);
            lodDebugSettingsEntity = settingsEntities[0];
            settingsEntities.Dispose();
            query.Dispose();
        }

        private LODDebugSettings GetLODDebugSettings()
        {
            EnsureLODDebugSettingsEntity();
            Debug.Assert(lodDebugSettingsEntity != Entity.Null, "[SimulationModeManager] LOD debug settings entity is missing.");
            if (lodDebugSettingsEntity == Entity.Null)
            {
                return LODDebugSettings.CreateDefault();
            }

            return entityManager.GetComponentData<LODDebugSettings>(lodDebugSettingsEntity);
        }

        private void SetLODDebugSettings(LODDebugSettings settings)
        {
            EnsureLODDebugSettingsEntity();
            Debug.Assert(lodDebugSettingsEntity != Entity.Null, "[SimulationModeManager] LOD debug settings entity is missing.");
            if (lodDebugSettingsEntity == Entity.Null)
            {
                return;
            }

            entityManager.SetComponentData(lodDebugSettingsEntity, settings);
        }

        private void OnLODDebugEnabledChanged(ChangeEvent<bool> evt)
        {
            LODDebugSettings settings = GetLODDebugSettings();
            settings.DebugOverridesEnabled = evt.newValue;
            SetLODDebugSettings(settings);
            RefreshLODDebugValueLabels(settings);
            Debug.Log("[SimulationModeManager] LOD distance overrides " + GetEnabledText(settings.DebugOverridesEnabled) + ".");
        }

        private void OnLODForceDropdownChanged(ChangeEvent<string> evt)
        {
            LODDebugSettings settings = GetLODDebugSettings();
            settings.ForcedLOD = GetForcedLODFromDropdownValue(evt.newValue);
            SetLODDebugSettings(settings);
            RefreshLODDebugValueLabels(settings);
            Debug.Log("[SimulationModeManager] LOD force mode set to " + GetLODForceDropdownValue(settings.ForcedLOD) + ". Dynamic entities without that LOD asset will clamp to their highest available LOD.");
        }

        private void OnLOD1DistanceChanged(ChangeEvent<float> evt)
        {
            LODDebugSettings settings = GetLODDebugSettings();
            settings.LOD1Distance = Mathf.Max(0.0f, evt.newValue);
            if (settings.LOD2Distance < settings.LOD1Distance)
            {
                settings.LOD2Distance = settings.LOD1Distance;
                if (lod2DistanceSlider != null)
                {
                    lod2DistanceSlider.SetValueWithoutNotify(settings.LOD2Distance);
                }
            }

            SetLODDebugSettings(settings);
            RefreshLODDebugValueLabels(settings);
            Debug.Log("[SimulationModeManager] LOD1 debug distance set to " + settings.LOD1Distance.ToString("F1") + "m.");
        }

        private void OnLOD2DistanceChanged(ChangeEvent<float> evt)
        {
            LODDebugSettings settings = GetLODDebugSettings();
            settings.LOD2Distance = Mathf.Max(0.0f, evt.newValue);
            if (settings.LOD2Distance < settings.LOD1Distance)
            {
                settings.LOD2Distance = settings.LOD1Distance;
                if (lod2DistanceSlider != null)
                {
                    lod2DistanceSlider.SetValueWithoutNotify(settings.LOD2Distance);
                }
            }

            SetLODDebugSettings(settings);
            RefreshLODDebugValueLabels(settings);
            Debug.Log("[SimulationModeManager] LOD2 debug distance set to " + settings.LOD2Distance.ToString("F1") + "m.");
        }

        private void ResetLODDebugSettings()
        {
            LODDebugSettings settings = LODDebugSettings.CreateDefault();
            SetLODDebugSettings(settings);

            if (lodDebugEnabledToggle != null)
            {
                lodDebugEnabledToggle.SetValueWithoutNotify(settings.DebugOverridesEnabled);
            }

            if (lodForceDropdownField != null)
            {
                lodForceDropdownField.SetValueWithoutNotify(GetLODForceDropdownValue(settings.ForcedLOD));
            }

            if (lod1DistanceSlider != null)
            {
                lod1DistanceSlider.SetValueWithoutNotify(settings.LOD1Distance);
            }

            if (lod2DistanceSlider != null)
            {
                lod2DistanceSlider.SetValueWithoutNotify(settings.LOD2Distance);
            }

            RefreshLODDebugValueLabels(settings);
            Debug.Log("[SimulationModeManager] LOD debug settings reset.");
        }

        private string GetLODForceDropdownValue(int forcedLOD)
        {
            if (forcedLOD == 0)
            {
                return "LOD 0";
            }

            if (forcedLOD == 1)
            {
                return "LOD 1";
            }

            if (forcedLOD == 2)
            {
                return "LOD 2";
            }

            return "Auto";
        }

        private int GetForcedLODFromDropdownValue(string value)
        {
            if (value == "LOD 0")
            {
                return 0;
            }

            if (value == "LOD 1")
            {
                return 1;
            }

            if (value == "LOD 2")
            {
                return 2;
            }

            return LODDebugSettings.AutoLOD;
        }

        private void RefreshLODDebugValueLabels(LODDebugSettings settings)
        {
            if (lod1DistanceValueLabel != null)
            {
                lod1DistanceValueLabel.text = "LOD1 starts after " + settings.LOD1Distance.ToString("F1") + "m";
            }

            if (lod2DistanceValueLabel != null)
            {
                lod2DistanceValueLabel.text = "LOD2 starts after " + settings.LOD2Distance.ToString("F1") + "m";
            }

            if (lodDebugStatusLabel != null)
            {
                string distanceMode = "Default distances";
                if (settings.DebugOverridesEnabled)
                {
                    distanceMode = "Debug distances";
                }

                lodDebugStatusLabel.text = "Force: " + GetLODForceDropdownValue(settings.ForcedLOD) + ". " + distanceMode + ". Force works without enabling distance overrides.";
            }
        }

        private string GetEnabledText(bool enabled)
        {
            if (enabled)
            {
                return "enabled";
            }

            return "disabled";
        }

        private void SetupViewsSetupPopup()
        {
            Debug.Assert(mainGui != null, "[SimulationModeManager] mainGui must exist before setting up the Views Setup popup.");
            Debug.Assert(ViewsSetupPopup != null, "[SimulationModeManager] ViewsSetupPopup asset is not assigned.");
            if (mainGui == null || ViewsSetupPopup == null)
            {
                return;
            }

            viewsSetupPopupContainer = ViewsSetupPopup.CloneTree();
            viewsSetupPopupContainer.style.position = Position.Absolute;
            viewsSetupPopupContainer.style.left = 0;
            viewsSetupPopupContainer.style.top = 0;
            viewsSetupPopupContainer.style.right = 0;
            viewsSetupPopupContainer.style.bottom = 0;
            viewsSetupPopupContainer.pickingMode = PickingMode.Ignore;
            mainGui.Add(viewsSetupPopupContainer);

            viewsSetupPopupRoot = viewsSetupPopupContainer.Q<VisualElement>("ViewsSetupPopupRoot");
            Debug.Assert(viewsSetupPopupRoot != null, "[SimulationModeManager] ViewsSetupPopupRoot not found.");
            if (viewsSetupPopupRoot == null)
            {
                return;
            }
            viewsSetupPopupRoot.pickingMode = PickingMode.Ignore;

            viewsSetupPopupCard = viewsSetupPopupRoot.Q<VisualElement>("ViewsSetupPopupCard");
            Debug.Assert(viewsSetupPopupCard != null, "[SimulationModeManager] ViewsSetupPopupCard not found.");
            if (viewsSetupPopupCard != null)
            {
                viewsSetupPopupCard.pickingMode = PickingMode.Position;
            }

            viewsSetupTitleLabel = viewsSetupPopupRoot.Q<Label>("ViewsSetupTitleLabel");
            Debug.Assert(viewsSetupTitleLabel != null, "[SimulationModeManager] ViewsSetupTitleLabel not found.");

            closeViewsSetupButton = viewsSetupPopupRoot.Q<Button>("CloseViewsSetupButton");
            Debug.Assert(closeViewsSetupButton != null, "[SimulationModeManager] CloseViewsSetupButton not found.");
            if (closeViewsSetupButton != null)
            {
                closeViewsSetupButton.RegisterCallback<ClickEvent>((evt) => CloseViewsSetupPopup());
            }

            viewsSetupSliders.Clear();
            viewsSetupSliderValueLabels.Clear();
            viewsSetupSizeSliders.Clear();
            viewsSetupSizeSliderValueLabels.Clear();
            for (int i = 0; i < 4; i++)
            {
                SliderInt slider = viewsSetupPopupRoot.Q<SliderInt>("PopulationPercentageSliderInt" + i);
                Debug.Assert(slider != null, "[SimulationModeManager] Views Setup slider not found for index " + i);
                if (slider == null)
                {
                    continue;
                }

                int sliderIndex = i;
                slider.RegisterValueChangedCallback((evt) => OnViewsSetupSliderChanged(sliderIndex, evt));
                viewsSetupSliders.Add(slider);

                Label sliderValueLabel = viewsSetupPopupRoot.Q<Label>("PopulationPercentageValueLabel" + i);
                Debug.Assert(sliderValueLabel != null, "[SimulationModeManager] Views Setup slider value label not found for index " + i);
                viewsSetupSliderValueLabels.Add(sliderValueLabel);

                Slider sizeSlider = viewsSetupPopupRoot.Q<Slider>("ViewSizeSlider" + i);
                Debug.Assert(sizeSlider != null, "[SimulationModeManager] Views Setup size slider not found for index " + i);
                if (sizeSlider == null)
                {
                    continue;
                }

                sizeSlider.RegisterValueChangedCallback((evt) => OnViewsSetupSizeSliderChanged(sliderIndex, evt));
                viewsSetupSizeSliders.Add(sizeSlider);

                Label sizeSliderValueLabel = viewsSetupPopupRoot.Q<Label>("ViewSizeValueLabel" + i);
                Debug.Assert(sizeSliderValueLabel != null, "[SimulationModeManager] Views Setup size slider value label not found for index " + i);
                viewsSetupSizeSliderValueLabels.Add(sizeSliderValueLabel);
            }

            viewsSetupPopupRoot.style.display = DisplayStyle.None;
        }

        private void RegisterViewsSetupButton(VisualElement dataRow, IViewsSetupTarget target)
        {
            Debug.Assert(dataRow != null, "[SimulationModeManager] Cannot register Views Setup button for a null row.");
            Debug.Assert(target != null, "[SimulationModeManager] Cannot register Views Setup button for a null target.");
            if (dataRow == null || target == null)
            {
                return;
            }

            Button viewsSetupButton = dataRow.Q<Button>("ViewsSetupButton");
            Debug.Assert(viewsSetupButton != null, "[SimulationModeManager] ViewsSetupButton not found in row.");
            if (viewsSetupButton == null)
            {
                return;
            }

            viewsSetupButton.RegisterCallback<ClickEvent>((evt) => OpenViewsSetupPopup(target));
        }

        private void OnViewsSetupSliderChanged(int viewIndex, ChangeEvent<int> evt)
        {
            Debug.Assert(activeViewsSetupTarget != null, "[SimulationModeManager] Cannot update popup slider without an active target.");
            if (activeViewsSetupTarget == null)
            {
                return;
            }

            SetViewsSetupSliderValueLabel(viewIndex, evt.newValue);
            activeViewsSetupTarget.SetViewVisibilityPercentage(viewIndex, evt.newValue);
        }

        private void OnViewsSetupSizeSliderChanged(int viewIndex, ChangeEvent<float> evt)
        {
            Debug.Assert(activeViewsSetupTarget != null, "[SimulationModeManager] Cannot update popup size slider without an active target.");
            if (activeViewsSetupTarget == null)
            {
                return;
            }

            SetViewsSetupSizeSliderValueLabel(viewIndex, evt.newValue);
            activeViewsSetupTarget.SetViewSizeMultiplier(viewIndex, evt.newValue);
        }

        private void SetViewsSetupSliderValueLabel(int viewIndex, int value)
        {
            if (viewIndex < 0 || viewIndex >= viewsSetupSliderValueLabels.Count)
            {
                return;
            }

            Label valueLabel = viewsSetupSliderValueLabels[viewIndex];
            if (valueLabel != null)
            {
                valueLabel.text = value + "%";
            }
        }

        private void SetViewsSetupSizeSliderValueLabel(int viewIndex, float value)
        {
            if (viewIndex < 0 || viewIndex >= viewsSetupSizeSliderValueLabels.Count)
            {
                return;
            }

            Label valueLabel = viewsSetupSizeSliderValueLabels[viewIndex];
            if (valueLabel != null)
            {
                valueLabel.text = value.ToString("0.00") + "x";
            }
        }

        private void OpenViewsSetupPopup(IViewsSetupTarget target)
        {
            Debug.Assert(target != null, "[SimulationModeManager] Cannot open Views Setup popup for a null target.");
            Debug.Assert(viewsSetupPopupRoot != null, "[SimulationModeManager] Views Setup popup has not been created.");
            if (target == null || viewsSetupPopupRoot == null)
            {
                return;
            }

            if (activeViewsSetupTarget != null)
            {
                activeViewsSetupTarget.ViewsSetupChanged -= RefreshViewsSetupPopup;
            }

            activeViewsSetupTarget = target;
            activeViewsSetupTarget.ViewsSetupChanged += RefreshViewsSetupPopup;
            viewsSetupPopupRoot.style.display = DisplayStyle.Flex;
            RefreshViewsSetupPopup();
        }

        private void CloseViewsSetupPopup()
        {
            if (activeViewsSetupTarget != null)
            {
                activeViewsSetupTarget.ViewsSetupChanged -= RefreshViewsSetupPopup;
                activeViewsSetupTarget = null;
            }

            if (viewsSetupPopupRoot != null)
            {
                viewsSetupPopupRoot.style.display = DisplayStyle.None;
            }
        }

        private void RefreshViewsSetupPopup()
        {
            Debug.Assert(viewsSetupPopupRoot != null, "[SimulationModeManager] Cannot refresh a missing Views Setup popup.");
            if (viewsSetupPopupRoot == null)
            {
                return;
            }

            if (activeViewsSetupTarget == null)
            {
                viewsSetupPopupRoot.style.display = DisplayStyle.None;
                return;
            }

            if (viewsSetupTitleLabel != null)
            {
                viewsSetupTitleLabel.text = activeViewsSetupTarget.DisplayName;
            }

            int[] visibilityPercentages = activeViewsSetupTarget.GetViewVisibilityPercentagesCopy();
            float[] sizeMultipliers = activeViewsSetupTarget.GetViewSizeMultipliersCopy();
            for (int i = 0; i < viewsSetupSliders.Count; i++)
            {
                SliderInt slider = viewsSetupSliders[i];
                if (slider == null)
                {
                    continue;
                }

                if (i < views.Count)
                {
                    if (slider.parent != null)
                    {
                        slider.parent.style.display = DisplayStyle.Flex;
                    }
                }
                else
                {
                    if (slider.parent != null)
                    {
                        slider.parent.style.display = DisplayStyle.None;
                    }
                }

                if (i < visibilityPercentages.Length)
                {
                    slider.SetValueWithoutNotify(visibilityPercentages[i]);
                    SetViewsSetupSliderValueLabel(i, visibilityPercentages[i]);
                }
            }

            for (int i = 0; i < viewsSetupSizeSliders.Count; i++)
            {
                Slider slider = viewsSetupSizeSliders[i];
                if (slider == null)
                {
                    continue;
                }

                if (i < views.Count)
                {
                    if (slider.parent != null)
                    {
                        slider.parent.style.display = DisplayStyle.Flex;
                    }
                }
                else
                {
                    if (slider.parent != null)
                    {
                        slider.parent.style.display = DisplayStyle.None;
                    }
                }

                if (i < sizeMultipliers.Length)
                {
                    slider.SetValueWithoutNotify(sizeMultipliers[i]);
                    SetViewsSetupSizeSliderValueLabel(i, sizeMultipliers[i]);
                }
            }
        }

        private string GetDefaultSceneSetupDirectory()
        {
            string assetsPath = Application.dataPath;
            string projectRoot = Path.GetFullPath(Path.Combine(assetsPath, ".."));
            return Path.Combine(projectRoot, "SavedScenes");
        }

        private void SaveSceneSetup()
        {
            Debug.Assert(mainScene != null, "[SimulationModeManager] SaveSceneSetup requires mainScene.");
            if (mainScene == null)
            {
                Debug.Assert(false, "[SimulationModeManager] mainScene is null in SaveSceneSetup.");
                return;
            }

            if (!MainScene.IsReady || !LocationScript.IsReady)
            {
                Debug.LogWarning("[SimulationModeManager] Cannot save scene setup; simulation is not ready.");
                return;
            }

            string initialDir = GetDefaultSceneSetupDirectory();
            if (!Directory.Exists(initialDir))
            {
                Directory.CreateDirectory(initialDir);
            }

            sceneSetupFileDialogs.ShowSavePath(initialDir, SaveSceneSetupToPath);
        }

        private void SaveSceneSetupToPath(string path)
        {
            Debug.Assert(!string.IsNullOrEmpty(path), "[SimulationModeManager] Save path is empty.");
            if (string.IsNullOrEmpty(path))
            {
                Debug.Assert(false, "[SimulationModeManager] Save path is empty.");
                return;
            }

            string requiredExt = "." + SceneSetupFileDialogs.SceneSetupExtension;
            if (!path.EndsWith(requiredExt, System.StringComparison.OrdinalIgnoreCase))
            {
                path = path + requiredExt;
            }

            SceneSetupFileV1 dto = SceneSetupCapture.Capture(mainScene);
            string json = JsonUtility.ToJson(dto, true);
            File.WriteAllText(path, json);
            Debug.Log("[SimulationModeManager] Scene setup saved: " + path);
        }

        private void LoadSceneSetup()
        {
            Debug.Assert(mainScene != null, "[SimulationModeManager] LoadSceneSetup requires mainScene.");
            if (mainScene == null)
            {
                Debug.Assert(false, "[SimulationModeManager] mainScene is null in LoadSceneSetup.");
                return;
            }

            if (!MainScene.IsReady)
            {
                Debug.LogWarning("[SimulationModeManager] Cannot load scene setup; MainScene is not ready.");
                return;
            }

            string initialDir = GetDefaultSceneSetupDirectory();
            if (!Directory.Exists(initialDir))
            {
                Directory.CreateDirectory(initialDir);
            }

            sceneSetupFileDialogs.ShowOpenPath(initialDir, LoadSceneSetupFromPath);
        }

        private void LoadSceneSetupFromPath(string path)
        {
            Debug.Assert(!string.IsNullOrEmpty(path), "[SimulationModeManager] Open path is empty.");
            if (string.IsNullOrEmpty(path))
            {
                Debug.Assert(false, "[SimulationModeManager] Open path is empty.");
                return;
            }

            if (!File.Exists(path))
            {
                Debug.LogError("[SimulationModeManager] Scene setup file does not exist: " + path);
                Debug.Assert(false, "[SimulationModeManager] Selected scene setup file does not exist.");
                return;
            }

            string json = File.ReadAllText(path);
            SceneSetupFileV1 setup = JsonUtility.FromJson<SceneSetupFileV1>(json);
            if (setup == null)
            {
                Debug.LogError("[SimulationModeManager] Failed to parse scene setup JSON.");
                Debug.Assert(false, "[SimulationModeManager] Failed to parse scene setup JSON.");
                return;
            }

            Debug.Assert(mainScene.simulationAPI != null, "[SimulationModeManager] SimulationAPI is null; cannot apply setup.");
            if (mainScene.simulationAPI == null)
            {
                Debug.Assert(false, "[SimulationModeManager] SimulationAPI is null; cannot apply setup.");
                return;
            }

            // Loading clears current scene setup first.
            SceneSetupApplier.ClearCurrentSetup(mainScene.simulationAPI, this);
            SceneSetupApplier.Apply(mainScene.simulationAPI, setup);

            Debug.Log("[SimulationModeManager] Scene setup loaded: " + path);
        }

        public override void EnterMode()
        {
            mainGui.style.display = DisplayStyle.Flex;
            
            // Re-enable camera controls if they were disabled
            if (cameraRig != null) cameraRig.SetActive(true);

            // Add first view if none exist
            if (views.Count == 0)
            {
                SetViewCountAndUpdateGUIState(1);
            }

            // The initial location is loaded before Simulation mode becomes active,
            // so its location-ready callback cannot refresh the species pickers.
            RefreshSpeciesAvailabilityUI();
            UpdateHudVisibility();
        }

        public override void ExitMode()
        {
            sceneSetupFileDialogs.Close();
            SetSimulationPaused(false);
            entityHoverController.Deactivate();
            mainGui.style.display = DisplayStyle.None;
            if (cameraRig != null)
            {
                if(cameraRig.GetComponent<SimulationModeCameraRig>().isActive)
                {
                    DectivateSwimMode();
                }

                if (isAutomaticCameraModeActive)
                {
                    DectivateAutomaticCameraMode();
                }

                cameraRig.SetActive(false);
            }
        }

        public override void EnterMenu()
        {
            sceneSetupFileDialogs.Close();
            entityHoverController.Deactivate();
            mainGui.style.display = DisplayStyle.None;
        }

        public override void ExitMenu()
        {
            mainGui.style.display = DisplayStyle.Flex;
            UpdateHudVisibility();
        }

        public override void OnUpdate()
        {
            // RMB exits swim mode and automatic camera mode
            if (Input.GetMouseButtonDown(1)) // Right mouse button
            {
                if (cameraRig.GetComponent<SimulationModeCameraRig>().isActive)
                {
                    DectivateSwimMode();
                }
                else if (mainScene.mainCamera.transform.parent == mainScene.currentLocationScript.dollyCart.transform)
                {
                    DectivateAutomaticCameraMode();
                }
            }

            entityHoverController.Update(
                IsEntityHoverInteractionAllowed(),
                views.Count,
                useSyntheticEntityHover,
                syntheticEntityHoverScreenPosition);
        }

        private bool IsEntityHoverInteractionAllowed()
        {
            if (mainGui == null)
            {
                return false;
            }

            if (useSyntheticEntityHover)
            {
                return true;
            }

            if (!mainScene.EntityHoverEnabled)
            {
                return false;
            }

            if (Application.isBatchMode || isHudless || isAutomaticCameraModeActive)
            {
                return false;
            }

            if (mainGui.resolvedStyle.display == DisplayStyle.None ||
                mainGui.resolvedStyle.opacity < 0.5f)
            {
                return false;
            }

            if (cameraRig == null)
            {
                return false;
            }

            SimulationModeCameraRig rig = cameraRig.GetComponent<SimulationModeCameraRig>();
            Debug.Assert(rig != null, "[SimulationModeManager] Simulation camera rig controller is required for entity hover.");
            return rig != null && !rig.isActive;
        }

        public void SetSyntheticEntityHoverForBenchmark(bool enabled, Vector2 screenPosition)
        {
            Debug.Assert(
                Application.isEditor || Debug.isDebugBuild || Application.isBatchMode,
                "[SimulationModeManager] Synthetic entity hover is only intended for benchmark-capable builds.");
            useSyntheticEntityHover = enabled;
            syntheticEntityHoverScreenPosition = screenPosition;
            if (!enabled)
            {
                entityHoverController.Deactivate();
            }
        }
        
        public override void OnLocationReady()
        {
            entityHoverController.Clear();
            EntityHoverSpatialIndexSystem hoverIndexSystem =
                World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<EntityHoverSpatialIndexSystem>();
            if (hoverIndexSystem != null)
            {
                hoverIndexSystem.RequestStaticRebuild();
            }
            RefreshSpeciesAvailabilityUI();

            // After the new location is loaded, update all dynamic entities groups
            foreach (DynamicEntitiesGroup group in dynamicEntitiesGroups)
            {
                // Get new boid bounds for all of the group's active habitats (override or preset)
                List<GameObject> newBoidBounds = new List<GameObject>();
                foreach (string habitat in group.GetActiveHabitats())
                {
                    var habitatBounds = mainScene.currentLocationScript.GetBoidBoundsByBiomeName(habitat);
                    if (habitatBounds != null && habitatBounds.Count > 0)
                    {
                        newBoidBounds.AddRange(habitatBounds);
                    }
                    else
                    {
                        Debug.LogWarning($"[MainScene] No boid bounds found for habitat: {habitat} in new location");
                    }
                }

                if (newBoidBounds.Count > 0)
                {
                    // Update the group with new bounds
                    group.UpdateBoidBounds(newBoidBounds);
                }
                else
                {
                    Debug.LogError($"[MainScene] No valid boid bounds found for group {group.name} in any of its habitats");
                }
            }

            // Update all static entities groups
            UpdateStaticEntitiesGroups();

            // Run any pending simulation setup executors for components in the newly loaded location.
            Debug.Assert(mainScene != null, "[SimulationModeManager] mainScene is null in OnLocationReady.");
            if (mainScene != null)
            {
                Debug.Assert(mainScene.simulationAPI != null, "[SimulationModeManager] SimulationAPI is null in OnLocationReady.");
                if (mainScene.simulationAPI != null)
                {
                    mainScene.simulationAPI.RunPendingExecutors();
                }
            }

            // Restore turbidity slider state based on current swim mode status
            if (cameraRig != null)
            {
                var cameraRigController = cameraRig.GetComponent<SimulationModeCameraRig>();
                if (cameraRigController != null && cameraRigController.isActive)
                {
                    SetTurbiditySliderInteractivity(false);
                }
            }
        }

        private void OnLocationLocationDropdownFieldChanged(string locationName)
        {
            if (cameraRig != null)
            {
                var cameraRigController = cameraRig.GetComponent<SimulationModeCameraRig>();
                if (cameraRigController != null)
                {
                    cameraRigController.ResetPositionAndRotation();
                }
            }
            mainScene.SwitchLocation(locationName);
            locationsDropdownField.value = locationName;
        }

        private void UpdateStaticEntitiesGroups()
        {
            foreach (var group in staticEntitiesGroups)
            {
                // Force reload of the group to update entities
                if (group != null)
                {
                    _ = group.ReloadGroup(new ClickEvent());
                }
            }
        }
        
        public void SpawnSelectedDynamicPreset()
        {
            if (!IsDynamicSpeciesAvailable(addDynamicRowDropdownField.value))
            {
                Debug.LogWarning("[SimulationModeManager] Cannot add dynamic species without matching boid bounds in the current location: " + addDynamicRowDropdownField.value);
                return;
            }

            SpawnDynamicPreset(addDynamicRowDropdownField.value);
        }
        
        public void SpawnDynamicPreset(string name)
        {
            if (GroupPresetsManager.Instance == null)
            {
                Debug.LogError("[MainScene] GroupPresetsManager.Instance is null. Make sure it's properly initialized.");
                return;
            }
            
            Debug.Log($"[MainScene] Attempting to spawn dynamic preset: {name}");
            DynamicEntityPreset selectedPreset = GroupPresetsManager.Instance.GetDynamicPresetByName(name);
            
            if (selectedPreset == null)
            {
                Debug.LogError($"[MainScene] Error: Dynamic preset '{name}' not found in the presets list.");
                return;
            }

            SpawnDynamicPresetWithPreset(selectedPreset, selectedPreset.name, null, "SpawnDynamicPreset");
        }

        /// <summary>
        /// Spawns a dynamic preset into explicitly provided habitats, overriding the preset habitats.
        /// </summary>
        /// <param name="presetName">Dynamic preset name</param>
        /// <param name="groupName">Display name for this session's group</param>
        /// <param name="habitats">Habitats to use to collect boid bounds</param>
        public void SpawnDynamicPresetInHabitats(string presetName, string groupName, string[] habitats)
        {
            if (GroupPresetsManager.Instance == null)
            {
                Debug.LogError("[MainScene] GroupPresetsManager.Instance is null. Make sure it's properly initialized.");
                return;
            }

            if (habitats == null || habitats.Length == 0)
            {
                Debug.LogError("[MainScene] SpawnDynamicPresetInHabitats requires at least one habitat.");
                Debug.Assert(false, "SpawnDynamicPresetInHabitats called with empty habitats.");
                return;
            }

            Debug.Log("[MainScene] Attempting to spawn dynamic preset in habitats: " + presetName + " as '" + groupName + "'");
            DynamicEntityPreset selectedPreset = GroupPresetsManager.Instance.GetDynamicPresetByName(presetName);

            if (selectedPreset == null)
            {
                Debug.LogError("[MainScene] Error: Dynamic preset '" + presetName + "' not found in the presets list.");
                return;
            }

            SpawnDynamicPresetWithPreset(selectedPreset, groupName, habitats, "SpawnDynamicPresetInHabitats");
        }

        /// <summary>
        /// Spawns a dynamic preset with a custom group display name for this session.
        /// </summary>
        /// <param name="presetName">Name of the dynamic preset to spawn</param>
        /// <param name="groupName">Display name for this session's group</param>
        public void SpawnDynamicPreset(string presetName, string groupName)
        {
            if (GroupPresetsManager.Instance == null)
            {
                Debug.LogError("[MainScene] GroupPresetsManager.Instance is null. Make sure it's properly initialized.");
                return;
            }

            Debug.Log("[MainScene] Attempting to spawn dynamic preset: " + presetName + " as '" + groupName + "'");
            DynamicEntityPreset selectedPreset = GroupPresetsManager.Instance.GetDynamicPresetByName(presetName);

            if (selectedPreset == null)
            {
                Debug.LogError("[MainScene] Error: Dynamic preset '" + presetName + "' not found in the presets list.");
                return;
            }

            SpawnDynamicPresetWithPreset(selectedPreset, groupName, null, "SpawnDynamicPreset (custom name)");
        }

        /// <summary>
        /// Spawns a dynamic group from a provided preset object.
        /// Used by the CPU benchmark so boid behavior values stay fixed even when StreamingAssets presets change.
        /// </summary>
        public void SpawnDynamicBenchmarkPreset(DynamicEntityPreset preset, string groupName, string[] habitats)
        {
            Debug.Assert(preset != null, "SpawnDynamicBenchmarkPreset requires a preset.");
            if (preset == null)
            {
                return;
            }

            Debug.Log("[MainScene] Attempting to spawn fixed benchmark dynamic preset: " + preset.name + " as '" + groupName + "'");
            SpawnDynamicPresetWithPreset(preset, groupName, habitats, "SpawnDynamicBenchmarkPreset");
        }

        private async void SpawnDynamicPresetWithPreset(DynamicEntityPreset selectedPreset, string groupName, string[] overrideHabitats, string completionLabel)
        {
            MainScene.SetReadyState(false);
            if (selectedPreset == null)
            {
                Debug.LogError("[MainScene] Dynamic preset is required.");
                MainScene.SetReadyState(true);
                return;
            }

            string[] habitats = selectedPreset.habitats;
            bool hasOverrideHabitats = overrideHabitats != null && overrideHabitats.Length > 0;
            if (hasOverrideHabitats)
            {
                habitats = overrideHabitats;
            }

            if (habitats == null || habitats.Length == 0)
            {
                Debug.LogError("[MainScene] Error: Dynamic preset '" + selectedPreset.name + "' has no habitats defined.");
                MainScene.SetReadyState(true);
                return;
            }

            VisualElement dataRow = DynamicGroupDataRow.CloneTree();
            mainGui.Q<VisualElement>("DataRows").Add(dataRow);

            List<GameObject> filteredBoidBounds = new List<GameObject>();
            foreach (string habitat in habitats)
            {
                if (string.IsNullOrEmpty(habitat))
                {
                    Debug.LogError("[MainScene] Empty habitat found for dynamic preset '" + selectedPreset.name + "'");
                    continue;
                }

                var habitatBounds = mainScene.currentLocationScript.GetBoidBoundsByBiomeName(habitat);
                if (habitatBounds != null && habitatBounds.Count > 0)
                {
                    filteredBoidBounds.AddRange(habitatBounds);
                    Debug.Log("[MainScene] Found " + habitatBounds.Count + " boid bounds for habitat: " + habitat);
                }
                else
                {
                    Debug.LogWarning("[MainScene] No boid bounds found for habitat: " + habitat);
                }
            }

            if (filteredBoidBounds.Count == 0)
            {
                Debug.LogError("[MainScene] No valid boid bounds found for any of the specified habitats");
                mainGui.Q<VisualElement>("DataRows").Remove(dataRow);
                MainScene.SetReadyState(true);
                return;
            }

            DynamicEntitiesGroup dynamicEntitiesGroup = new DynamicEntitiesGroup();
            dynamicEntitiesGroup.Setup(
                name: groupName,
                dynamicEntityId: nextDynamicEntityGroupId,
                dynamicEntityPreset: selectedPreset,
                dataRow: dataRow,
                viewsCount: views.Count,
                boidBounds: filteredBoidBounds
            );

            if (hasOverrideHabitats)
            {
                dynamicEntitiesGroup.SetOverrideHabitats(overrideHabitats);
            }

            RegisterViewsSetupButton(dataRow, dynamicEntitiesGroup);

            try
            {
                await dynamicEntitiesGroup.LoadAndSpawnGroup();
                dynamicEntitiesGroups.Add(dynamicEntitiesGroup);
                dynamicEntitiesGroup.OnDeleteRequest += HandleGroupDeleteRequest;
                nextDynamicEntityGroupId++;
            }
            catch (Exception e)
            {
                Debug.LogError("[MainScene] Failed to load and spawn dynamic entities group: " + e.Message);
                mainGui.Q<VisualElement>("DataRows").Remove(dataRow);
                return;
            }
            finally
            {
                MainScene.SetReadyState(true);
                Debug.Log("[MainScene] " + completionLabel + " completed, IsReady set to true");
            }
        }
        
        public async void SpawnSelectedStaticPreset()
        {
            if (!IsStaticSpeciesAvailable(addStaticRowDropdownField.value))
            {
                Debug.LogWarning("[SimulationModeManager] Cannot add static species without a matching terrain or mesh habitat in the current location: " + addStaticRowDropdownField.value);
                return;
            }

            await SpawnStaticPreset(addStaticRowDropdownField.value, addStaticRowDropdownField.value);
        }

        private void RefreshSpeciesAvailabilityUI()
        {
            availableDynamicSpeciesNames.Clear();
            availableStaticSpeciesNames.Clear();

            if (mainScene != null && mainScene.currentLocationScript != null && GroupPresetsManager.Instance != null)
            {
                HashSet<string> availableBoidHabitats = mainScene.currentLocationScript.GetAvailableBoidHabitatNames();
                foreach (DynamicEntityPreset preset in GroupPresetsManager.Instance.dynamicEntitiesPresetsList)
                {
                    if (HasAnyAvailableHabitat(preset.habitats, availableBoidHabitats))
                    {
                        availableDynamicSpeciesNames.Add(preset.name);
                    }
                }

                HashSet<string> availableStaticHabitats = mainScene.currentLocationScript.GetAvailableStaticSpawnHabitatNames();
                foreach (StaticEntityPreset preset in GroupPresetsManager.Instance.staticEntitiesPresetsList)
                {
                    if (HasAnyAvailableHabitat(preset.habitats, availableStaticHabitats))
                    {
                        availableStaticSpeciesNames.Add(preset.name);
                    }
                }
            }

            Debug.Log(
                "[SimulationModeManager] Species availability refreshed. Dynamic: "
                + availableDynamicSpeciesNames.Count
                + "/"
                + GroupPresetsManager.Instance.dynamicEntitiesPresetsList.Count
                + ", Static: "
                + availableStaticSpeciesNames.Count
                + "/"
                + GroupPresetsManager.Instance.staticEntitiesPresetsList.Count);

            if (addDynamicRowDropdownField != null)
            {
                addDynamicRowDropdownField.SetValueWithoutNotify(addDynamicRowDropdownField.value);
                addDynamicRowDropdownField.MarkDirtyRepaint();
            }
            if (addStaticRowDropdownField != null)
            {
                addStaticRowDropdownField.SetValueWithoutNotify(addStaticRowDropdownField.value);
                addStaticRowDropdownField.MarkDirtyRepaint();
            }

            RefreshSpeciesAddButtonStates();
        }

        private void OnMeshHabitatAvailabilityChanged()
        {
            RefreshSpeciesAvailabilityUI();
        }

        private void RefreshSpeciesAddButtonStates()
        {
            if (addDynamicRowButton != null && addDynamicRowDropdownField != null)
            {
                addDynamicRowButton.SetEnabled(IsDynamicSpeciesAvailable(addDynamicRowDropdownField.value));
            }
            if (addStaticRowButton != null && addStaticRowDropdownField != null)
            {
                addStaticRowButton.SetEnabled(IsStaticSpeciesAvailable(addStaticRowDropdownField.value));
            }
        }

        private string FormatDynamicSpeciesChoice(string speciesName)
        {
            if (IsDynamicSpeciesAvailable(speciesName))
            {
                return speciesName;
            }

            return "<color=#777777>" + speciesName + " (no matching habitat)</color>";
        }

        private string FormatStaticSpeciesChoice(string speciesName)
        {
            if (IsStaticSpeciesAvailable(speciesName))
            {
                return speciesName;
            }

            return "<color=#777777>" + speciesName + " (no matching habitat)</color>";
        }

        private bool IsDynamicSpeciesAvailable(string speciesName)
        {
            return !string.IsNullOrEmpty(speciesName) && availableDynamicSpeciesNames.Contains(speciesName);
        }

        private bool IsStaticSpeciesAvailable(string speciesName)
        {
            return !string.IsNullOrEmpty(speciesName) && availableStaticSpeciesNames.Contains(speciesName);
        }

        private static bool HasAnyAvailableHabitat(string[] habitatNames, HashSet<string> availableHabitatNames)
        {
            if (habitatNames == null || availableHabitatNames == null)
            {
                return false;
            }

            foreach (string habitatName in habitatNames)
            {
                string resolvedHabitatName = habitatName;
                if (string.IsNullOrEmpty(resolvedHabitatName))
                {
                    resolvedHabitatName = "Default";
                }

                if (availableHabitatNames.Contains(resolvedHabitatName))
                {
                    return true;
                }
            }

            return false;
        }

        public async Task SpawnStaticPreset(string presetName, string groupName)
        {
            MainScene.SetReadyState(false);
            if (GroupPresetsManager.Instance == null)
            {
                Debug.LogError("[MainScene] GroupPresetsManager.Instance is null. Make sure it's properly initialized.");
                return;
            }
            
            Debug.Log($"[MainScene] Attempting to spawn static preset: {presetName}");
            StaticEntityPreset selectedPreset = GroupPresetsManager.Instance.GetStaticPresetByName(presetName);
            
            if (selectedPreset == null)
            {
                Debug.LogError($"[MainScene] Error: Static preset '{presetName}' not found in the presets list.");
                return;
            }

            if (selectedPreset.habitats == null || selectedPreset.habitats.Length == 0)
            {
                Debug.LogError($"[MainScene] Error: Static preset '{presetName}' has no habitats defined.");
                return;
            }

            VisualElement dataRow = StaticGroupDataRow.CloneTree();
            mainGui.Q<VisualElement>("DataRows").Add(dataRow);

            StaticEntitiesGroup staticEntitiesGroup = new StaticEntitiesGroup();
            staticEntitiesGroup.Setup(
                name: groupName,
                dataRow: dataRow,
                viewsCount: views.Count,
                staticEntitiesGroupId: nextStaticEntityGroupId,
                staticEntityPreset: selectedPreset
            );
            RegisterViewsSetupButton(dataRow, staticEntitiesGroup);
            
            try
            {
                await staticEntitiesGroup.LoadAndSpawnGroup();
                staticEntitiesGroups.Add(staticEntitiesGroup);
                if (mainScene.currentLocationScript != null)
                {
                    mainScene.currentLocationScript.ApplyWaterCurrentSettingsToStaticEntityGroup(staticEntitiesGroup);
                }
                staticEntitiesGroup.OnDeleteRequest += HandleStaticGroupDeleteRequest;
                nextStaticEntityGroupId++;
            }
            catch (Exception e)
            {
                Debug.LogError($"[MainScene] Failed to load and spawn static entities group: {e.Message}");
                mainGui.Q<VisualElement>("DataRows").Remove(dataRow);
                return;
            }
            finally
            {
                MainScene.SetReadyState(true);
                Debug.Log("[MainScene] SpawnStaticPreset completed, IsReady set to true");
            }
        }

        /// <summary>
        /// Spawns a static preset and writes the provided habitats to the group's ECS buffer instead of the preset habitats.
        /// </summary>
        /// <param name="presetName">Static preset name</param>
        /// <param name="groupName">Display name for the group</param>
        /// <param name="habitats">Habitats to use when creating the group's ECS habitat buffer</param>
        public async Task SpawnStaticPresetInHabitats(string presetName, string groupName, string[] habitats)
        {
            MainScene.SetReadyState(false);
            if (GroupPresetsManager.Instance == null)
            {
                Debug.LogError("[MainScene] GroupPresetsManager.Instance is null. Make sure it's properly initialized.");
                return;
            }

            if (habitats == null || habitats.Length == 0)
            {
                Debug.LogError("[MainScene] SpawnStaticPresetInHabitats requires at least one habitat.");
                Debug.Assert(false, "SpawnStaticPresetInHabitats called with empty habitats.");
                return;
            }

            Debug.Log("[MainScene] Attempting to spawn static preset in habitats: " + presetName + " as '" + groupName + "'");
            StaticEntityPreset selectedPreset = GroupPresetsManager.Instance.GetStaticPresetByName(presetName);

            if (selectedPreset == null)
            {
                Debug.LogError("[MainScene] Error: Static preset '" + presetName + "' not found in the presets list.");
                return;
            }

            VisualElement dataRow = StaticGroupDataRow.CloneTree();
            mainGui.Q<VisualElement>("DataRows").Add(dataRow);

            StaticEntitiesGroup staticEntitiesGroup = new StaticEntitiesGroup();
            staticEntitiesGroup.Setup(
                name: groupName,
                dataRow: dataRow,
                viewsCount: views.Count,
                staticEntitiesGroupId: nextStaticEntityGroupId,
                staticEntityPreset: selectedPreset
            );
            staticEntitiesGroup.SetOverrideHabitats(habitats);
            RegisterViewsSetupButton(dataRow, staticEntitiesGroup);

            try
            {
                await staticEntitiesGroup.LoadAndSpawnGroup();
                staticEntitiesGroups.Add(staticEntitiesGroup);
                if (mainScene.currentLocationScript != null)
                {
                    mainScene.currentLocationScript.ApplyWaterCurrentSettingsToStaticEntityGroup(staticEntitiesGroup);
                }
                staticEntitiesGroup.OnDeleteRequest += HandleStaticGroupDeleteRequest;
                nextStaticEntityGroupId++;
            }
            catch (Exception e)
            {
                Debug.LogError("[MainScene] Failed to load and spawn static entities group: " + e.Message);
                mainGui.Q<VisualElement>("DataRows").Remove(dataRow);
                return;
            }
            finally
            {
                MainScene.SetReadyState(true);
                Debug.Log("[MainScene] SpawnStaticPresetInHabitats completed, IsReady set to true");
            }
        }

        private void HandleGroupDeleteRequest(DynamicEntitiesGroup dynamicEntitiesGroup)
        {
            if (ReferenceEquals(activeViewsSetupTarget, dynamicEntitiesGroup))
            {
                CloseViewsSetupPopup();
            }

            dynamicEntitiesGroups.Remove(dynamicEntitiesGroup);
        }

        private void HandleStaticGroupDeleteRequest(StaticEntitiesGroup staticEntitiesGroup)
        {
            if (ReferenceEquals(activeViewsSetupTarget, staticEntitiesGroup))
            {
                CloseViewsSetupPopup();
            }

            staticEntitiesGroups.Remove(staticEntitiesGroup);
        }

        /// <summary>
        /// Enables or disables all turbidity sliders with visual feedback
        /// </summary>
        /// <param name="enabled">True to enable sliders, false to disable them</param>
        private void SetTurbiditySliderInteractivity(bool enabled)
        {
            if (mainScene == null || mainScene.currentLocationScript == null)
            {
                return;
            }

            foreach (var slider in mainScene.currentLocationScript.turbiditySliders)
            {
                if (slider != null)
                {
                    slider.SetEnabled(enabled);

                    // Visual feedback for disabled state
                    slider.style.opacity = enabled ? 1.0f : 0.3f;
                }
            }
        }

        public void OnTurbiditySliderValueChanged(ChangeEvent<float> evt)
        {
            var slider = evt.target as Slider;

            // Reject slider changes when swim mode is active
            if (cameraRig != null)
            {
                var cameraRigController = cameraRig.GetComponent<SimulationModeCameraRig>();
                if (cameraRigController != null && cameraRigController.isActive)
                {
                    // Revert the slider to its previous value without triggering callback
                    slider.SetValueWithoutNotify(evt.previousValue);
                    return;
                }
            }

            int viewIndex = int.Parse(slider.name.Substring(slider.name.Length - 1));
            float turbidityStrength = Mathf.Clamp(evt.newValue, -1f, 1f);
            turbidityPerView[viewIndex] = turbidityStrength;
            mainScene.currentLocationScript.SetTurbidityForView(viewIndex, turbidityStrength);
        }
        
        public void SetViewCountAndUpdateGUIState(int viewCount)
        {
            MainScene.SetReadyState(false);
            try
            {
                // Dark gray color
                StyleColor defaultColor = new StyleColor(new Color(0.3f, 0.3f, 0.3f));

                // Selected cyan color
                StyleColor selectedColor = new StyleColor(new Color(0.0f, 0.8f, 1.0f));

                // Reset the background color of the view count buttons to default
                mainGui.Q<Button>("SetViews1").style.backgroundColor = defaultColor;
                mainGui.Q<Button>("SetViews2").style.backgroundColor = defaultColor;
                mainGui.Q<Button>("SetViews3").style.backgroundColor = defaultColor;
                mainGui.Q<Button>("SetViews4").style.backgroundColor = defaultColor;

                // Change the background color of the correct view count button to blue to indicate the current view count
                if (viewCount == 1)
                {
                    mainGui.Q<Button>("SetViews1").style.backgroundColor = selectedColor;
                }
                else if (viewCount == 2)
                {
                    mainGui.Q<Button>("SetViews2").style.backgroundColor = selectedColor;
                }
                else if (viewCount == 3)
                {
                    mainGui.Q<Button>("SetViews3").style.backgroundColor = selectedColor;
                }
                else if (viewCount == 4)
                {
                    mainGui.Q<Button>("SetViews4").style.backgroundColor = selectedColor;
                }

                // If the view count is less than the current view count
                if (viewCount < views.Count)
                {
                    // Remove views until the view count is equal to the desired view count
                    while (views.Count > viewCount)
                    {
                        RemoveView();
                    }
                }
                // If the view count is greater than the current view count
                else if (viewCount > views.Count)
                {
                    // Add views until the view count is equal to the desired view count
                    while (views.Count < viewCount)
                    {
                        AddView();
                    }
                }
            }
            finally
            {
                MainScene.SetReadyState(true);
            }
        }

        private void AddView()
        {
            MainScene.SetReadyState(false);
            try
            {
                // Debug
                Debug.Log("[MainScene] AddView");

                // Instantiate a View object and add it to the views list
                View view = new View();
                views.Add(view);

                // Get index of viewPrefab in views list
                int index = views.IndexOf(view);
                
                // Inform each group about viewcount change
                foreach (DynamicEntitiesGroup group in dynamicEntitiesGroups)
                {
                    group.UpdateViewsCount(views.Count);
                }
                
                foreach (StaticEntitiesGroup group in staticEntitiesGroups)
                {
                    group.UpdateViewsCount(views.Count);
                }
                
                // If it's not null, inform the location script about the view count change
                if (mainScene.currentLocationScript != null)
                {
                    mainScene.currentLocationScript.UpdateViewsCount(views.Count);
                }

                RefreshViewsSetupPopup();
                UpdateViewsLabel();
            }
            finally
            {
                MainScene.SetReadyState(true);
            }
        }

        private void RemoveView()
        {
            MainScene.SetReadyState(false);
            try
            {
                // Debug
                Debug.Log("[MainScene] RemoveView");

                // If min views reached, return
                if (views.Count <= 1)
                {
                    return;
                }

                // Erase the last view from views list
                views.RemoveAt(views.Count - 1);

                // Inform each group about viewcount change
                foreach (DynamicEntitiesGroup group in dynamicEntitiesGroups)
                {
                    group.UpdateViewsCount(views.Count);
                }
                
                foreach (StaticEntitiesGroup group in staticEntitiesGroups)
                {
                    group.UpdateViewsCount(views.Count);
                }
                
                // If it's not null, inform the location script about the view count change
                if (mainScene.currentLocationScript != null)
                {
                    mainScene.currentLocationScript.UpdateViewsCount(views.Count);
                }

                RefreshViewsSetupPopup();
                UpdateViewsLabel();
            }
            finally
            {
                MainScene.SetReadyState(true);
            }
        }

        void UpdateViewsLabel()
        {
            // Debug
            Debug.Log("[MainScene] Views: " + views.Count);
        }

        public void ActivateSwimMode()
        {
            cameraRig.GetComponent<SimulationModeCameraRig>().Activate();
            SetTurbiditySliderInteractivity(false); // Disable sliders when entering swim mode
            UpdateHudVisibility();
        }

        public void DectivateSwimMode()
        {
            cameraRig.GetComponent<SimulationModeCameraRig>().Deactivate();
            SetTurbiditySliderInteractivity(true); // Re-enable sliders when exiting swim mode
            UpdateHudVisibility();
        }
        
        public void ActivateAutomaticCameraMode()
        {
            isAutomaticCameraModeActive = true;
            mainScene.mainCamera.transform.parent = mainScene.currentLocationScript.dollyCart.transform;
            mainScene.mainCamera.transform.localPosition = Vector3.zero;
            mainScene.mainCamera.transform.localRotation = Quaternion.identity;
            UpdateHudVisibility();
            UnityEngine.Cursor.visible = false;
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
        }
        
        public void DectivateAutomaticCameraMode()
        {
            isAutomaticCameraModeActive = false;
            mainScene.mainCamera.transform.parent = cameraRig.transform;
            mainScene.mainCamera.transform.localPosition = Vector3.zero;
            mainScene.mainCamera.transform.localRotation = Quaternion.identity;
            UpdateHudVisibility();
            UnityEngine.Cursor.visible = true;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
        }

        public void LoadLocationAndUpdateGUIState(string locationName)
        {
            if (cameraRig != null)
            {
                var cameraRigController = cameraRig.GetComponent<SimulationModeCameraRig>();
                if (cameraRigController != null)
                {
                    cameraRigController.ResetPositionAndRotation();
                }
            }
            // Temporarily unregister the callback using the stored reference
            locationsDropdownField.UnregisterCallback(locationChangeCallback);
            
            // Update the dropdown value
            locationsDropdownField.value = locationName;
            
            // Re-register the callback using the stored reference
            locationsDropdownField.RegisterCallback(locationChangeCallback);
            
            mainScene.LoadLocation(locationName);
        }

        public override bool OnEscapePressed()
        {
            if (sceneSetupFileDialogs.Close())
            {
                return true;
            }

            if (cameraRig != null && cameraRig.GetComponent<SimulationModeCameraRig>().isActive)
            {
                DectivateSwimMode();
                return true;
            }
            return false;
        }
        
        public override void SetHudless(bool hudless)
        {
            if (hudless)
            {
                sceneSetupFileDialogs.Close();
            }

            isHudless = hudless;
            UpdateHudVisibility();
        }

        private void UpdateHudVisibility()
        {
            bool hideHud = false;
            
            if (isHudless)
            {
                hideHud = true;
            }

            if (cameraRig != null)
            {
                var rig = cameraRig.GetComponent<SimulationModeCameraRig>();
                if (rig != null)
                {
                    if (rig.isActive)
                    {
                        hideHud = true;
                    }
                }
            }

            if (isAutomaticCameraModeActive)
            {
                hideHud = true;
            }

            if (mainGui != null)
            {
                if (hideHud)
                {
                    mainGui.style.opacity = 0;
                }
                else
                {
                    mainGui.style.opacity = 1;
                }
            }
        }

        private void OnDestroy()
        {
            Time.timeScale = 1.0f;
            MeshHabitatSetupSystem.AvailabilityChanged -= OnMeshHabitatAvailabilityChanged;
        }

        public StaticEntitiesGroupComponent GetStaticEntitiesGroupComponent(string groupName)
        {
            var group = staticEntitiesGroups.Find(g => g.name == groupName);
            if (group != null && group.staticEntitiesGroupStructs.Count > 0)
            {
                var entity = group.staticEntitiesGroupStructs[0].StaticEntitiesGroupEntity;
                if (entityManager.Exists(entity) && entityManager.HasComponent<StaticEntitiesGroupComponent>(entity))
                {
                    return entityManager.GetComponentData<StaticEntitiesGroupComponent>(entity);
                }
            }
            // Return a default or throw an exception if not found
            Debug.LogWarning($"[SimulationModeManager] Static entity group component for '{groupName}' not found.");
            return default;
        }
    }
} 
