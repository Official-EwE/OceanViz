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
using System.Reflection.Emit;
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

namespace OceanViz3 {

/// <summary>
/// Main controller class for the ocean visualization scene. Handles initialization, UI management,
/// entity group management, location management, and camera controls.
/// </summary>
public class MainScene : MonoBehaviour
{
	/// <summary>
	/// Indicates whether the MainScene is fully initialized and ready for operations. Used by the SimulationAPI to check if the scene is ready to receive state updates.
	/// </summary>
	public static bool IsReady { get; private set; }

	public static void SetReadyState(bool state)
	{
		IsReady = state;
	}

	/// <summary>
	/// Maximum number of simultaneous views supported by the application.
	/// </summary>
	private const int MaxViews = 4;
	private const string EntityHoverEnabledPlayerPrefsKey = "OceanViz3.EntityHoverEnabled";
	private const string FpsCounterEnabledPlayerPrefsKey = "OceanViz3.FpsCounterEnabled";
	private const float MainMenuWidth = 255.0f;
	private const float MainMenuHeight = 279.0f;
	private const float SettingsMenuWidth = 510.0f;
	private const float SettingsMenuHeight = 558.0f;
	public SimulationAPI simulationAPI;
	public LocationScript currentLocationScript;
	public String currentLocationName;
	public List<String> locationNames = new List<String>();
	/// <summary>
	/// List of active views in the scene.
	/// </summary>
	public List<View> views = new List<View>();
	
	/// <summary>
	/// Collection of location presets containing environment-specific settings like water turbidity colors.
	/// </summary>
	public LinkedList<KeyValuePair<string, LocationPreset>> locationPresets;

	/// <summary>
	/// List of active dynamic entity groups (e.g., fish schools) in the scene.
	/// </summary>
	[SerializeField]
	public List<DynamicEntitiesGroup> dynamicEntitiesGroups = new List<DynamicEntitiesGroup>();
	
	/// <summary>
	/// List of active static entity groups (e.g., coral, seaweed) in the scene.
	/// </summary>
	[SerializeField]
	public List<StaticEntitiesGroup> staticEntitiesGroups = new List<StaticEntitiesGroup>();

	//// UI
	public GameObject mainMenuUIDocument;
	private VisualElement mainMenuRoot;
	private VisualElement mainMenuWindow;
	private VisualElement mainMenuNavigationPanel;
	private VisualElement settingsPanel;
	private Button assetBrowserButton;
	private Button simulationModeButton;
	private Toggle entityHoverEnabledToggle;
	private Toggle fpsCounterEnabledToggle;
	private bool isMainMenuVisible = false;
	public bool EntityHoverEnabled { get; private set; }
	public bool FpsCounterEnabled { get; private set; }
	
	//// Game Objects
	public GameObject templateTerrain;
	public GameObject mainCamera;
	public GameObject currentLocationGameObject;
	[SerializeField] private FpsCounter fpsCounter;
	
	//// Rendering
	/// <summary>
	/// Universal Render Pipeline (URP) asset configuration. Used to enable/disable renderer features.
	/// </summary>
	public UniversalRendererData urpAsset;

	[Header("Culling (Size-Aware)")]
	[SerializeField] private float cullingStartMeshSize = 0.12f; // With this largest mesh dimension or smaller, the entity will be culled at cullingStartDistance
	[SerializeField] private float cullingStartDistance = 30.0f;
	[SerializeField] private float cullingEndMeshSize = 40.0f; // With this largest mesh dimension or larger, the entity will be culled at cullingEndDistance
	[SerializeField] private float cullingEndDistance = 300.0f; // After this distance, all entities are culled
	// For example, if the cullingStartMeshSize is 1.0f, the cullingEndMeshSize is 2.0f, the cullingStartDistance is 3.0f, the cullingEndDistance is 4.0f, then a mesh with a largest dimension of 1.5f will be culled at 3.5f distance from the camera, since it's in the middle of the range.
	// These values are sampled when entities spawn and are not refreshed at runtime.

