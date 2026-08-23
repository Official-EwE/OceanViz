using System;
using System.Collections.Generic;
using OceanViz3;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using UnityEngine.UIElements;

/// <summary>
/// Represents a location preset with values like turbidity color settings
/// </summary>
[Serializable]
public struct LocationPreset
{
    public string name;
    public int wind_turbine_pylon_amount;
}

/// <summary>
/// Represents a flora habitat preset for a location with associated splatmap texture. The splatmap is used to place the flora on the terrain.
/// </summary>
[Serializable]
public struct FloraHabitatPreset
{
    public string name;
    public Texture2D splatmap;
}

/// <summary>
/// Manages location-specific settings and behaviors including terrain, turbidity effects, and habitat management.
/// This script handles the initialization and updates of post-processing effects for water turbidity visualization,
/// including depth-aware bloom weighting controls. The fullscreen materials are cloned from the configured renderer
/// data features so each loaded location can safely override effect parameters at runtime.
/// </summary>
public class LocationScript : MonoBehaviour
{
    /// <summary>
    /// Indicates whether the LocationScript has completed initialization and is ready for use
    /// </summary>
    public static bool IsReady { get; private set; }

    public Terrain terrain;
    [HideInInspector] public LocationPreset locationPreset;
    private LocationPreset currentLocationPreset;
    public GameObject dollyCart;
    public List<FloraHabitatPreset> habitatPresets = new List<FloraHabitatPreset>();
    
    private int viewsCount = 1;
    /// <summary>
    /// Default initial turbidity used when incoming value equals the old default (0.5) or is unavailable
    /// </summary>
    private const float DefaultInitialTurbidity = 0.25f;
    /// <summary>
    /// Current Universal Render Pipeline (URP) asset configuration used by the location.
    /// </summary>
    public UniversalRenderPipelineAsset _pipelineAssetCurrent;
    /// <summary>
    /// Renderer data asset that owns the fullscreen renderer features used by this location.
    /// </summary>
    public ScriptableRendererData pipelineRendererData;
    private bool materialsInitialized = false;
    private MainScene mainScene;
    
    // GUI
    public VisualElement turbidityRow;
    public List<Slider> turbiditySliders = new List<Slider>();
    
    // Post effect materials
    private Material turbidityMaterial;
    /// <summary>
    /// Original turbidity material. Used in editor to reset the material to starting state 
    /// </summary>
    private Material originalTurbidityMaterial;
    private Material bloomMaterial;
    /// <summary>
    /// Original bloom material. Used in editor to reset the material to starting state
    /// </summary>
    private Material originalBloomMaterial;
    private FullScreenPassRendererFeature turbidityFeature;
    private FullScreenPassRendererFeature bloomFeature;

#if UNITY_EDITOR
    [Header("Editor Debug")]
    [SerializeField]
    private Material debugTurbidityMaterial;
    [SerializeField]
    private Material debugBloomMaterial;

    public Material DebugTurbidityMaterial
    {
        get { return debugTurbidityMaterial; }
    }

    public Material DebugBloomMaterial
    {
        get { return debugBloomMaterial; }
    }
#endif
    
    // Turbidity colors applied to the turbidity post effect material on load
    public Color colorAtTurbidityValueOne = new Color32(0x30, 0x5C, 0x54, 0xFF);     // #305C54 for value +1
    public Color colorAtTurbidityValueZero = new Color32(0x26, 0x74, 0x7F, 0xFF);    // #26747F for value 0
    public Color colorAtTurbidityValueMinusOne = new Color32(0x57, 0x48, 0x26, 0xFF); // #574826 for value -1

    [Header("Bloom Settings (Turbidity)")]
    public float bloomCutoffAtTurbidityValueOne = 0.0f;
    public float bloomCutoffAtTurbidityValueZero = 0.0f;
    public float bloomCutoffAtTurbidityValueMinusOne = 0.0f;
    public float bloomWeightAtTurbidityValueOne = 1.0f;
    public float bloomWeightAtTurbidityValueZero = 0.0f;
    public float bloomWeightAtTurbidityValueMinusOne = 1.0f;
    public float bloomOffsetAtTurbidityValueOne = 20.0f;
    public float bloomOffsetAtTurbidityValueZero = 0.2f;
    public float bloomOffsetAtTurbidityValueMinusOne = 20.0f;
    [Range(1, 16)] public int bloomSampleCount = 4;
    
    [Header("Bloom Settings (Depth Weight)")]
    [Range(0.0f, 1.0f)] public float depthBloomEnabled = 1.0f;
    [Range(0.0f, 1.0f)] public float depthBloomNear = 0.0f;
    [Range(0.0f, 1.0f)] public float depthBloomFar = 1.0f;
    public float depthBloomWeightNear = 0.0f;
    public float depthBloomWeightFar = 30.0f;
    [Min(0.01f)] public float depthBloomExponent = 1.0f;

