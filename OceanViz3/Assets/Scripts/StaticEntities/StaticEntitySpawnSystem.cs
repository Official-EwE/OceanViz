using Unity.Assertions;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace OceanViz3
{
    internal static class StaticEntityViewVisibilityUtility
    {
        public static bool IsVisible(int spawnSiteIndex, int groupId, float visibilityFraction)
        {
            if (visibilityFraction <= 0.0f)
            {
                return false;
            }

            if (visibilityFraction >= 1.0f)
            {
                return true;
            }

            uint visibilityHash = math.hash(new uint2((uint)spawnSiteIndex, (uint)groupId));
            float visibilityRank = visibilityHash * (1.0f / uint.MaxValue);
            return visibilityRank < visibilityFraction;
        }
    }

    /// <summary>
    /// Generates deterministic static habitat sites and streams their ECS entities around the camera.
    /// RequestedCount describes the full habitat population. Count describes only instantiated nearby entities.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(StaticEntityDataSetupSystem))]
    [RequireMatchingQueriesForUpdate]
    public partial struct StaticEntitySpawnSystem : ISystem
    {
        private const int MaxSitesGeneratedPerUpdate = 2048;
        private const int MaxSitesRemovedPerUpdate = 4096;
        private const int MaxSitesScannedPerUpdate = 10000;
        private const int MaxShaderSitesPerUpdate = 2048;
        private const int MaxEntityChangesPerUpdate = 256;
        private const float StreamingRefreshDistance = 5.0f;
        private const float EnableDistanceMultiplier = 0.95f;
        private int nextGroupStartIndex;
        private EntityQuery groupQuery;
        private ComponentLookup<MeshHabitatBlobRef> meshHabitatLookup;
        private NativeList<StaticEntitySpawnRequest> pendingSpawnRequests;
        private NativeList<Entity> pendingDespawnRequests;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<StaticEntitiesGroupComponent>();
            state.RequireForUpdate<SceneData>();
            nextGroupStartIndex = 0;
            groupQuery = state.EntityManager.CreateEntityQuery(typeof(StaticEntitiesGroupComponent));
            meshHabitatLookup = state.GetComponentLookup<MeshHabitatBlobRef>(true);
            pendingSpawnRequests = new NativeList<StaticEntitySpawnRequest>(MaxEntityChangesPerUpdate, Allocator.Persistent);
            pendingDespawnRequests = new NativeList<Entity>(MaxEntityChangesPerUpdate, Allocator.Persistent);
        }

        public void OnDestroy(ref SystemState state)
        {
            groupQuery.Dispose();
            pendingSpawnRequests.Dispose();
            pendingDespawnRequests.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.Dependency.Complete();

            EntityManager entityManager = state.EntityManager;
            SceneData sceneData = SystemAPI.GetSingleton<SceneData>();
            var ecbSingleton = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>();
            EntityCommandBuffer destroyCommandBuffer = ecbSingleton.CreateCommandBuffer(state.WorldUnmanaged);
            NativeArray<Entity> groupEntities = groupQuery.ToEntityArray(Allocator.Temp);
            int entityChanges = 0;
            int generatedSites = 0;
            int removedSites = 0;
            int reconciledSites = 0;
            int shaderSites = 0;
            int startIndex = 0;
            if (groupEntities.Length > 0)
            {
                startIndex = nextGroupStartIndex % groupEntities.Length;
            }

            for (int groupOffset = 0; groupOffset < groupEntities.Length; groupOffset++)
            {
                int groupIndex = (startIndex + groupOffset) % groupEntities.Length;
                Entity groupEntity = groupEntities[groupIndex];
                StaticEntitiesGroupComponent group = entityManager.GetComponentData<StaticEntitiesGroupComponent>(groupEntity);

                if (group.DestroyRequested)
                {
                    DestroyGroup(entityManager, destroyCommandBuffer, groupEntity);
                    continue;
                }

                if (!group.SpawnDataIsReady)
                {
                    continue;
                }

                pendingSpawnRequests.Clear();
                pendingDespawnRequests.Clear();

                Assert.IsTrue(entityManager.HasBuffer<StaticEntitySpawnSite>(groupEntity),
                    "StaticEntitySpawnSystem requires a StaticEntitySpawnSite buffer on every static group.");
                DynamicBuffer<StaticEntitySpawnSite> sites = entityManager.GetBuffer<StaticEntitySpawnSite>(groupEntity);
                Assert.AreEqual(group.GeneratedCount, sites.Length,
                    "Static entity generated count must match its spawn-site buffer length.");

                float cullingMaxDistance = MainScene.CalculateCullingMaxDistance(
                    group.MeshLargestDimension,
                    sceneData.CullingStartMeshSize,
                    sceneData.CullingStartDistance,
                    sceneData.CullingEndMeshSize,
                    sceneData.CullingEndDistance);

                bool populationReductionInProgress = group.PopulationReductionInProgress;
                if (group.GeneratedCount > group.RequestedCount)
                {
                    TrimSpawnSites(
                        entityManager,
                        ref sites,
                        ref group,
                        sceneData.CameraPosition,
                        ref entityChanges,
                        ref removedSites,
                        ref pendingDespawnRequests);
                }
                else if (group.GeneratedCount < group.RequestedCount)
                {
                    meshHabitatLookup.Update(ref state);
                    GenerateSpawnSites(
                        ref state,
                        entityManager,
                        meshHabitatLookup,
                        groupEntity,
                        ref sites,
                        ref group,
                        sceneData.CameraPosition,
                        cullingMaxDistance,
                        !populationReductionInProgress,
                        ref entityChanges,
                        ref generatedSites,
                        ref pendingSpawnRequests);
                }

                if (group.GeneratedCount == group.RequestedCount && populationReductionInProgress)
                {
                    FinishPopulationReduction(ref group, sceneData.CameraPosition);
                }
                else if (group.GeneratedCount == group.RequestedCount)
                {
                    RequestStreamingRefreshForCameraMovement(ref group, sceneData.CameraPosition);
                    ReconcileSpawnSites(
                        entityManager,
                        groupEntity,
                        ref sites,
                        ref group,
                        cullingMaxDistance,
                        ref entityChanges,
                        ref reconciledSites,
                        ref pendingSpawnRequests,
                        ref pendingDespawnRequests);
                }

                Assert.IsFalse(
                    populationReductionInProgress && pendingSpawnRequests.Length > 0,
                    "Lowering a static entity population must not enqueue entity spawns.");

                UpdateShaderVisibility(entityManager, ref sites, ref group, ref shaderSites);
                ApplyPendingEntityChanges(
                    entityManager,
                    groupEntity,
                    ref group,
                    ref pendingSpawnRequests,
                    ref pendingDespawnRequests);
                entityManager.SetComponentData(groupEntity, group);
            }

            if (groupEntities.Length > 0)
            {
                nextGroupStartIndex = (startIndex + 1) % groupEntities.Length;
            }

            groupEntities.Dispose();
        }

        private static void DestroyGroup(
            EntityManager entityManager,
            EntityCommandBuffer commandBuffer,
            Entity groupEntity)
        {
            if (entityManager.HasBuffer<StaticEntitySpawnSite>(groupEntity))
            {
                DynamicBuffer<StaticEntitySpawnSite> sites = entityManager.GetBuffer<StaticEntitySpawnSite>(groupEntity);
                for (int i = 0; i < sites.Length; i++)
                {
                    Entity spawnedEntity = sites[i].SpawnedEntity;
                    if (spawnedEntity != Entity.Null && entityManager.Exists(spawnedEntity))
                    {
                        commandBuffer.DestroyEntity(spawnedEntity);
                    }
                }
            }

            commandBuffer.DestroyEntity(groupEntity);
        }

        private static void TrimSpawnSites(
            EntityManager entityManager,
            ref DynamicBuffer<StaticEntitySpawnSite> sites,
            ref StaticEntitiesGroupComponent group,
            float3 cameraPosition,
            ref int entityChanges,
            ref int removedSites,
            ref NativeList<Entity> pendingDespawns)
        {
            while (group.GeneratedCount > group.RequestedCount && removedSites < MaxSitesRemovedPerUpdate)
            {
                int lastIndex = sites.Length - 1;
                StaticEntitySpawnSite site = sites[lastIndex];
                if (site.SpawnedEntity != Entity.Null)
                {
                    if (entityManager.Exists(site.SpawnedEntity))
                    {
                        if (entityChanges >= MaxEntityChangesPerUpdate)
                        {
                            break;
                        }

                        pendingDespawns.Add(site.SpawnedEntity);
                        entityChanges++;
                    }

                    group.Count--;
                }

                sites.RemoveAt(lastIndex);
                group.GeneratedCount--;
                removedSites++;
            }

            group.StreamingRefreshRequested = false;
            group.StreamingScanIndex = 0;
            group.StreamingScanCameraPosition = cameraPosition;
            group.ShaderUpdateScanIndex = math.min(group.ShaderUpdateScanIndex, sites.Length);
        }

        private static void GenerateSpawnSites(
            ref SystemState state,
            EntityManager entityManager,
            ComponentLookup<MeshHabitatBlobRef> meshHabitatLookup,
            Entity groupEntity,
            ref DynamicBuffer<StaticEntitySpawnSite> sites,
            ref StaticEntitiesGroupComponent group,
            float3 cameraPosition,
            float cullingMaxDistance,
            bool instantiateGeneratedSites,
            ref int entityChanges,
            ref int generatedSites,
            ref NativeList<StaticEntitySpawnRequest> pendingSpawns)
        {
            int remainingGenerationBudget = MaxSitesGeneratedPerUpdate - generatedSites;
            if (remainingGenerationBudget <= 0)
            {
                return;
            }

            int amountToGenerate = math.min(group.RequestedCount - group.GeneratedCount, remainingGenerationBudget);
            NativeList<Entity> meshHabitats = new NativeList<Entity>(Allocator.TempJob);
            if (group.UseMeshHabitats && entityManager.HasBuffer<MeshHabitatEntityRef>(groupEntity))
            {
                DynamicBuffer<MeshHabitatEntityRef> meshBuffer = entityManager.GetBuffer<MeshHabitatEntityRef>(groupEntity);
                for (int i = 0; i < meshBuffer.Length; i++)
                {
                    Entity meshEntity = meshBuffer[i].MeshEntity;
                    if (entityManager.Exists(meshEntity) && meshHabitatLookup.HasComponent(meshEntity))
                    {
                        MeshHabitatBlobRef habitat = meshHabitatLookup[meshEntity];
                        if (habitat.BlobRef.IsCreated && habitat.BlobRef.Value.SurfaceArea > 0.0f &&
                            habitat.BlobRef.Value.Triangles.Length >= 3 &&
                            habitat.BlobRef.Value.Colors.Length == habitat.BlobRef.Value.Vertices.Length)
                        {
                            meshHabitats.Add(meshEntity);
                        }
                    }
                }
            }

            bool canUseTerrain = group.UseSplatmap && group.SplatmapDataBlobRef.IsCreated &&
                                 group.HeightmapDataBlobRef.IsCreated && group.FallbackSplatmapIndex >= 0;
            bool canUseMesh = meshHabitats.Length > 0;
            if (!canUseTerrain && !canUseMesh)
            {
                Debug.LogError("[StaticEntitySpawnSystem] Static group " + group.StaticEntitiesGroupId +
                               " has no valid terrain or mesh habitat spawn data. Streaming cannot continue.");
                group.SpawnDataIsReady = false;
                meshHabitats.Dispose();
                return;
            }

            NativeArray<StaticEntityPlacement> generatedPlacements =
                new NativeArray<StaticEntityPlacement>(amountToGenerate, Allocator.TempJob);
            var generateJob = new GenerateStaticSpawnSitesJob
            {
                StartSiteIndex = group.GeneratedCount,
                GroupId = group.StaticEntitiesGroupId,
                CanUseTerrain = canUseTerrain,
                CanUseMesh = canUseMesh,
                MeshHabitatRatio = math.saturate(group.MeshHabitatRatio),
                SplatmapData = group.SplatmapDataBlobRef,
                SplatmapWidth = group.SplatmapWidth,
                SplatmapHeight = group.SplatmapHeight,
                FallbackSplatmapIndex = group.FallbackSplatmapIndex,
                HeightmapData = group.HeightmapDataBlobRef,
                HeightmapWidth = group.HeightmapWidth,
                HeightmapHeight = group.HeightmapHeight,
                TerrainSize = group.TerrainSize,
                TerrainHeight = group.TerrainHeight,
                TerrainOffset = new float3(group.TerrainOffsetX, group.TerrainOffsetY, group.TerrainOffsetZ),
                GroupNoiseOffset = group.GroupNoiseOffset,
                NoiseScale = group.NoiseScale,
                MinScale = group.MinScale,
                MaxScale = group.MaxScale,
                MeshHabitats = meshHabitats.AsArray(),
                MeshHabitatLookup = meshHabitatLookup,
                GeneratedPlacements = generatedPlacements
            };

            JobHandle generateHandle = generateJob.Schedule(amountToGenerate, 64, state.Dependency);
            generateHandle.Complete();
            state.Dependency = default;

            float enableDistance = cullingMaxDistance * EnableDistanceMultiplier;
            float enableDistanceSq = enableDistance * enableDistance;
            for (int i = 0; i < generatedPlacements.Length; i++)
            {
                int siteIndex = group.GeneratedCount;
                StaticEntityPlacement placement = generatedPlacements[i];
                var site = new StaticEntitySpawnSite
                {
                    Position = placement.Position,
                    Rotation = placement.Rotation,
                    Scale = placement.Scale,
                    SpawnedEntity = Entity.Null
                };

                if (instantiateGeneratedSites && entityChanges < MaxEntityChangesPerUpdate &&
                    math.distancesq(placement.Position, cameraPosition) <= enableDistanceSq)
                {
                    pendingSpawns.Add(new StaticEntitySpawnRequest
                    {
                        SiteIndex = siteIndex,
                        LocalToWorld = site.CreateLocalToWorld()
                    });
                    group.Count++;
                    entityChanges++;
                }

                sites.Add(site);
                group.GeneratedCount++;
                generatedSites++;
            }

            generatedPlacements.Dispose();
            meshHabitats.Dispose();

            if (group.GeneratedCount == group.RequestedCount && instantiateGeneratedSites)
            {
                group.StreamingRefreshRequested = true;
                group.StreamingScanIndex = 0;
                group.StreamingScanCameraPosition = cameraPosition;
            }
        }

        private static void FinishPopulationReduction(
            ref StaticEntitiesGroupComponent group,
            float3 cameraPosition)
        {
            group.PopulationReductionInProgress = false;
            group.StreamingRefreshRequested = false;
            group.StreamingScanIndex = 0;
            group.StreamingScanCameraPosition = cameraPosition;
        }

        private static void RequestStreamingRefreshForCameraMovement(
            ref StaticEntitiesGroupComponent group,
            float3 cameraPosition)
        {
            if (group.StreamingRefreshRequested)
            {
                return;
            }

            float refreshDistanceSq = StreamingRefreshDistance * StreamingRefreshDistance;
            if (math.distancesq(group.StreamingScanCameraPosition, cameraPosition) >= refreshDistanceSq)
            {
                group.StreamingRefreshRequested = true;
                group.StreamingScanIndex = 0;
                group.StreamingScanCameraPosition = cameraPosition;
            }
        }

        private static void ReconcileSpawnSites(
            EntityManager entityManager,
            Entity groupEntity,
            ref DynamicBuffer<StaticEntitySpawnSite> sites,
            ref StaticEntitiesGroupComponent group,
            float cullingMaxDistance,
            ref int entityChanges,
            ref int reconciledSites,
            ref NativeList<StaticEntitySpawnRequest> pendingSpawns,
            ref NativeList<Entity> pendingDespawns)
        {
            if (!group.StreamingRefreshRequested)
            {
                return;
            }

            float disableDistanceSq = cullingMaxDistance * cullingMaxDistance;
            float enableDistance = cullingMaxDistance * EnableDistanceMultiplier;
            float enableDistanceSq = enableDistance * enableDistance;
            int remainingScanBudget = MaxSitesScannedPerUpdate - reconciledSites;
            if (remainingScanBudget <= 0)
            {
                return;
            }

            int scanEnd = math.min(sites.Length, group.StreamingScanIndex + remainingScanBudget);

            while (group.StreamingScanIndex < scanEnd)
            {
                int siteIndex = group.StreamingScanIndex;
                StaticEntitySpawnSite site = sites[siteIndex];
                bool entityExists = site.SpawnedEntity != Entity.Null && entityManager.Exists(site.SpawnedEntity);
                if (site.SpawnedEntity != Entity.Null && !entityExists)
                {
                    site.SpawnedEntity = Entity.Null;
                    group.Count--;
                    sites[siteIndex] = site;
                }

                float distanceSq = math.distancesq(site.Position, group.StreamingScanCameraPosition);
                if (entityExists && distanceSq > disableDistanceSq)
                {
                    if (entityChanges >= MaxEntityChangesPerUpdate)
                    {
                        break;
                    }

                    pendingDespawns.Add(site.SpawnedEntity);
                    site.SpawnedEntity = Entity.Null;
                    sites[siteIndex] = site;
                    group.Count--;
                    entityChanges++;
                }
                else if (!entityExists && distanceSq <= enableDistanceSq)
                {
                    if (entityChanges >= MaxEntityChangesPerUpdate)
                    {
                        break;
                    }

                    pendingSpawns.Add(new StaticEntitySpawnRequest
                    {
                        SiteIndex = siteIndex,
                        LocalToWorld = site.CreateLocalToWorld()
                    });
                    group.Count++;
                    entityChanges++;
                }

                group.StreamingScanIndex++;
                reconciledSites++;
            }

            if (group.StreamingScanIndex >= sites.Length)
            {
                group.StreamingRefreshRequested = false;
                group.StreamingScanIndex = 0;
            }
        }

        private static void ApplyPendingEntityChanges(
            EntityManager entityManager,
            Entity groupEntity,
            ref StaticEntitiesGroupComponent group,
            ref NativeList<StaticEntitySpawnRequest> pendingSpawns,
            ref NativeList<Entity> pendingDespawns)
        {
            if (pendingDespawns.Length > 0)
            {
                entityManager.DestroyEntity(pendingDespawns.AsArray());
            }

            if (pendingSpawns.Length == 0)
            {
                return;
            }

            Assert.IsTrue(group.StaticEntityPrototype != Entity.Null && entityManager.Exists(group.StaticEntityPrototype),
                "StaticEntitySpawnSystem requires a valid static entity prototype.");
            Assert.IsTrue(entityManager.HasComponent<RenderMeshArray>(group.StaticEntityPrototype),
                "StaticEntitySpawnSystem requires a RenderMeshArray on the static entity prototype.");

            NativeArray<Entity> spawnedEntities = new NativeArray<Entity>(pendingSpawns.Length, Allocator.Temp);
            entityManager.Instantiate(group.StaticEntityPrototype, spawnedEntities);
            DynamicBuffer<StaticEntitySpawnSite> refreshedSites =
                entityManager.GetBuffer<StaticEntitySpawnSite>(groupEntity);

            for (int i = 0; i < spawnedEntities.Length; i++)
            {
                StaticEntitySpawnRequest request = pendingSpawns[i];
                Assert.IsTrue(request.SiteIndex >= 0 && request.SiteIndex < refreshedSites.Length,
                    "Static entity spawn request must reference a valid spawn site.");
                StaticEntitySpawnSite site = refreshedSites[request.SiteIndex];
                Assert.AreEqual(Entity.Null, site.SpawnedEntity,
                    "Static entity spawn request requires an unoccupied spawn site.");

                Entity spawnedEntity = spawnedEntities[i];
                ConfigureSpawnedSite(
                    entityManager,
                    spawnedEntity,
                    groupEntity,
                    request.SiteIndex,
                    request.LocalToWorld,
                    in group);
                site.SpawnedEntity = spawnedEntity;
                refreshedSites[request.SiteIndex] = site;
            }

            spawnedEntities.Dispose();
        }

        private static void ConfigureSpawnedSite(
            EntityManager entityManager,
            Entity spawnedEntity,
            Entity groupEntity,
            int siteIndex,
            float4x4 localToWorld,
            in StaticEntitiesGroupComponent group)
        {
            entityManager.SetComponentData(spawnedEntity, new LocalToWorld { Value = localToWorld });
            entityManager.SetComponentData(spawnedEntity, new StaticEntitySpawnSiteIndex { Value = siteIndex });
            if (entityManager.HasComponent<StaticEntityHoverMember>(spawnedEntity))
            {
                entityManager.SetComponentData(spawnedEntity, new StaticEntityHoverMember { GroupEntity = groupEntity });
            }

            SetEntityShaderVisibility(entityManager, spawnedEntity, siteIndex, in group);
        }

        private static void UpdateShaderVisibility(
            EntityManager entityManager,
            ref DynamicBuffer<StaticEntitySpawnSite> sites,
            ref StaticEntitiesGroupComponent group,
            ref int shaderSites)
        {
            if (!group.ShaderUpdateRequested)
            {
                return;
            }

            int remainingShaderBudget = MaxShaderSitesPerUpdate - shaderSites;
            if (remainingShaderBudget <= 0)
            {
                return;
            }

            int scanEnd = math.min(sites.Length, group.ShaderUpdateScanIndex + remainingShaderBudget);
            while (group.ShaderUpdateScanIndex < scanEnd)
            {
                int siteIndex = group.ShaderUpdateScanIndex;
                Entity spawnedEntity = sites[siteIndex].SpawnedEntity;
                if (spawnedEntity != Entity.Null && entityManager.Exists(spawnedEntity))
                {
                    SetEntityShaderVisibility(entityManager, spawnedEntity, siteIndex, in group);
                }
                group.ShaderUpdateScanIndex++;
                shaderSites++;
            }

            if (group.ShaderUpdateScanIndex >= sites.Length && group.GeneratedCount == group.RequestedCount)
            {
                group.ShaderUpdateRequested = false;
                group.ShaderUpdateScanIndex = 0;
            }
        }

        private static void SetEntityShaderVisibility(
            EntityManager entityManager,
            Entity entity,
            int siteIndex,
            in StaticEntitiesGroupComponent group)
        {
            float4 displayStart = float4.zero;
            float4 displayEnd = float4.zero;
            for (int viewIndex = 0; viewIndex < group.ViewsCount; viewIndex++)
            {
                float visibility = GetViewValue(group.ViewVisibilityPercentages, viewIndex);
                if (!StaticEntityViewVisibilityUtility.IsVisible(siteIndex, group.StaticEntitiesGroupId, visibility))
                {
                    continue;
                }

                float start = (float)viewIndex / group.ViewsCount;
                float end = (float)(viewIndex + 1) / group.ViewsCount;
                SetViewValue(ref displayStart, viewIndex, start);
                SetViewValue(ref displayEnd, viewIndex, end);
            }

            entityManager.SetComponentData(entity, new ScreenDisplayStartOverride { Value = displayStart });
            entityManager.SetComponentData(entity, new ScreenDisplayEndOverride { Value = displayEnd });
        }

        private static float GetViewValue(float4 values, int viewIndex)
        {
            switch (viewIndex)
            {
                case 0: return values.x;
                case 1: return values.y;
                case 2: return values.z;
                case 3: return values.w;
                default:
                    Assert.IsTrue(false, "Static entity view index must be between 0 and 3.");
                    return 0.0f;
            }
        }

        private static void SetViewValue(ref float4 values, int viewIndex, float value)
        {
            switch (viewIndex)
            {
                case 0: values.x = value; break;
                case 1: values.y = value; break;
                case 2: values.z = value; break;
                case 3: values.w = value; break;
                default: Assert.IsTrue(false, "Static entity view index must be between 0 and 3."); break;
            }
        }
    }

    /// <summary>
    /// Generates lightweight terrain or mesh placement transforms without creating ECS entities.
    /// </summary>
    [BurstCompile]
    internal struct GenerateStaticSpawnSitesJob : IJobParallelFor
    {
        private const int MaxPlacementAttempts = 100;

        [ReadOnly] public int StartSiteIndex;
        [ReadOnly] public int GroupId;
        [ReadOnly] public bool CanUseTerrain;
        [ReadOnly] public bool CanUseMesh;
        [ReadOnly] public float MeshHabitatRatio;
        [ReadOnly] public BlobAssetReference<ByteBlob> SplatmapData;
        [ReadOnly] public int SplatmapWidth;
        [ReadOnly] public int SplatmapHeight;
        [ReadOnly] public int FallbackSplatmapIndex;
        [ReadOnly] public BlobAssetReference<FloatBlob> HeightmapData;
        [ReadOnly] public int HeightmapWidth;
        [ReadOnly] public int HeightmapHeight;
        [ReadOnly] public float TerrainSize;
        [ReadOnly] public float TerrainHeight;
        [ReadOnly] public float3 TerrainOffset;
        [ReadOnly] public float3 GroupNoiseOffset;
        [ReadOnly] public float NoiseScale;
        [ReadOnly] public float MinScale;
        [ReadOnly] public float MaxScale;
        [ReadOnly] public NativeArray<Entity> MeshHabitats;
        [ReadOnly] public ComponentLookup<MeshHabitatBlobRef> MeshHabitatLookup;

        [WriteOnly] public NativeArray<StaticEntityPlacement> GeneratedPlacements;

        public void Execute(int index)
        {
            int siteIndex = StartSiteIndex + index;
            uint seed = math.hash(new uint2((uint)GroupId, (uint)siteIndex));
            Unity.Mathematics.Random random = Unity.Mathematics.Random.CreateFromIndex(seed);

            bool useMesh = CanUseMesh && (!CanUseTerrain || random.NextFloat() < MeshHabitatRatio);
            if (useMesh)
            {
                GeneratedPlacements[index] = GenerateMeshPlacement(ref random);
            }
            else
            {
                GeneratedPlacements[index] = GenerateTerrainPlacement(ref random);
            }
        }

        private StaticEntityPlacement GenerateTerrainPlacement(ref Unity.Mathematics.Random random)
        {
            int spawnIndex = FallbackSplatmapIndex;
            ref BlobArray<byte> splatmapValues = ref SplatmapData.Value.Values;
            for (int attempt = 0; attempt < MaxPlacementAttempts; attempt++)
            {
                int candidateIndex = random.NextInt(0, SplatmapWidth * SplatmapHeight);
                float habitatWeight = splatmapValues[candidateIndex] / 255.0f;
                if (habitatWeight < 0.01f)
                {
                    continue;
                }

                int candidateX = candidateIndex % SplatmapWidth;
                int candidateZ = candidateIndex / SplatmapWidth;
                float normalizedX = (candidateX + 0.5f) / SplatmapWidth;
                float normalizedZ = (candidateZ + 0.5f) / SplatmapHeight;
                float worldX = TerrainOffset.x + normalizedX * TerrainSize;
                float worldZ = TerrainOffset.z + normalizedZ * TerrainSize;
                float noiseValue = noise.snoise(new float3(
                    worldX / NoiseScale + GroupNoiseOffset.x,
                    worldZ / NoiseScale + GroupNoiseOffset.z,
                    GroupNoiseOffset.y));
                float noiseWeight = math.saturate(noiseValue + 0.5f);
                if (random.NextFloat() <= habitatWeight * noiseWeight)
                {
                    spawnIndex = candidateIndex;
                    break;
                }
            }

            int splatX = spawnIndex % SplatmapWidth;
            int splatZ = spawnIndex / SplatmapWidth;
            float terrainNormalizedX = math.saturate((splatX + random.NextFloat()) / SplatmapWidth);
            float terrainNormalizedZ = math.saturate((splatZ + random.NextFloat()) / SplatmapHeight);
            float height = SampleHeight(terrainNormalizedX, terrainNormalizedZ);
            float3 position = new float3(
                TerrainOffset.x + terrainNormalizedX * TerrainSize,
                TerrainOffset.y + height * TerrainHeight,
                TerrainOffset.z + terrainNormalizedZ * TerrainSize);
            quaternion rotation = quaternion.Euler(
                math.radians(random.NextFloat(-5.0f, 5.0f)),
                random.NextFloat(0.0f, math.PI * 2.0f),
                math.radians(random.NextFloat(-5.0f, 5.0f)));
            return new StaticEntityPlacement
            {
                Position = position,
                Rotation = rotation,
                Scale = GenerateScale(ref random)
            };
        }

        private StaticEntityPlacement GenerateMeshPlacement(ref Unity.Mathematics.Random random)
        {
            float totalArea = 0.0f;
            for (int i = 0; i < MeshHabitats.Length; i++)
            {
                totalArea += MeshHabitatLookup[MeshHabitats[i]].BlobRef.Value.SurfaceArea;
            }

            float areaSelection = random.NextFloat(0.0f, totalArea);
            Entity selectedHabitat = MeshHabitats[MeshHabitats.Length - 1];
            for (int i = 0; i < MeshHabitats.Length; i++)
            {
                Entity candidate = MeshHabitats[i];
                areaSelection -= MeshHabitatLookup[candidate].BlobRef.Value.SurfaceArea;
                if (areaSelection <= 0.0f)
                {
                    selectedHabitat = candidate;
                    break;
                }
            }

            MeshHabitatBlobRef habitat = MeshHabitatLookup[selectedHabitat];
            ref MeshHabitatBlobData mesh = ref habitat.BlobRef.Value;
            int triangleCount = mesh.Triangles.Length / 3;
            int selectedTriangle = random.NextInt(0, triangleCount);
            float3 localPosition = float3.zero;
            float3 localNormal = math.up();

            for (int attempt = 0; attempt < MaxPlacementAttempts; attempt++)
            {
                int triangleIndex = random.NextInt(0, triangleCount);
                SampleTriangle(ref mesh, triangleIndex, ref random, out float3 candidatePosition, out float3 candidateNormal, out float density);
                selectedTriangle = triangleIndex;
                localPosition = candidatePosition;
                localNormal = candidateNormal;
                if (random.NextFloat() <= density)
                {
                    break;
                }
            }

            if (math.lengthsq(localNormal) < 0.0001f)
            {
                SampleTriangle(ref mesh, selectedTriangle, ref random, out localPosition, out localNormal, out float _);
            }

            float3 worldPosition = math.transform(habitat.LocalToWorld, localPosition);
            float3 worldNormal = math.normalizesafe(math.rotate(new quaternion(habitat.LocalToWorld), localNormal), math.up());
            float3 tangent = math.normalizesafe(math.cross(worldNormal, new float3(0.37f, 0.81f, 0.21f)), math.forward());
            quaternion alignToSurface = quaternion.LookRotationSafe(tangent, worldNormal);
            quaternion randomYaw = quaternion.AxisAngle(worldNormal, random.NextFloat(0.0f, math.PI * 2.0f));
            return new StaticEntityPlacement
            {
                Position = worldPosition,
                Rotation = math.mul(randomYaw, alignToSurface),
                Scale = GenerateScale(ref random)
            };
        }

        private void SampleTriangle(
            ref MeshHabitatBlobData mesh,
            int triangleIndex,
            ref Unity.Mathematics.Random random,
            out float3 position,
            out float3 normal,
            out float density)
        {
            int firstIndex = mesh.Triangles[triangleIndex * 3];
            int secondIndex = mesh.Triangles[triangleIndex * 3 + 1];
            int thirdIndex = mesh.Triangles[triangleIndex * 3 + 2];
            float3 first = mesh.Vertices[firstIndex];
            float3 second = mesh.Vertices[secondIndex];
            float3 third = mesh.Vertices[thirdIndex];

            float u = random.NextFloat();
            float v = random.NextFloat();
            if (u + v > 1.0f)
            {
                u = 1.0f - u;
                v = 1.0f - v;
            }
            float w = 1.0f - u - v;
            position = first * u + second * v + third * w;
            normal = math.normalizesafe(math.cross(second - first, third - first), math.up());
            float colorWeight = mesh.Colors[firstIndex] * u + mesh.Colors[secondIndex] * v + mesh.Colors[thirdIndex] * w;
            float noiseWeight = noise.snoise((position + GroupNoiseOffset) / NoiseScale) * 0.5f + 0.5f;
            density = math.saturate(colorWeight * noiseWeight);
        }

        private float3 GenerateScale(ref Unity.Mathematics.Random random)
        {
            float minimumScale = MinScale;
            if (minimumScale <= 0.0f)
            {
                minimumScale = 0.8f;
            }

            float maximumScale = MaxScale;
            if (maximumScale < minimumScale)
            {
                maximumScale = minimumScale;
            }
            float baseScale = random.NextFloat(minimumScale, maximumScale);
            return new float3(
                baseScale * random.NextFloat(0.9f, 1.1f),
                baseScale,
                baseScale * random.NextFloat(0.9f, 1.1f));
        }

        private float SampleHeight(float normalizedX, float normalizedZ)
        {
            ref BlobArray<float> values = ref HeightmapData.Value.Values;
            float heightmapX = normalizedX * (HeightmapWidth - 1);
            float heightmapZ = normalizedZ * (HeightmapHeight - 1);
            int x0 = (int)heightmapX;
            int z0 = (int)heightmapZ;
            int x1 = math.min(x0 + 1, HeightmapWidth - 1);
            int z1 = math.min(z0 + 1, HeightmapHeight - 1);
            float tx = heightmapX - x0;
            float tz = heightmapZ - z0;
            float h0 = math.lerp(values[z0 * HeightmapWidth + x0], values[z0 * HeightmapWidth + x1], tx);
            float h1 = math.lerp(values[z1 * HeightmapWidth + x0], values[z1 * HeightmapWidth + x1], tx);
            return math.lerp(h0, h1, tz);
        }
    }

    internal struct StaticEntityPlacement
    {
        public float3 Position;
        public quaternion Rotation;
        public float3 Scale;
    }

    internal struct StaticEntitySpawnRequest
    {
        public int SiteIndex;
        public float4x4 LocalToWorld;
    }
}
