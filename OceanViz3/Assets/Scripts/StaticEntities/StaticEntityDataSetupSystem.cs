using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine;
using Unity.Burst;
using System.Collections.Generic;
using System.Linq;
using Unity.Transforms;
using System;

namespace OceanViz3
{
    /// <summary>
    /// Owns immutable terrain height and flora splatmap data for every location visited during this app session.
    /// Location loading prepares this cache before entity groups are allowed to spawn.
    /// </summary>
    public sealed class StaticEntityLocationDataCache : IDisposable
    {
        public readonly struct SplatmapData
        {
            public readonly BlobAssetReference<ByteBlob> BlobRef;
            public readonly int Width;
            public readonly int Height;
            public readonly int FallbackIndex;

            public SplatmapData(
                BlobAssetReference<ByteBlob> blobRef,
                int width,
                int height,
                int fallbackIndex)
            {
                BlobRef = blobRef;
                Width = width;
                Height = height;
                FallbackIndex = fallbackIndex;
            }
        }

        public sealed class LocationData
        {
            public readonly BlobAssetReference<FloatBlob> HeightmapBlobRef;
            public readonly int HeightmapWidth;
            public readonly int HeightmapHeight;
            public readonly Dictionary<string, SplatmapData> Splatmaps;

            public LocationData(
                BlobAssetReference<FloatBlob> heightmapBlobRef,
                int heightmapWidth,
                int heightmapHeight,
                Dictionary<string, SplatmapData> splatmaps)
            {
                HeightmapBlobRef = heightmapBlobRef;
                HeightmapWidth = heightmapWidth;
                HeightmapHeight = heightmapHeight;
                Splatmaps = splatmaps;
            }
        }

        private readonly Dictionary<string, LocationData> locations = new Dictionary<string, LocationData>();
        private bool isDisposed;

        public void Prepare(LocationScript locationScript)
        {
            Debug.Assert(!isDisposed, "Static entity location data cannot be prepared after its cache is disposed.");
            Debug.Assert(locationScript != null, "Static entity location data preparation requires a LocationScript.");

            string locationKey = GetLocationKey(locationScript);
            if (locations.ContainsKey(locationKey))
            {
                Debug.Log("[StaticEntityLocationDataCache] Reusing prepared data for " + locationKey + ".");
                return;
            }

            Terrain terrain = locationScript.GetTerrain();
            Debug.Assert(terrain != null, "Static entity location data preparation requires a Terrain.");
            Debug.Assert(terrain.terrainData != null, "Static entity location data preparation requires TerrainData.");

            TerrainData terrainData = terrain.terrainData;
            int heightmapResolution = terrainData.heightmapResolution;
            float[,] heightmapData = terrainData.GetHeights(0, 0, heightmapResolution, heightmapResolution);
            BlobAssetReference<FloatBlob> heightmapBlobRef;
            using (var blobBuilder = new BlobBuilder(Allocator.Temp))
            {
                ref FloatBlob heightmapBlobAsset = ref blobBuilder.ConstructRoot<FloatBlob>();
                int length = heightmapResolution * heightmapResolution;
                BlobBuilderArray<float> heightmapArrayBuilder = blobBuilder.Allocate(ref heightmapBlobAsset.Values, length);
                for (int y = 0; y < heightmapResolution; y++)
                {
                    for (int x = 0; x < heightmapResolution; x++)
                    {
                        heightmapArrayBuilder[y * heightmapResolution + x] = heightmapData[y, x];
                    }
                }
                heightmapBlobRef = blobBuilder.CreateBlobAssetReference<FloatBlob>(Allocator.Persistent);
            }

            Dictionary<string, SplatmapData> splatmaps = new Dictionary<string, SplatmapData>();
            foreach (FloraHabitatPreset habitatPreset in locationScript.habitatPresets)
            {
                if (string.IsNullOrEmpty(habitatPreset.name))
                {
                    Debug.LogError("[StaticEntityLocationDataCache] A flora habitat in " + locationKey + " has no name.");
                    continue;
                }

                Texture2D texture = habitatPreset.splatmap;
                if (texture == null)
                {
                    continue;
                }
                if (!texture.isReadable)
                {
                    Debug.LogError("[StaticEntityLocationDataCache] Splatmap texture '" + texture.name +
                                   "' for habitat '" + habitatPreset.name + "' is not readable.");
                    continue;
                }
                if (splatmaps.ContainsKey(habitatPreset.name))
                {
                    Debug.LogError("[StaticEntityLocationDataCache] Duplicate flora habitat '" + habitatPreset.name +
                                   "' in " + locationKey + ".");
                    continue;
                }

                Color32[] pixels = texture.GetPixels32();
                int fallbackIndex = -1;
                BlobAssetReference<ByteBlob> splatmapBlobRef;
                using (var blobBuilder = new BlobBuilder(Allocator.Temp))
                {
                    ref ByteBlob splatmapBlobAsset = ref blobBuilder.ConstructRoot<ByteBlob>();
                    BlobBuilderArray<byte> splatmapArrayBuilder = blobBuilder.Allocate(ref splatmapBlobAsset.Values, pixels.Length);
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        byte habitatWeight = pixels[i].g;
                        splatmapArrayBuilder[i] = habitatWeight;
                        if (fallbackIndex < 0 && habitatWeight >= 3)
                        {
                            fallbackIndex = i;
                        }
                    }
                    splatmapBlobRef = blobBuilder.CreateBlobAssetReference<ByteBlob>(Allocator.Persistent);
                }

                splatmaps.Add(
                    habitatPreset.name,
                    new SplatmapData(splatmapBlobRef, texture.width, texture.height, fallbackIndex));
            }