	//// Entity Component System
	private World world;
	private EntityManager entityManager;
	public StaticEntityLocationDataCache StaticEntityLocationCache { get; } = new StaticEntityLocationDataCache();
	
	private NoiseTextureManager noiseTextureManager;

	// App Mode Management
	public AppMode currentMode { get; private set; }
	public SimulationModeManager simulationModeManager;
	public AssetBrowserModeManager assetBrowserModeManager;
	private AppModeManager currentModeManager;
	private bool isHudless;

	private void Awake()
	{
		EntityHoverEnabled = PlayerPrefs.GetInt(EntityHoverEnabledPlayerPrefsKey, 1) != 0;
		FpsCounterEnabled = PlayerPrefs.GetInt(FpsCounterEnabledPlayerPrefsKey, 0) != 0;
		Debug.Assert(fpsCounter != null, "[MainScene] FPS counter is required.");
		fpsCounter.SetVisible(FpsCounterEnabled);

		world = World.DefaultGameObjectInjectionWorld;
		entityManager = world.EntityManager;
		var sceneDataQuery = entityManager.CreateEntityQuery(typeof(SceneData));

		// --- Ensure SceneData singleton entity exists ---
		if (sceneDataQuery.CalculateEntityCount() == 0)
		{
			// Use mainCamera position if available, otherwise default to zero
			float3 cameraPos = float3.zero;
			if (mainCamera != null)
			{
				cameraPos = mainCamera.transform.position;
			}
			Entity sceneDataEntity = entityManager.CreateEntity(typeof(SceneData));
			entityManager.SetComponentData(sceneDataEntity, new SceneData
			{
				CameraPosition = cameraPos,
				CullingStartMeshSize = cullingStartMeshSize,
				CullingStartDistance = cullingStartDistance,
				CullingEndMeshSize = cullingEndMeshSize,
				CullingEndDistance = cullingEndDistance
			});
		}
		else
		{
			Entity sceneDataEntity = sceneDataQuery.GetSingletonEntity();
			entityManager.SetComponentData(sceneDataEntity, new SceneData
			{
				CameraPosition = entityManager.GetComponentData<SceneData>(sceneDataEntity).CameraPosition,
				CullingStartMeshSize = cullingStartMeshSize,
				CullingStartDistance = cullingStartDistance,
				CullingEndMeshSize = cullingEndMeshSize,
				CullingEndDistance = cullingEndDistance
			});
		}
	}

	private void OnDestroy()
	{
		if (world != null && world.IsCreated)
		{
			world.EntityManager.CompleteAllTrackedJobs();
		}
		StaticEntityLocationCache.Dispose();
	}
	
	private void Start()
	{
		Debug.Log("[MainScene] Start");
		
		IsReady = false;

		//// Rendering
		// Enable each Renderer Feature
		foreach (var feature in urpAsset.rendererFeatures)
		{
			feature.SetActive(true);
		}

		// Initialize NoiseTextureManager
		noiseTextureManager = NoiseTextureManager.Instance;
		if (noiseTextureManager == null)
		{
			Debug.LogError("[MainScene] Failed to initialize NoiseTextureManager");
			return;
		}
		
		//// Locations
		// Read StreamingAssets/Locations folder to populate the locationNames list
		UpdateLocationPresets();
		
		//// Presets
		// Read StreamingAssets/Locations folder to populate the presets lists
		GroupPresetsManager.Instance.UpdatePresets();

		// Set current location name before setting up mode managers
		var firstLocation = locationPresets.First.Value.Key;
		currentLocationName = firstLocation;

		// Setup mode managers
		simulationModeManager.Setup(this);
		assetBrowserModeManager.Setup(this);

		//// UI
		SetupMainMenu();
		// Hide main menu initially
		mainMenuRoot.style.display = DisplayStyle.None;
		
		// Load the first location scene
		SceneManager.LoadScene(firstLocation, LoadSceneMode.Additive);
		
		StartCoroutine(WaitForECSInitialization());
    }