    [Header("Water Current")]
    public bool waterCurrentEnabled = true;
    [Min(2)] public int waterCurrentNoiseTextureSize = 512;
    [Min(0.0001f)] public float waterCurrentNoiseTextureFeatureScale = 50.0f;
    [Min(0.0001f)] public float waterCurrentNoiseScale = 400.0f;
    [Min(0.0f)] public float waterCurrentNoiseSpeed = 0.02f;
    [FormerlySerializedAs("waterCurrentStrengthMultiplier")]
    [Min(0.0f)] public float waterCurrentBoidStrengthMultiplier = 5.0f;
    [Min(0.0f)] public float waterCurrentStaticShaderStrengthMultiplier = 4.0f;
    [Min(0.0f)] public float waterCurrentStaticLongPlantDamping = 0.02f;
    [Min(0.0001f)] public float waterCurrentSecondaryNoiseScale = 1.0f;
    [Min(0.0f)] public float waterCurrentSecondaryNoiseSpeed = 0.24f;
    [Min(0.0f)] public float waterCurrentSecondaryNoiseStrength = 0.34f;
    public float waterCurrentSecondaryNoiseSeed = 17.0f;
    [Min(0.0f)] public float waterCurrentMaxAffectedBoidSize = 1.0f;
    public Vector2 waterCurrentHorizontalDirection = new Vector2(1.0f, -2.17f);

    private bool bloomSettingsInitialized = false;
    private float lastBloomCutoffAtTurbidityValueOne;
    private float lastBloomCutoffAtTurbidityValueZero;
    private float lastBloomCutoffAtTurbidityValueMinusOne;
    private float lastBloomWeightAtTurbidityValueOne;
    private float lastBloomWeightAtTurbidityValueZero;
    private float lastBloomWeightAtTurbidityValueMinusOne;
    private float lastBloomOffsetAtTurbidityValueOne;
    private float lastBloomOffsetAtTurbidityValueZero;
    private float lastBloomOffsetAtTurbidityValueMinusOne;
    private int lastBloomSampleCount;
    private float lastDepthBloomEnabled;
    private float lastDepthBloomNear;
    private float lastDepthBloomFar;
    private float lastDepthBloomWeightNear;
    private float lastDepthBloomWeightFar;
    private float lastDepthBloomExponent;
    private bool waterCurrentSettingsInitialized = false;
    private bool lastWaterCurrentEnabled;
    private int lastWaterCurrentNoiseTextureSize;
    private float lastWaterCurrentNoiseTextureFeatureScale;
    private float lastWaterCurrentNoiseScale;
    private float lastWaterCurrentNoiseSpeed;
    private float lastWaterCurrentBoidStrengthMultiplier;
    private float lastWaterCurrentStaticShaderStrengthMultiplier;
    private float lastWaterCurrentStaticLongPlantDamping;
    private float lastWaterCurrentSecondaryNoiseScale;
    private float lastWaterCurrentSecondaryNoiseSpeed;
    private float lastWaterCurrentSecondaryNoiseStrength;
    private float lastWaterCurrentSecondaryNoiseSeed;
    private float lastWaterCurrentMaxAffectedBoidSize;
    private Vector2 lastWaterCurrentHorizontalDirection;
    
    // Removed camera-driven turbidity color logic
    
    /// <summary>
    /// Initializes the location, sets up terrain settings, and prepares post-processing materials.
    /// Notifies MainScene when initialization is complete.
    /// </summary>
    void Start()
    {
        IsReady = false;
        Debug.Assert(terrain != null, "LocationScript requires a Terrain reference");
        Debug.Assert(_pipelineAssetCurrent != null, "LocationScript requires a URP asset reference");
        Debug.Assert(pipelineRendererData != null, "LocationScript requires a ScriptableRendererData reference");
        
        SceneManager.SetActiveScene(gameObject.scene);
        
        //// Terrain
        // Set terrain detail scattering mode
        terrain.terrainData.SetDetailScatterMode(DetailScatterMode.CoverageMode);
        
        InitializeRendererFeatureMaterials();
        ApplyWaterCurrentSettingsIfChanged();

#if UNITY_EDITOR
        debugTurbidityMaterial = turbidityMaterial;
        debugBloomMaterial = bloomMaterial;
#endif
        
        // After all is loaded, notify the MainScene script. It will run Setup()
        GameObject mainSceneScript = GameObject.Find("MainSceneScript");
        if (mainSceneScript != null)
        {
            Debug.Log("MainSceneScript found by LocationScript");
            
            // Send the loaded location Scene to the MainScene script
            mainSceneScript.GetComponent<MainScene>().OnLocationLoaded(this);
        }
        else
        {
            Debug.LogError("MainSceneScript not found by LocationScript");
        }
    }

    private void InitializeRendererFeatureMaterials()
    {
        turbidityFeature = GetFullScreenPassRendererFeature("Turbidity");
        bloomFeature = GetFullScreenPassRendererFeature("Bloom");

        Debug.Assert(turbidityFeature != null, "LocationScript could not find the Turbidity renderer feature");
        Debug.Assert(bloomFeature != null, "LocationScript could not find the Bloom renderer feature");
        Debug.Assert(turbidityFeature.passMaterial != null, "Turbidity renderer feature is missing its material");
        Debug.Assert(bloomFeature.passMaterial != null, "Bloom renderer feature is missing its material");

        originalTurbidityMaterial = turbidityFeature.passMaterial;
        originalBloomMaterial = bloomFeature.passMaterial;

        turbidityMaterial = new Material(originalTurbidityMaterial);
        bloomMaterial = new Material(originalBloomMaterial);

        turbidityFeature.passMaterial = turbidityMaterial;
        bloomFeature.passMaterial = bloomMaterial;
        materialsInitialized = true;
    }