            locations.Add(
                locationKey,
                new LocationData(heightmapBlobRef, heightmapResolution, heightmapResolution, splatmaps));
            Debug.Log("[StaticEntityLocationDataCache] Prepared " + locationKey + " with " + splatmaps.Count +
                      " flora splatmap(s) before static entity spawning.");
        }

        public LocationData GetPrepared(LocationScript locationScript)
        {
            Debug.Assert(!isDisposed, "Static entity location data cannot be read after its cache is disposed.");
            string locationKey = GetLocationKey(locationScript);
            if (!locations.TryGetValue(locationKey, out LocationData locationData))
            {
                throw new InvalidOperationException("Static entity location data was not prepared for " + locationKey + ".");
            }
            return locationData;
        }

        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            foreach (LocationData locationData in locations.Values)
            {
                if (locationData.HeightmapBlobRef.IsCreated)
                {
                    locationData.HeightmapBlobRef.Dispose();
                }
                foreach (SplatmapData splatmapData in locationData.Splatmaps.Values)
                {
                    if (splatmapData.BlobRef.IsCreated)
                    {
                        splatmapData.BlobRef.Dispose();
                    }
                }
            }
            locations.Clear();
            isDisposed = true;
        }

        private static string GetLocationKey(LocationScript locationScript)
        {
            Debug.Assert(locationScript != null, "A LocationScript is required to identify cached static entity data.");
            string scenePath = locationScript.gameObject.scene.path;
            Debug.Assert(!string.IsNullOrEmpty(scenePath), "Static entity location data requires a saved scene path.");
            return scenePath;
        }
    }

    /// <summary>
    /// Component to store references to mesh habitats for a static entity group
    /// </summary>
    public struct StaticEntityMeshHabitatRefs : IComponentData
    {
        // Store up to 16 mesh habitat entity references, which should be enough for most cases
        // For more, we'd need a DynamicBuffer or a different approach
        public FixedList128Bytes<Entity> MeshHabitatEntities;
    }

    /// <summary>
    /// System responsible for gathering terrain/splatmap/noise data for StaticEntityGroups
    /// and storing it efficiently (e.g., in BlobAssets) within the StaticEntitiesGroupComponent.
    /// Also associates mesh habitats with static entity groups based on matching habitat names.
    /// Runs before StaticEntitySpawnSystem.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(StaticEntitySpawnSystem))]
    [UpdateAfter(typeof(MeshHabitatSetupSystem))]
    [RequireMatchingQueriesForUpdate]
    public partial struct StaticEntityDataSetupSystem : ISystem
    {
        private EntityQuery groupsNeedingSetupQuery;
        private EntityQuery meshHabitatsQuery;
        private BufferLookup<MeshHabitatEntityRef> meshHabitatBufferLookup;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            groupsNeedingSetupQuery = SystemAPI.QueryBuilder()
                .WithAllRW<StaticEntitiesGroupComponent>()
                .WithNone<SpawnDataReadyTag>() // Use a tag to mark completion
                .Build();
                
            // Query for *processed* mesh habitats
            meshHabitatsQuery = SystemAPI.QueryBuilder()
                .WithAll<MeshHabitatComponent, MeshHabitatProcessedTag, MeshHabitatBlobRef>()
                .Build();
            meshHabitatBufferLookup = state.GetBufferLookup<MeshHabitatEntityRef>(false);
                
            state.RequireForUpdate(groupsNeedingSetupQuery);
            // No need to require meshHabitatsQuery here, as we check its length later
            // state.RequireForUpdate(meshHabitatsQuery); 
            Debug.Log("[StaticEntityDataSetupSystem] OnCreate completed.");
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            Debug.Log("[StaticEntityDataSetupSystem] OnDestroy called.");
        }

        public void OnUpdate(ref SystemState state)
        {
            Debug.Log("[StaticEntityDataSetupSystem] OnUpdate Start"); // Log system start

            var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            meshHabitatBufferLookup.Update(ref state);

            // --- Find Main Scene/Location --- 
            GameObject mainSceneObj = GameObject.Find("MainSceneScript");
            if (mainSceneObj == null) 
            {
                Debug.LogError("[StaticEntityDataSetupSystem] MainSceneScript GameObject not found. Exiting OnUpdate.");
                return; 
            }
            MainScene mainScene = mainSceneObj.GetComponent<MainScene>();
            if (mainScene == null) 
            {
                 Debug.LogError("[StaticEntityDataSetupSystem] MainScene component not found. Exiting OnUpdate.");
                 return;
            }
            if (mainScene.currentLocationScript == null)
            {
                 Debug.LogWarning("[StaticEntityDataSetupSystem] MainScene.currentLocationScript is null. Waiting for location to load? Exiting OnUpdate.");
                 return; // Might be between location loads
            }
            LocationScript locationScript = mainScene.currentLocationScript;
            StaticEntityLocationDataCache.LocationData cachedLocationData =
                mainScene.StaticEntityLocationCache.GetPrepared(locationScript);
            Terrain terrain = locationScript.GetTerrain();
            if (terrain == null) 
            {
                 Debug.LogError("[StaticEntityDataSetupSystem] LocationScript.GetTerrain() returned null. Exiting OnUpdate.");
                 return;
            }
            if (terrain.terrainData == null)
            {
                 Debug.LogError("[StaticEntityDataSetupSystem] Terrain.terrainData is null. Exiting OnUpdate.");
                 return;
            }
            Debug.Log("[StaticEntityDataSetupSystem] Found MainScene, LocationScript, and Terrain.");

            // --- Gather Static Terrain Data --- 
            TerrainData terrainData = terrain.terrainData;
            float terrainSize = terrainData.size.x; 
            float terrainHeight = terrainData.size.y;
            float terrainOffsetX = terrain.transform.position.x;
            float terrainOffsetY = terrain.transform.position.y;
            float terrainOffsetZ = terrain.transform.position.z;

            // --- Mesh Habitat Lookup Preparation ---
            var allMeshHabitatEntities = meshHabitatsQuery.ToEntityArray(Allocator.Temp);
            var allMeshHabitatComponents = meshHabitatsQuery.ToComponentDataArray<MeshHabitatComponent>(Allocator.Temp);
            Debug.Log($"[StaticEntityDataSetupSystem] Found {allMeshHabitatEntities.Length} processed mesh habitat entities.");

            // --- Base Noise Settings --- 
            float3 baseNoiseOffset = new float3(123.45f, 678.90f, 111.22f);
            float defaultNoiseScale = 6.0f;

            // Process each group needing setup
            int groupsFound = 0;
            foreach (var (groupComponentRW, entity) in SystemAPI.Query<RefRW<StaticEntitiesGroupComponent>>()
                         .WithNone<SpawnDataReadyTag>()
                         .WithEntityAccess())
            {
                groupsFound++;
                ref var groupComponent = ref groupComponentRW.ValueRW;
                int currentGroupId = groupComponent.StaticEntitiesGroupId;
                Debug.Log($"[StaticEntityDataSetupSystem] ---------- Processing Group {currentGroupId} (Entity: {entity}) ----------");

                // --- Assign Terrain Data --- 
                groupComponent.TerrainSize = terrainSize;
                groupComponent.TerrainHeight = terrainHeight;
                groupComponent.TerrainOffsetX = terrainOffsetX;
                groupComponent.TerrainOffsetY = terrainOffsetY;
                groupComponent.TerrainOffsetZ = terrainOffsetZ;
                groupComponent.HeightmapWidth = cachedLocationData.HeightmapWidth;
                groupComponent.HeightmapHeight = cachedLocationData.HeightmapHeight;
                groupComponent.HeightmapDataBlobRef = cachedLocationData.HeightmapBlobRef;
                Debug.Log($"[StaticEntityDataSetupSystem] Group {currentGroupId}: Assigned cached terrain data and heightmap blob (IsCreated={cachedLocationData.HeightmapBlobRef.IsCreated})");
                
                // --- Get Habitat Info & Scale --- 
                var groupHabitatsBuffer = SystemAPI.GetBuffer<StaticEntityHabitat>(entity);
                var groupHabitats = new NativeHashSet<FixedString64Bytes>(groupHabitatsBuffer.Length, Allocator.Temp);
                foreach (var h in groupHabitatsBuffer)
                {
                    groupHabitats.Add(h.Name);
                }
                
                // Find the StaticEntitiesGroup MonoBehaviour in either Simulation or Asset Browser mode
                StaticEntitiesGroup staticGroupMono = null;
                if (mainScene.simulationModeManager != null)
                {
                    staticGroupMono = mainScene.simulationModeManager.staticEntitiesGroups.FirstOrDefault(g => g != null && g.StaticEntitiesGroupId == currentGroupId);
                }

                if (staticGroupMono == null && mainScene.assetBrowserModeManager != null)
                {
                    var assetBrowserGroup = mainScene.assetBrowserModeManager.GetCurrentStaticGroup();
                    if (assetBrowserGroup != null && assetBrowserGroup.StaticEntitiesGroupId == currentGroupId)
                    {
                        staticGroupMono = assetBrowserGroup;
                    }
                }
                
                if (staticGroupMono != null && staticGroupMono.staticEntityPreset != null)
                {
                    groupComponent.MinScale = staticGroupMono.staticEntityPreset.minScale > 0 ? staticGroupMono.staticEntityPreset.minScale : 0.8f;
                    groupComponent.MaxScale = staticGroupMono.staticEntityPreset.maxScale > 0 ? staticGroupMono.staticEntityPreset.maxScale : 1.2f;
                    groupComponent.Rigidity = staticGroupMono.staticEntityPreset.rigidity; // Rigidity from 0 to 1
                    Debug.Log($"[StaticEntityDataSetupSystem] Group {currentGroupId}: Found MonoBehaviour. Scale={groupComponent.MinScale:F2}-{groupComponent.MaxScale:F2}, Rigidity={groupComponent.Rigidity:F2}");
                }
                else
                {
                    Debug.LogWarning($"[StaticEntityDataSetupSystem] Group {currentGroupId}: Could not find StaticEntitiesGroup MonoBehaviour. Using default scale/rigidity.");
                    groupComponent.MinScale = 0.8f;
                    groupComponent.MaxScale = 1.2f;
                    groupComponent.Rigidity = 0.5f; // Default rigidity value
                }

                // --- Noise Setup --- 
                groupComponent.NoiseScale = defaultNoiseScale;
                groupComponent.GroupNoiseOffset = baseNoiseOffset + new float3(currentGroupId * 13.7f, currentGroupId * 29.1f, currentGroupId * 43.3f);
                Debug.Log($"[StaticEntityDataSetupSystem] Group {currentGroupId}: Set noise parameters (Scale={groupComponent.NoiseScale:F1})");

                // --- Splatmap Setup --- 
                BlobAssetReference<ByteBlob> splatmapBlobRef = BlobAssetReference<ByteBlob>.Null;
                bool useSplatmap = false;
                int splatmapWidth = 0;
                int splatmapHeight = 0;
                int fallbackSplatmapIndex = -1;
                
                string foundSplatmapHabitat = string.Empty;
                StaticEntityLocationDataCache.SplatmapData cachedSplatmapData = default;

                foreach (StaticEntityHabitat habitat in groupHabitatsBuffer)
                {
                    string habitatName = habitat.Name.ToString();
                    if (cachedLocationData.Splatmaps.TryGetValue(habitatName, out cachedSplatmapData))
                    {
                        foundSplatmapHabitat = habitatName;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(foundSplatmapHabitat))
                {
                    useSplatmap = true;
                    splatmapWidth = cachedSplatmapData.Width;
                    splatmapHeight = cachedSplatmapData.Height;
                    fallbackSplatmapIndex = cachedSplatmapData.FallbackIndex;
                    splatmapBlobRef = cachedSplatmapData.BlobRef;
                    Debug.Log($"[StaticEntityDataSetupSystem] Group {currentGroupId}: Reusing cached splatmap for habitat '{foundSplatmapHabitat}'.");
                }
                else
                {
                    Debug.LogWarning($"[StaticEntityDataSetupSystem] Group {currentGroupId}: No matching splatmap texture found for any of its habitats.");
                }

                groupComponent.UseSplatmap = useSplatmap;
                groupComponent.SplatmapWidth = splatmapWidth;
                groupComponent.SplatmapHeight = splatmapHeight;
                groupComponent.SplatmapDataBlobRef = splatmapBlobRef;
                groupComponent.FallbackSplatmapIndex = fallbackSplatmapIndex;
                Debug.Log($"[StaticEntityDataSetupSystem] Group {currentGroupId}: Splatmap setup complete (UseSplatmap={useSplatmap}, BlobCreated={splatmapBlobRef.IsCreated})");

                // --- Mesh Habitat Matching --- 
                bool useMeshHabitats = false;
                int addedMeshCount = 0;
                if (groupHabitats.Count > 0 && allMeshHabitatEntities.Length > 0)
                {
                    if (meshHabitatBufferLookup.HasBuffer(entity))
                    {
                        var meshHabitatBuffer = meshHabitatBufferLookup[entity];
                        meshHabitatBuffer.Clear();
                        for(int i = 0; i < allMeshHabitatEntities.Length; ++i)
                        {
                            if (groupHabitats.Contains(allMeshHabitatComponents[i].HabitatName))
                            {
                                meshHabitatBuffer.Add(new MeshHabitatEntityRef { MeshEntity = allMeshHabitatEntities[i] });
                                useMeshHabitats = true;
                                addedMeshCount++;
                            }
                        }
                        if (addedMeshCount > 0)
                        {
                             Debug.Log($"[StaticEntityDataSetupSystem] Group {currentGroupId}: Found and added {addedMeshCount} matching mesh habitats to buffer.");
                        }
                        else
                        {
                            Debug.Log($"[StaticEntityDataSetupSystem] Group {currentGroupId}: No mesh habitats found matching the group's habitats.");
                        }
                    }
                    else
                    {
                        // This should not happen if the buffer is added correctly in the authoring baker
                        Debug.LogError($"[StaticEntityDataSetupSystem] Group {currentGroupId}: Entity {entity} is MISSING the MeshHabitatEntityRef buffer component!");
                    }
                }
                else
                {
                     Debug.Log($"[StaticEntityDataSetupSystem] Group {currentGroupId}: Skipping mesh habitat matching (GroupHabitats={groupHabitats.Count}, AvailableMeshes={allMeshHabitatEntities.Length})");
                }
                groupComponent.UseMeshHabitats = useMeshHabitats;

                // --- Mesh Ratio Settings --- 
                if (SystemAPI.HasComponent<StaticEntityMeshSpawnSettings>(entity))
                {
                    var settings = SystemAPI.GetComponent<StaticEntityMeshSpawnSettings>(entity);
                    groupComponent.MeshHabitatRatio = settings.MeshHabitatRatio;
                    Debug.Log($"[StaticEntityDataSetupSystem] Group {currentGroupId}: Found MeshSpawnSettings, Ratio set to {settings.MeshHabitatRatio:F2}");
                }
                else
                {
                    groupComponent.MeshHabitatRatio = 0.5f; // Default if no settings component
                    Debug.Log($"[StaticEntityDataSetupSystem] Group {currentGroupId}: No MeshSpawnSettings found, using default Ratio=0.5");
                }

                // --- Mark as Ready --- 
                groupComponent.SpawnDataIsReady = true; 
                ecb.AddComponent<SpawnDataReadyTag>(entity); 
                Debug.Log($"[StaticEntityDataSetupSystem] Group {currentGroupId}: Added SpawnDataReadyTag and set SpawnDataIsReady=true.");

                Debug.Log($"[StaticEntityDataSetupSystem] ---------- FINAL Setup complete for group {currentGroupId} ---------- " +
                         $"UseSplatmap: {groupComponent.UseSplatmap}, UseMeshHabitats: {useMeshHabitats}, MeshRatio: {groupComponent.MeshHabitatRatio:F2}");
                
                // Dispose the hash set used for this group
                groupHabitats.Dispose();
            }

            if (groupsFound == 0)
            {
                Debug.Log("[StaticEntityDataSetupSystem] No groups found needing setup this frame.");
            }
            else
            {
                Debug.Log($"[StaticEntityDataSetupSystem] Processed {groupsFound} group(s) needing setup.");
            }

            // --- Cleanup --- 
            allMeshHabitatEntities.Dispose();
            allMeshHabitatComponents.Dispose();
            Debug.Log("[StaticEntityDataSetupSystem] OnUpdate End"); // Log system end
        }
    }

    /// <summary>
    /// Tag component added to StaticEntitiesGroup entities once their
    /// terrain/splatmap data has been processed by StaticEntityDataSetupSystem.
    /// </summary>
    public struct SpawnDataReadyTag : IComponentData { }
} 