    /// <summary>
    /// Initializes ECS components and waits for the world to be ready.
    /// </summary>
    private IEnumerator WaitForECSInitialization()
    {
        // Wait for the DefaultWorld to be created
        while (World.DefaultGameObjectInjectionWorld == null)
        {
            yield return null;
        }

        world = World.DefaultGameObjectInjectionWorld;

        // Wait for the EntityManager to be available
        while (world.EntityManager == null)
        {
            yield return null;
        }

        entityManager = world.EntityManager;

        // Wait for any systems to complete their initialization
        yield return null;

        // ECS world is now initialized
        IsReady = true;
        Debug.Log("[MainScene] ECS World initialized. MainScene is now ready.");

        simulationAPI.Setup(this);
		
		// Start in Simulation mode
		SwitchMode(AppMode.Simulation);
    }
	
    private struct TempLocationPreset
    {
        public string name;
        public int wind_turbine_pylon_amount;
    }
	
	/// <summary>
	/// Updates the collection of available location presets from the StreamingAssets folder.
	/// </summary>
	private void UpdateLocationPresets()
	{
		IsReady = false;
		
		locationPresets = new LinkedList<KeyValuePair<string, LocationPreset>>();
		locationNames.Clear();
        string[] locationFolders = Directory.GetDirectories(Path.Combine(Application.streamingAssetsPath, "Locations"));
		foreach (string folder in locationFolders)
		{
            var jsonPath = Path.Combine(Application.streamingAssetsPath, "Locations", folder, "location_properties.json");
			if (!File.Exists(jsonPath))
			{
				Debug.LogError($"[MainScene] location_properties.json not found in {jsonPath}");
				continue;
			}
			string json = File.ReadAllText(jsonPath);
			try
			{
				TempLocationPreset tempPreset = JsonUtility.FromJson<TempLocationPreset>(json);

				LocationPreset locationPreset = new LocationPreset
				{
                    name = tempPreset.name,
                    wind_turbine_pylon_amount = tempPreset.wind_turbine_pylon_amount
				};

				locationPresets.AddLast(new KeyValuePair<string, LocationPreset>(tempPreset.name, locationPreset));
				locationNames.Add(tempPreset.name);
			}
			catch (Exception e)
			{
				Debug.LogError($"[MainScene] Invalid JSON in {jsonPath}: {e.Message}");
			}
		}
		
		IsReady = true;
	}

	/// <summary>
	/// Converts a hexadecimal color string to a Unity Color object.
	/// </summary>
	/// <param name="hex">Hexadecimal color string (format: "0xRRGGBB" or "#RRGGBB")</param>
	/// <returns>Unity Color object</returns>
    // Removed color parsing; turbidity color settings are no longer used

	public void UnloadLocation()
	{
		IsReady = false;
		Debug.Log("[MainScene] Unloading location: " + currentLocationName);
		if (SceneManager.GetSceneByName(currentLocationName).isLoaded)
		{
			SceneManager.UnloadSceneAsync(currentLocationName);
		}
	}
	
	public void LoadLocationAndUpdateGUIState(string locationName)
	{
		simulationModeManager.LoadLocationAndUpdateGUIState(locationName);
	}

	/// <summary>
	/// Loads a new location and updates all entity groups to work with the new environment.
	/// </summary>
	/// <param name="locationName">Name of the location to load</param>
	public void LoadLocation(string locationName)
	{
		IsReady = false;
		
		Debug.Log("[MainScene] Location changing to: " + locationName);

		// Delete all the entities belonging to the previous location
		// - Obstacle entities
		EntityQuery obstacleQuery = entityManager.CreateEntityQuery(typeof(BoidObstacle));
		NativeArray<Entity> obstacleEntities = obstacleQuery.ToEntityArray(Allocator.Temp);
		foreach (Entity obstacleEntity in obstacleEntities)
		{
			entityManager.DestroyEntity(obstacleEntity);
		}
		obstacleEntities.Dispose();

		// Load the new location scene
		var loadOperation = SceneManager.LoadSceneAsync(locationName, LoadSceneMode.Additive);
		loadOperation.completed += (asyncOperation) => {
			currentLocationName = locationName;
			
			// Wait for OnLocationLoaded to complete
			StartCoroutine(WaitForLocationSetup(locationName));
		};
	}