    private FullScreenPassRendererFeature GetFullScreenPassRendererFeature(string featureName)
    {
        Debug.Assert(pipelineRendererData != null, "LocationScript requires a renderer data asset before loading features");

        foreach (ScriptableRendererFeature rendererFeature in pipelineRendererData.rendererFeatures)
        {
            if (rendererFeature == null)
            {
                continue;
            }

            if (rendererFeature.name != featureName)
            {
                continue;
            }

            FullScreenPassRendererFeature fullScreenPassRendererFeature = rendererFeature as FullScreenPassRendererFeature;
            Debug.Assert(fullScreenPassRendererFeature != null, "Renderer feature " + featureName + " must be a FullScreenPassRendererFeature");
            return fullScreenPassRendererFeature;
        }

        Debug.Assert(false, "LocationScript could not find renderer feature " + featureName);
        return null;
    }
    
    /// <summary>
    /// Sets up the location with specified parameters and initializes turbidity controls
    /// </summary>
    public void Setup(VisualElement turbidityRow, LocationPreset locationPreset, List<float> turbidityPerView, int viewsCount, MainScene mainScene)
    {
        IsReady = false;
        this.locationPreset = locationPreset;
        this.currentLocationPreset = locationPreset;
        this.viewsCount = viewsCount;
        this.mainScene = mainScene;
        
        // GUI
        this.turbidityRow = turbidityRow;
        
        // Turbidity Sliders
        for (int i = 0; i < 4; i++)
        {
            Slider slider = turbidityRow.Q<Slider>("TurbiditySlider" + i);
            if (slider != null)
            {
                slider.RegisterValueChangedCallback((evt) => mainScene.simulationModeManager.OnTurbiditySliderValueChanged(evt));
                turbiditySliders.Add(slider);
            }
            
            // If it's not the first slider, hide it
            if (i >= viewsCount)
            {
                if (slider != null) slider.style.display = DisplayStyle.None;
            }
            
            // Set initial turbidity value
            float initialTurbidity = DefaultInitialTurbidity;
            if (turbidityPerView != null)
            {
                if (i < turbidityPerView.Count)
                {
                    initialTurbidity = turbidityPerView[i];
                }
            }
            if (Mathf.Approximately(initialTurbidity, 0.5f))
            {
                initialTurbidity = DefaultInitialTurbidity;
            }
            SetTurbidityForView(i, initialTurbidity);
        }
        
        if (materialsInitialized)
        {
            ApplyWaterCurrentSettingsIfChanged();
            ApplyTurbidityColorsIfAvailable();
            UpdateTurbidity();
            IsReady = true;
            Debug.Log("LocationScript is now ready");
        }
    }

    // Removed Update() turbidity color adjustments

    void Update()
    {
        if (turbidityMaterial == null)
        {
            return;
        }
        if (mainScene == null || mainScene.mainCamera == null)
        {
            return;
        }

        float cameraY = mainScene.mainCamera.transform.position.y;
        float t = Mathf.InverseLerp(-10f, -180f, cameraY);
        float lightAmount = Mathf.Lerp(1.0f, 0.22f, t);
        turbidityMaterial.SetFloat("_LightAmount", lightAmount);

        if (HaveBloomSettingsChanged())
        {
            UpdateTurbidity();
        }

        ApplyWaterCurrentSettingsIfChanged();
    }

    private bool HaveWaterCurrentSettingsChanged()
    {
        if (waterCurrentSettingsInitialized == false)
        {
            return true;
        }

        if (waterCurrentEnabled != lastWaterCurrentEnabled)
        {
            return true;
        }

        if (waterCurrentNoiseTextureSize != lastWaterCurrentNoiseTextureSize)
        {
            return true;
        }

        if (!Mathf.Approximately(waterCurrentNoiseTextureFeatureScale, lastWaterCurrentNoiseTextureFeatureScale))
        {
            return true;
        }

        if (!Mathf.Approximately(waterCurrentNoiseScale, lastWaterCurrentNoiseScale))
        {
            return true;
        }

        if (!Mathf.Approximately(waterCurrentNoiseSpeed, lastWaterCurrentNoiseSpeed))
        {
            return true;
        }

        if (!Mathf.Approximately(waterCurrentBoidStrengthMultiplier, lastWaterCurrentBoidStrengthMultiplier))
        {
            return true;
        }

        if (!Mathf.Approximately(waterCurrentStaticShaderStrengthMultiplier, lastWaterCurrentStaticShaderStrengthMultiplier))
        {
            return true;
        }

        if (!Mathf.Approximately(waterCurrentStaticLongPlantDamping, lastWaterCurrentStaticLongPlantDamping))
        {
            return true;
        }

        if (!Mathf.Approximately(waterCurrentSecondaryNoiseScale, lastWaterCurrentSecondaryNoiseScale))
        {
            return true;
        }

        if (!Mathf.Approximately(waterCurrentSecondaryNoiseSpeed, lastWaterCurrentSecondaryNoiseSpeed))
        {
            return true;
        }

        if (!Mathf.Approximately(waterCurrentSecondaryNoiseStrength, lastWaterCurrentSecondaryNoiseStrength))
        {
            return true;
        }

        if (!Mathf.Approximately(waterCurrentSecondaryNoiseSeed, lastWaterCurrentSecondaryNoiseSeed))
        {
            return true;
        }

        if (!Mathf.Approximately(waterCurrentMaxAffectedBoidSize, lastWaterCurrentMaxAffectedBoidSize))
        {
            return true;
        }

        if ((waterCurrentHorizontalDirection - lastWaterCurrentHorizontalDirection).sqrMagnitude > 0.000001f)
        {
            return true;
        }

        return false;
    }

    private void ApplyWaterCurrentSettingsIfChanged()
    {
        if (HaveWaterCurrentSettingsChanged() == false)
        {
            return;
        }

        Debug.Assert(waterCurrentNoiseTextureSize > 1, "LocationScript water current noise texture size must be greater than 1");
        Debug.Assert(waterCurrentNoiseTextureFeatureScale > 0.0f, "LocationScript water current texture feature scale must be positive");
        Debug.Assert(waterCurrentNoiseScale > 0.0f, "LocationScript water current noise scale must be positive");
        Debug.Assert(waterCurrentNoiseSpeed >= 0.0f, "LocationScript water current noise speed cannot be negative");
        Debug.Assert(waterCurrentBoidStrengthMultiplier >= 0.0f, "LocationScript water current boid strength cannot be negative");
        Debug.Assert(waterCurrentStaticShaderStrengthMultiplier >= 0.0f, "LocationScript water current static shader strength cannot be negative");
        Debug.Assert(waterCurrentStaticLongPlantDamping >= 0.0f, "LocationScript water current static long plant damping cannot be negative");
        Debug.Assert(waterCurrentSecondaryNoiseScale > 0.0f, "LocationScript water current secondary noise scale must be positive");
        Debug.Assert(waterCurrentSecondaryNoiseSpeed >= 0.0f, "LocationScript water current secondary noise speed cannot be negative");
        Debug.Assert(waterCurrentSecondaryNoiseStrength >= 0.0f, "LocationScript water current secondary noise strength cannot be negative");
        Debug.Assert(waterCurrentMaxAffectedBoidSize >= 0.0f, "LocationScript water current max affected boid size cannot be negative");
        Debug.Assert(waterCurrentHorizontalDirection.sqrMagnitude > 0.000001f, "LocationScript water current direction cannot be zero");

        NoiseTextureManager noiseTextureManager = NoiseTextureManager.Instance;
        Debug.Assert(noiseTextureManager != null, "LocationScript requires a NoiseTextureManager instance for water current settings");
        noiseTextureManager.ApplyLocationWaterCurrentSettings(
            waterCurrentEnabled,
            waterCurrentNoiseTextureSize,
            waterCurrentNoiseTextureFeatureScale,
            waterCurrentNoiseScale,
            waterCurrentNoiseSpeed,
            waterCurrentBoidStrengthMultiplier,
            waterCurrentStaticShaderStrengthMultiplier,
            waterCurrentStaticLongPlantDamping,
            waterCurrentSecondaryNoiseScale,
            waterCurrentSecondaryNoiseSpeed,
            waterCurrentSecondaryNoiseStrength,
            waterCurrentSecondaryNoiseSeed,
            waterCurrentMaxAffectedBoidSize,
            waterCurrentHorizontalDirection);

        ApplyWaterCurrentSettingsToStaticEntityMaterials();
        CacheWaterCurrentSettings();
    }

    public void ApplyWaterCurrentSettingsToStaticEntityGroup(StaticEntitiesGroup staticEntitiesGroup)
    {
        Debug.Assert(staticEntitiesGroup != null, "LocationScript requires a static entity group before applying water current shader settings");
        if (staticEntitiesGroup == null)
        {
            return;
        }

        staticEntitiesGroup.ApplyWaterCurrentShaderSettings(
            waterCurrentNoiseScale,
            waterCurrentNoiseSpeed);
    }

    private void ApplyWaterCurrentSettingsToStaticEntityMaterials()
    {
        MainScene targetMainScene = mainScene;
        if (targetMainScene == null)
        {
            targetMainScene = UnityEngine.Object.FindFirstObjectByType<MainScene>();
        }

        Debug.Assert(targetMainScene != null, "LocationScript requires MainScene before applying water current shader settings to static entities");
        if (targetMainScene == null)
        {
            return;
        }

        Debug.Assert(targetMainScene.simulationModeManager != null, "LocationScript requires SimulationModeManager before applying water current shader settings to static entities");
        if (targetMainScene.simulationModeManager == null)
        {
            return;
        }

        foreach (StaticEntitiesGroup staticEntitiesGroup in targetMainScene.simulationModeManager.staticEntitiesGroups)
        {
            if (staticEntitiesGroup == null)
            {
                continue;
            }

            ApplyWaterCurrentSettingsToStaticEntityGroup(staticEntitiesGroup);
        }
    }

    private void CacheWaterCurrentSettings()
    {
        waterCurrentSettingsInitialized = true;
        lastWaterCurrentEnabled = waterCurrentEnabled;
        lastWaterCurrentNoiseTextureSize = waterCurrentNoiseTextureSize;
        lastWaterCurrentNoiseTextureFeatureScale = waterCurrentNoiseTextureFeatureScale;
        lastWaterCurrentNoiseScale = waterCurrentNoiseScale;
        lastWaterCurrentNoiseSpeed = waterCurrentNoiseSpeed;
        lastWaterCurrentBoidStrengthMultiplier = waterCurrentBoidStrengthMultiplier;
        lastWaterCurrentStaticShaderStrengthMultiplier = waterCurrentStaticShaderStrengthMultiplier;
        lastWaterCurrentStaticLongPlantDamping = waterCurrentStaticLongPlantDamping;
        lastWaterCurrentSecondaryNoiseScale = waterCurrentSecondaryNoiseScale;
        lastWaterCurrentSecondaryNoiseSpeed = waterCurrentSecondaryNoiseSpeed;
        lastWaterCurrentSecondaryNoiseStrength = waterCurrentSecondaryNoiseStrength;
        lastWaterCurrentSecondaryNoiseSeed = waterCurrentSecondaryNoiseSeed;
        lastWaterCurrentMaxAffectedBoidSize = waterCurrentMaxAffectedBoidSize;
        lastWaterCurrentHorizontalDirection = waterCurrentHorizontalDirection;
    }