	private IEnumerator WaitForLocationSetup(string locationName)
	{
		// Wait until currentLocationScript is set and initialized
		while (currentLocationScript == null || !currentLocationScript.isActiveAndEnabled || !LocationScript.IsReady)
		{
			yield return null;
		}

		Debug.Log($"[MainScene] Location {locationName} is ready, notifying current mode manager.");
		
		currentModeManager?.OnLocationReady();
		
		IsReady = true;
	}

	public void OnLocationLoaded(LocationScript loadedLocationScript)
	{
		Debug.Log("[MainScene] Location loaded: " + loadedLocationScript.gameObject.scene.name);
		
		currentLocationScript = loadedLocationScript;
		currentLocationGameObject = loadedLocationScript.gameObject;
		StaticEntityLocationCache.Prepare(loadedLocationScript);
		
		// Find the LocationPreset for the current location
		var presetPair = locationPresets.FirstOrDefault(kvp => kvp.Key == currentLocationName);
		if (presetPair.Equals(default(KeyValuePair<string, LocationPreset>)))
		{
			Debug.LogError($"[MainScene] LocationPreset not found for {currentLocationName}. Setup aborted.");
			return;
		}
		
		LocationPreset currentPreset = presetPair.Value;
		
		currentLocationScript.Setup(simulationModeManager.mainGui.Q<VisualElement>("TurbidityRow"), currentPreset, simulationModeManager.turbidityPerView, simulationModeManager.views.Count, this);
		
		//IsReady = true; // This will be set in WaitForLocationSetup
	}
	
	public void SpawnDynamicPreset(string name)
	{
		simulationModeManager.SpawnDynamicPreset(name);
	}

	/// <summary>
	/// Spawns a dynamic preset with a custom group display name for this session.
	/// </summary>
	/// <param name="presetName">Name of the dynamic preset</param>
	/// <param name="groupName">Display name for the group</param>
	public void SpawnDynamicPreset(string presetName, string groupName)
	{
		simulationModeManager.SpawnDynamicPreset(presetName, groupName);
	}
	
	/// <summary>
	/// Creates and spawns a new static entity group based on the specified preset.
	/// </summary>
	/// <param name="presetName">Name of the preset to use</param>
	/// <param name="groupName">Name for the new group instance</param>
	public Task SpawnStaticPreset(string presetName, string groupName)
	{
		return simulationModeManager.SpawnStaticPreset(presetName, groupName);
	}

    private void Update()
	{
		// Add main menu toggle to existing Update method
		if (Input.GetKeyDown(KeyCode.Escape))
		{
			if (currentModeManager == null || !currentModeManager.OnEscapePressed())
			{
				ToggleMainMenu();
			}
		}

		if (Input.GetKeyDown(KeyCode.F11))
		{
			isHudless = !isHudless;
			if (currentModeManager != null)
			{
				currentModeManager.SetHudless(isHudless);
			}
		}

		currentModeManager?.OnUpdate();
	}

	public static float CalculateCullingMaxDistance(float meshLargestDimension, float startMeshSize, float startDistance, float endMeshSize, float endDistance)
	{
		if (endMeshSize == startMeshSize)
		{
			return endDistance;
		}

		if (meshLargestDimension <= startMeshSize)
		{
			return startDistance;
		}

		if (meshLargestDimension >= endMeshSize)
		{
			return endDistance;
		}

		float t = (meshLargestDimension - startMeshSize) / (endMeshSize - startMeshSize);
		return math.lerp(startDistance, endDistance, t);
	}

	/// <summary>
	/// Updates the number of active views and adjusts the UI accordingly.
	/// </summary>
	/// <param name="viewCount">Desired number of views (1-4)</param>
	public void SetViewCountAndUpdateGUIState(int viewCount)
	{
		simulationModeManager.SetViewCountAndUpdateGUIState(viewCount);
	}

	/// <summary>
	/// Sets up the main menu, Settings panel, and their callbacks once.
	/// </summary>
	private void SetupMainMenu()
	{
		Debug.Assert(mainMenuUIDocument != null, "[MainScene] Main menu UI document GameObject is required.");
		UIDocument document = mainMenuUIDocument.GetComponent<UIDocument>();
		Debug.Assert(document != null, "[MainScene] Main menu requires a UIDocument component.");
		mainMenuRoot = document.rootVisualElement;
		Debug.Assert(mainMenuRoot != null, "[MainScene] Main menu root is required.");

		Button closeButton = mainMenuRoot.Q<Button>("CloseMenuButton");
		Button settingsButton = mainMenuRoot.Q<Button>("SettingsButton");
		Button closeSettingsButton = mainMenuRoot.Q<Button>("CloseSettingsButton");
		Button closeAppButton = mainMenuRoot.Q<Button>("CloseAppButton");
		assetBrowserButton = mainMenuRoot.Q<Button>("AssetBrowserButton");
		simulationModeButton = mainMenuRoot.Q<Button>("SimulationModeButton");
		mainMenuWindow = mainMenuRoot.Q<VisualElement>("MainMenuWindow");
		mainMenuNavigationPanel = mainMenuRoot.Q<VisualElement>("MainMenuNavigationPanel");
		settingsPanel = mainMenuRoot.Q<VisualElement>("SettingsPanel");
		entityHoverEnabledToggle = mainMenuRoot.Q<Toggle>("EntityHoverEnabledToggle");
		fpsCounterEnabledToggle = mainMenuRoot.Q<Toggle>("FpsCounterEnabledToggle");

		Debug.Assert(closeButton != null, "[MainScene] CloseMenuButton is required.");
		Debug.Assert(settingsButton != null, "[MainScene] SettingsButton is required.");
		Debug.Assert(closeSettingsButton != null, "[MainScene] CloseSettingsButton is required.");
		Debug.Assert(closeAppButton != null, "[MainScene] CloseAppButton is required.");
		Debug.Assert(assetBrowserButton != null, "[MainScene] AssetBrowserButton is required.");
		Debug.Assert(simulationModeButton != null, "[MainScene] SimulationModeButton is required.");
		Debug.Assert(mainMenuWindow != null, "[MainScene] MainMenuWindow is required.");
		Debug.Assert(mainMenuNavigationPanel != null, "[MainScene] MainMenuNavigationPanel is required.");
		Debug.Assert(settingsPanel != null, "[MainScene] SettingsPanel is required.");
		Debug.Assert(entityHoverEnabledToggle != null, "[MainScene] EntityHoverEnabledToggle is required.");
		Debug.Assert(fpsCounterEnabledToggle != null, "[MainScene] FpsCounterEnabledToggle is required.");

		closeButton.clicked += CloseMainMenu;
		settingsButton.clicked += OpenSettingsPanel;
		closeSettingsButton.clicked += ShowMainMenuNavigation;
		closeAppButton.clicked += Application.Quit;
		assetBrowserButton.clicked += OpenAssetBrowserMode;
		simulationModeButton.clicked += OpenSimulationMode;
		entityHoverEnabledToggle.RegisterValueChangedCallback(OnEntityHoverEnabledChanged);
		fpsCounterEnabledToggle.RegisterValueChangedCallback(OnFpsCounterEnabledChanged);
		entityHoverEnabledToggle.SetValueWithoutNotify(EntityHoverEnabled);
		fpsCounterEnabledToggle.SetValueWithoutNotify(FpsCounterEnabled);
		ShowMainMenuNavigation();
	}