    private static void SetMaterialColorIfExists(Material targetMaterial, string propertyName, Color color)
    {
        if (targetMaterial.HasProperty(propertyName))
        {
            targetMaterial.SetColor(propertyName, color);
        }
    }
    
    // Applies the public colors to the turbidity material if the shader defines the properties
    private void ApplyTurbidityColorsIfAvailable()
    {
        if (turbidityMaterial == null)
        {
            return;
        }
        
        // Support both underscored and non-underscored property naming
        SetMaterialColorIfExists(turbidityMaterial, "ColorAtTurbidityValueOne", colorAtTurbidityValueOne);
        SetMaterialColorIfExists(turbidityMaterial, "_ColorAtTurbidityValueOne", colorAtTurbidityValueOne);
        
        SetMaterialColorIfExists(turbidityMaterial, "ColorAtTurbidityValueZero", colorAtTurbidityValueZero);
        SetMaterialColorIfExists(turbidityMaterial, "_ColorAtTurbidityValueZero", colorAtTurbidityValueZero);
        
        SetMaterialColorIfExists(turbidityMaterial, "ColorAtTurbidityValueMinusOne", colorAtTurbidityValueMinusOne);
        SetMaterialColorIfExists(turbidityMaterial, "_ColorAtTurbidityValueMinusOne", colorAtTurbidityValueMinusOne);
    }

    private bool HaveBloomSettingsChanged()
    {
        if (!bloomSettingsInitialized)
        {
            return true;
        }

        if (!Mathf.Approximately(bloomCutoffAtTurbidityValueOne, lastBloomCutoffAtTurbidityValueOne))
        {
            return true;
        }
        if (!Mathf.Approximately(bloomCutoffAtTurbidityValueZero, lastBloomCutoffAtTurbidityValueZero))
        {
            return true;
        }
        if (!Mathf.Approximately(bloomCutoffAtTurbidityValueMinusOne, lastBloomCutoffAtTurbidityValueMinusOne))
        {
            return true;
        }
        if (!Mathf.Approximately(bloomWeightAtTurbidityValueOne, lastBloomWeightAtTurbidityValueOne))
        {
            return true;
        }
        if (!Mathf.Approximately(bloomWeightAtTurbidityValueZero, lastBloomWeightAtTurbidityValueZero))
        {
            return true;
        }
        if (!Mathf.Approximately(bloomWeightAtTurbidityValueMinusOne, lastBloomWeightAtTurbidityValueMinusOne))
        {
            return true;
        }
        if (!Mathf.Approximately(bloomOffsetAtTurbidityValueOne, lastBloomOffsetAtTurbidityValueOne))
        {
            return true;
        }
        if (!Mathf.Approximately(bloomOffsetAtTurbidityValueZero, lastBloomOffsetAtTurbidityValueZero))
        {
            return true;
        }
        if (!Mathf.Approximately(bloomOffsetAtTurbidityValueMinusOne, lastBloomOffsetAtTurbidityValueMinusOne))
        {
            return true;
        }
        if (bloomSampleCount != lastBloomSampleCount)
        {
            return true;
        }
        if (!Mathf.Approximately(depthBloomEnabled, lastDepthBloomEnabled))
        {
            return true;
        }
        if (!Mathf.Approximately(depthBloomNear, lastDepthBloomNear))
        {
            return true;
        }
        if (!Mathf.Approximately(depthBloomFar, lastDepthBloomFar))
        {
            return true;
        }
        if (!Mathf.Approximately(depthBloomWeightNear, lastDepthBloomWeightNear))
        {
            return true;
        }
        if (!Mathf.Approximately(depthBloomWeightFar, lastDepthBloomWeightFar))
        {
            return true;
        }
        if (!Mathf.Approximately(depthBloomExponent, lastDepthBloomExponent))
        {
            return true;
        }

        return false;
    }

    private void MarkBloomSettingsApplied()
    {
        bloomSettingsInitialized = true;
        lastBloomCutoffAtTurbidityValueOne = bloomCutoffAtTurbidityValueOne;
        lastBloomCutoffAtTurbidityValueZero = bloomCutoffAtTurbidityValueZero;
        lastBloomCutoffAtTurbidityValueMinusOne = bloomCutoffAtTurbidityValueMinusOne;
        lastBloomWeightAtTurbidityValueOne = bloomWeightAtTurbidityValueOne;
        lastBloomWeightAtTurbidityValueZero = bloomWeightAtTurbidityValueZero;
        lastBloomWeightAtTurbidityValueMinusOne = bloomWeightAtTurbidityValueMinusOne;
        lastBloomOffsetAtTurbidityValueOne = bloomOffsetAtTurbidityValueOne;
        lastBloomOffsetAtTurbidityValueZero = bloomOffsetAtTurbidityValueZero;
        lastBloomOffsetAtTurbidityValueMinusOne = bloomOffsetAtTurbidityValueMinusOne;
        lastBloomSampleCount = bloomSampleCount;
        lastDepthBloomEnabled = depthBloomEnabled;
        lastDepthBloomNear = depthBloomNear;
        lastDepthBloomFar = depthBloomFar;
        lastDepthBloomWeightNear = depthBloomWeightNear;
        lastDepthBloomWeightFar = depthBloomWeightFar;
        lastDepthBloomExponent = depthBloomExponent;
    }

    private static float LerpByTurbidity(float turbidity, float valueAtMinusOne, float valueAtZero, float valueAtOne)
    {
        if (turbidity >= 0f)
        {
            return Mathf.Lerp(valueAtZero, valueAtOne, turbidity);
        }

        return Mathf.Lerp(valueAtZero, valueAtMinusOne, -turbidity);
    }
    
    /// <summary>
    /// Editor only. Resets the turbidity and bloom materials to their original state when the script is destroyed (when exiting play mode)
    /// </summary>
    private void OnDestroy()
    {
        RestoreOriginalFeatureMaterials();

        if (turbidityMaterial != null && turbidityMaterial != originalTurbidityMaterial)
        {
            Destroy(turbidityMaterial);
        }

        if (bloomMaterial != null && bloomMaterial != originalBloomMaterial)
        {
            Destroy(bloomMaterial);
        }
    }    
    
    /// <summary>
    /// Editor only. Resets the turbidity and bloom materials to their original state when the script is disabled
    /// </summary>
    private void OnDisable()
    {
        RestoreOriginalFeatureMaterials();
    }

    private void RestoreOriginalFeatureMaterials()
    {
        if (turbidityFeature != null && originalTurbidityMaterial != null)
        {
            turbidityFeature.passMaterial = originalTurbidityMaterial;
        }

        if (bloomFeature != null && originalBloomMaterial != null)
        {
            bloomFeature.passMaterial = originalBloomMaterial;
        }
    }
    
    /// <summary>
    /// Updates the number of active views and adjusts UI elements accordingly
    /// </summary>
    public void UpdateViewsCount(int incViewsCount)
    {
        viewsCount = incViewsCount;
        
        // Change the sliders visibility according to the amount of views
        for (int i = 0; i < 4; i++)
        {
            if (i < incViewsCount)
            {
                if (i < turbiditySliders.Count) turbiditySliders[i].style.display = DisplayStyle.Flex;
            }
            else
            {
                if (i < turbiditySliders.Count) turbiditySliders[i].style.display = DisplayStyle.None;
            }
        }
        
        UpdateTurbidity();
    }
    


    /// <summary>
    /// Sets the turbidity value for a specific view
    /// </summary>
    /// <param name="viewIndex">Index of the view (0-3)</param>
    /// <param name="turbidityValue">Turbidity value between 0 and 1</param>
    public void SetTurbidityForView(int viewIndex, float turbidityValue)
    {
        // Clamp value between -1 and 1
        turbidityValue = Mathf.Clamp(turbidityValue, -1f, 1f);
        
        // Update the turbidity value in the SimulationModeManager
        if (mainScene == null)
        {
            mainScene = UnityEngine.Object.FindFirstObjectByType<MainScene>();
        }

        if (mainScene != null && mainScene.simulationModeManager != null)
        {
            mainScene.simulationModeManager.turbidityPerView[viewIndex] = turbidityValue;
        }
        
        // Update the GUI slider if it exists
        if (viewIndex < turbiditySliders.Count)
        {
            turbiditySliders[viewIndex].SetValueWithoutNotify(turbidityValue);
        }
        
        UpdateTurbidity();
    }
    