	/// <summary>
	/// Toggles the visibility of the main menu and main GUI.
	/// </summary>
	public void ToggleMainMenu()
	{
		if (isMainMenuVisible)
		{
			CloseMainMenu();
			return;
		}

		isMainMenuVisible = true;
		if (currentModeManager != null)
		{
			currentModeManager.EnterMenu();
		}

		mainMenuRoot.style.display = DisplayStyle.Flex;
		ShowMainMenuNavigation();
		RefreshModeButtonVisibility();
	}

	private void OpenSettingsPanel()
	{
		mainMenuWindow.style.width = SettingsMenuWidth;
		mainMenuWindow.style.height = SettingsMenuHeight;
		mainMenuNavigationPanel.style.display = DisplayStyle.None;
		settingsPanel.style.display = DisplayStyle.Flex;
		entityHoverEnabledToggle.SetValueWithoutNotify(EntityHoverEnabled);
		fpsCounterEnabledToggle.SetValueWithoutNotify(FpsCounterEnabled);
	}

	private void ShowMainMenuNavigation()
	{
		mainMenuWindow.style.width = MainMenuWidth;
		mainMenuWindow.style.height = MainMenuHeight;
		settingsPanel.style.display = DisplayStyle.None;
		mainMenuNavigationPanel.style.display = DisplayStyle.Flex;
	}

	private void RefreshModeButtonVisibility()
	{
		assetBrowserButton.style.display = DisplayStyle.Flex;
		simulationModeButton.style.display = DisplayStyle.Flex;
		if (currentMode == AppMode.Simulation)
		{
			simulationModeButton.style.display = DisplayStyle.None;
		}
		else if (currentMode == AppMode.AssetBrowser)
		{
			assetBrowserButton.style.display = DisplayStyle.None;
		}
	}

	private void OnEntityHoverEnabledChanged(ChangeEvent<bool> evt)
	{
		EntityHoverEnabled = evt.newValue;
		SaveBooleanSetting(EntityHoverEnabledPlayerPrefsKey, EntityHoverEnabled);
	}

	private void OnFpsCounterEnabledChanged(ChangeEvent<bool> evt)
	{
		FpsCounterEnabled = evt.newValue;
		fpsCounter.SetVisible(FpsCounterEnabled);
		SaveBooleanSetting(FpsCounterEnabledPlayerPrefsKey, FpsCounterEnabled);
	}

	private static void SaveBooleanSetting(string playerPrefsKey, bool value)
	{
		int savedValue = 0;
		if (value)
		{
			savedValue = 1;
		}
		PlayerPrefs.SetInt(playerPrefsKey, savedValue);
		PlayerPrefs.Save();
	}

	private void OpenAssetBrowserMode()
	{
		SwitchMode(AppMode.AssetBrowser);
		CloseMainMenu();
	}

	private void OpenSimulationMode()
	{
		SwitchMode(AppMode.Simulation);
		CloseMainMenu();
	}

	/// <summary>
	/// Closes the main menu and returns to the main GUI
	/// </summary>
	private void CloseMainMenu()
	{
		isMainMenuVisible = false;
		mainMenuRoot.style.display = DisplayStyle.None;
		
		// Re-enter the current mode to show its GUI
		if (currentModeManager != null)
		{
			currentModeManager.ExitMenu();
		}
	}

	public void SwitchMode(AppMode newMode)
	{
		if (currentModeManager != null)
		{
			currentModeManager.ExitMode();
		}

		switch (newMode)
		{
			case AppMode.Simulation:
				currentModeManager = simulationModeManager;
				break;
			case AppMode.AssetBrowser:
				currentModeManager = assetBrowserModeManager;
				break;
		}

		currentMode = newMode;
		currentModeManager.EnterMode();
		currentModeManager.SetHudless(isHudless);
	}

	public void SwitchLocation(string locationName)
	{
		IsReady = false;
		try
		{
			UnloadLocation();
			LoadLocation(locationName);
		}
		finally
		{
			IsReady = true;
		}
	}
}
}