    /// <summary>
    /// Updates turbidity effects and per-view bloom settings for all active views
    /// </summary>
    public void UpdateTurbidity()
    {
        if (turbidityMaterial == null)
        {
            Debug.LogError("turbidityMaterial is null");
            return;
        }
        
        if (bloomMaterial == null)
        {
            Debug.LogError("bloomMaterial is null");
            return;
        }
        
        if (mainScene == null)
        {
            mainScene = UnityEngine.Object.FindFirstObjectByType<MainScene>();
        }

        if (mainScene == null || mainScene.simulationModeManager == null)
        {
            Debug.LogError("SimulationModeManager not found");
            return;
        }

        Debug.Assert(bloomMaterial.HasProperty("_DepthBloomEnabled"), "Bloom material is missing _DepthBloomEnabled");
        Debug.Assert(bloomMaterial.HasProperty("_DepthBloomNear"), "Bloom material is missing _DepthBloomNear");
        Debug.Assert(bloomMaterial.HasProperty("_DepthBloomFar"), "Bloom material is missing _DepthBloomFar");
        Debug.Assert(bloomMaterial.HasProperty("_DepthBloomWeightNear"), "Bloom material is missing _DepthBloomWeightNear");
        Debug.Assert(bloomMaterial.HasProperty("_DepthBloomWeightFar"), "Bloom material is missing _DepthBloomWeightFar");
        Debug.Assert(bloomMaterial.HasProperty("_DepthBloomExponent"), "Bloom material is missing _DepthBloomExponent");
        Debug.Assert(bloomMaterial.HasProperty("_BloomSampleCount"), "Bloom material is missing _BloomSampleCount");
        float clampedDepthBloomNear = Mathf.Clamp01(depthBloomNear);
        float clampedDepthBloomFar = Mathf.Clamp01(depthBloomFar);
        int clampedBloomSampleCount = Mathf.Clamp(bloomSampleCount, 1, 16);
        Debug.Assert(clampedDepthBloomFar >= clampedDepthBloomNear, "Depth bloom far must be greater than or equal to depth bloom near");
        Debug.Assert(clampedBloomSampleCount == bloomSampleCount, "Bloom sample count must be between 1 and 16");
        if (clampedDepthBloomFar < clampedDepthBloomNear)
        {
            clampedDepthBloomFar = clampedDepthBloomNear;
        }
        bloomMaterial.SetFloat("_DepthBloomEnabled", Mathf.Clamp01(depthBloomEnabled));
        bloomMaterial.SetFloat("_DepthBloomNear", clampedDepthBloomNear);
        bloomMaterial.SetFloat("_DepthBloomFar", clampedDepthBloomFar);
        bloomMaterial.SetFloat("_DepthBloomWeightNear", depthBloomWeightNear);
        bloomMaterial.SetFloat("_DepthBloomWeightFar", depthBloomWeightFar);
        bloomMaterial.SetFloat("_DepthBloomExponent", Mathf.Max(0.01f, depthBloomExponent));
        bloomMaterial.SetInt("_BloomSampleCount", clampedBloomSampleCount);

        // Set the turbidity and color values for each view
        for (int i = 0; i < viewsCount; i++)
        {
            float turbidityValue = mainScene.simulationModeManager.turbidityPerView[i];
            float bloomCutoff = LerpByTurbidity(
                turbidityValue,
                bloomCutoffAtTurbidityValueMinusOne,
                bloomCutoffAtTurbidityValueZero,
                bloomCutoffAtTurbidityValueOne);
            float bloomOffset = LerpByTurbidity(
                turbidityValue,
                bloomOffsetAtTurbidityValueMinusOne,
                bloomOffsetAtTurbidityValueZero,
                bloomOffsetAtTurbidityValueOne);
            turbidityMaterial.SetFloat("_Turbidity" + i, turbidityValue);

            float bloomWeight = LerpByTurbidity(
                turbidityValue,
                bloomWeightAtTurbidityValueMinusOne,
                bloomWeightAtTurbidityValueZero,
                bloomWeightAtTurbidityValueOne);

            string bloomCutoffPropertyName = "_BloomCutoffView" + i;
            string bloomOffsetPropertyName = "_BloomOffsetView" + i;
            string bloomWeightPropertyName = "_BloomWeightView" + i;

            Debug.Assert(bloomMaterial.HasProperty(bloomCutoffPropertyName), "Bloom material is missing " + bloomCutoffPropertyName);
            Debug.Assert(bloomMaterial.HasProperty(bloomOffsetPropertyName), "Bloom material is missing " + bloomOffsetPropertyName);
            Debug.Assert(bloomMaterial.HasProperty(bloomWeightPropertyName), "Bloom material is missing " + bloomWeightPropertyName);

            bloomMaterial.SetFloat(bloomCutoffPropertyName, bloomCutoff);
            bloomMaterial.SetFloat(bloomOffsetPropertyName, bloomOffset);
            bloomMaterial.SetFloat(bloomWeightPropertyName, bloomWeight);
        }

        // Set the mask for each view
        switch (viewsCount)
        {
            case 1:
                turbidityMaterial.SetVector("_MaskView0", new Vector2(0.0f, 1.0f));
                turbidityMaterial.SetVector("_MaskView1", new Vector2(0.0f, 0.0f));
                turbidityMaterial.SetVector("_MaskView2", new Vector2(0.0f, 0.0f));
                turbidityMaterial.SetVector("_MaskView3", new Vector2(0.0f, 0.0f));
                bloomMaterial.SetVector("_MaskView0", new Vector2(0.0f, 1.0f));
                bloomMaterial.SetVector("_MaskView1", new Vector2(0.0f, 0.0f));
                bloomMaterial.SetVector("_MaskView2", new Vector2(0.0f, 0.0f));
                bloomMaterial.SetVector("_MaskView3", new Vector2(0.0f, 0.0f));
                break;
            case 2:
                turbidityMaterial.SetVector("_MaskView0", new Vector2(0.0f, 0.5f));
                turbidityMaterial.SetVector("_MaskView1", new Vector2(0.5f, 1.0f));
                turbidityMaterial.SetVector("_MaskView2", new Vector2(0.0f, 0.0f));
                turbidityMaterial.SetVector("_MaskView3", new Vector2(0.0f, 0.0f));
                bloomMaterial.SetVector("_MaskView0", new Vector2(0.0f, 0.5f));
                bloomMaterial.SetVector("_MaskView1", new Vector2(0.5f, 1.0f));
                bloomMaterial.SetVector("_MaskView2", new Vector2(0.0f, 0.0f));
                bloomMaterial.SetVector("_MaskView3", new Vector2(0.0f, 0.0f));
                break;
            case 3:
                turbidityMaterial.SetVector("_MaskView0", new Vector2(0.0f, 0.3333f));
                turbidityMaterial.SetVector("_MaskView1", new Vector2(0.3333f, 0.6666f));
                turbidityMaterial.SetVector("_MaskView2", new Vector2(0.6666f, 1.0f));
                turbidityMaterial.SetVector("_MaskView3", new Vector2(0.0f, 0.0f));
                bloomMaterial.SetVector("_MaskView0", new Vector2(0.0f, 0.3333f));
                bloomMaterial.SetVector("_MaskView1", new Vector2(0.3333f, 0.6666f));
                bloomMaterial.SetVector("_MaskView2", new Vector2(0.6666f, 1.0f));
                bloomMaterial.SetVector("_MaskView3", new Vector2(0.0f, 0.0f));
                break;
            case 4:
                turbidityMaterial.SetVector("_MaskView0", new Vector2(0.0f, 0.25f));
                turbidityMaterial.SetVector("_MaskView1", new Vector2(0.25f, 0.5f));
                turbidityMaterial.SetVector("_MaskView2", new Vector2(0.5f, 0.75f));
                turbidityMaterial.SetVector("_MaskView3", new Vector2(0.75f, 1.0f));
                bloomMaterial.SetVector("_MaskView0", new Vector2(0.0f, 0.25f));
                bloomMaterial.SetVector("_MaskView1", new Vector2(0.25f, 0.5f));
                bloomMaterial.SetVector("_MaskView2", new Vector2(0.5f, 0.75f));
                bloomMaterial.SetVector("_MaskView3", new Vector2(0.75f, 1.0f));
                break;
        }

        MarkBloomSettingsApplied();
    }
    
    /// <summary>
    /// Retrieves boid bounds GameObjects associated with a specific habitat name
    /// </summary>
    public List<GameObject> GetBoidBoundsByBiomeName(string requestedBiomeName)
    {
        Debug.Log("Getting boidBounds for habitat name: " + requestedBiomeName);
        
        Dictionary<string, List<GameObject>> biomeNameToBoidBoundsList = new Dictionary<string, List<GameObject>>();
        
        // If the biomeName is empty, set it to "Default"
        if (requestedBiomeName == null || requestedBiomeName == "")
        {
            requestedBiomeName = "Default";
        }
        
        // Find all boidBounds in the scene and separate them by habitat name
        GameObject[] boidBoundsArray = GameObject.FindGameObjectsWithTag("BoidBounds");
        foreach (GameObject boidBounds in boidBoundsArray)
        {
            string biomeName = boidBounds.GetComponent<BoidBounds>().HabitatName;
            
            // If there is no habitat name, set it to "Default"
            if (biomeName == null || biomeName == "")
            {
                biomeName = "Default";
            }
            
            // If the habitat name is not in the dictionary, add it
            if (!biomeNameToBoidBoundsList.ContainsKey(biomeName))
            {
                biomeNameToBoidBoundsList[biomeName] = new List<GameObject>();
            }
            
            // Add the boidBounds to the list of boidBounds for the habitat
            biomeNameToBoidBoundsList[biomeName].Add(boidBounds);
            Debug.Log("Added boidBounds for habitat: " + biomeName);
        }
        
        // If the habitat name is in the dictionary, return the list of boidBounds
        if (biomeNameToBoidBoundsList.ContainsKey(requestedBiomeName))
        {
            Debug.Log("Found boidBounds amount: " + biomeNameToBoidBoundsList[requestedBiomeName].Count);
            
            return biomeNameToBoidBoundsList[requestedBiomeName];
        }
        
        // Else, throw error
        Debug.LogError("Biome not found: " + requestedBiomeName);
        return null;
    }

    /// <summary>
    /// Returns the habitat names provided by active boid boundaries in this location.
    /// Bounds with no authored habitat name provide the Default habitat.
    /// </summary>
    public HashSet<string> GetAvailableBoidHabitatNames()
    {
        HashSet<string> availableHabitats = new HashSet<string>();

        GameObject[] boidBoundsObjects = GameObject.FindGameObjectsWithTag("BoidBounds");
        foreach (GameObject boidBoundsObject in boidBoundsObjects)
        {
            BoidBounds boidBounds = boidBoundsObject.GetComponent<BoidBounds>();
            Debug.Assert(boidBounds != null, "Every GameObject tagged BoidBounds requires a BoidBounds component.");
            if (boidBounds == null)
            {
                continue;
            }

            string habitatName = boidBounds.HabitatName;
            if (string.IsNullOrEmpty(habitatName))
            {
                habitatName = "Default";
            }
            availableHabitats.Add(habitatName);
        }

        return availableHabitats;
    }

    /// <summary>
    /// Returns the habitat names provided by terrain splatmaps or loaded mesh habitats in this location.
    /// </summary>
    public HashSet<string> GetAvailableStaticSpawnHabitatNames()
    {
        HashSet<string> availableHabitats = new HashSet<string>();

        foreach (FloraHabitatPreset habitatPreset in habitatPresets)
        {
            if (habitatPreset.splatmap != null && !string.IsNullOrEmpty(habitatPreset.name))
            {
                availableHabitats.Add(habitatPreset.name);
            }
        }

        World world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated)
        {
            return availableHabitats;
        }

        EntityQuery meshHabitatQuery = world.EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<MeshHabitatComponent>(),
            ComponentType.ReadOnly<MeshHabitatProcessedTag>(),
            ComponentType.ReadOnly<MeshHabitatBlobRef>());
        NativeArray<MeshHabitatComponent> meshHabitats = meshHabitatQuery.ToComponentDataArray<MeshHabitatComponent>(Allocator.Temp);
        foreach (MeshHabitatComponent meshHabitat in meshHabitats)
        {
            string habitatName = meshHabitat.HabitatName.ToString();
            if (!string.IsNullOrEmpty(habitatName))
            {
                availableHabitats.Add(habitatName);
            }
        }
        meshHabitats.Dispose();
        meshHabitatQuery.Dispose();

        return availableHabitats;
    }

    /// <summary>
    /// Returns the terrain component associated with this location
    /// </summary>
    public Terrain GetTerrain()
    {
        return terrain;
    }

    /// <summary>
    /// Retrieves the splatmap texture for a specific flora habitat
    /// </summary>
    /// <param name="habitat">Name of the habitat</param>
    public Texture2D GetFloraBiomeSplatmap(string habitat)
    {
        foreach (var floraBiomePreset in habitatPresets)
        {
            if (floraBiomePreset.name == habitat)
            {
                return floraBiomePreset.splatmap;
            }
        }

        return null;
    }
}
