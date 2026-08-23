using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Rendering;
using Unity.Assertions;

namespace OceanViz3
{
    internal static class BoidSchoolRuntimeUtility
    {
        public static int ComputeSchoolIndex(int dynamicEntityId, int boidSchoolId)
        {
            return (int)math.hash(new uint2((uint)dynamicEntityId, (uint)boidSchoolId));
        }

        public static BoidShared BuildLegacyBoidShared(in BoidSchoolRuntimeData runtimeData)
        {
            return new BoidShared
            {
                DynamicEntityId = runtimeData.DynamicEntityId,
                BoidSchoolId = runtimeData.BoidSchoolId,
                CellRadius = runtimeData.CellRadius,
                BoundsMax = runtimeData.BoundsMax,
                BoundsMin = runtimeData.BoundsMin,
                SeparationWeight = runtimeData.SeparationWeight,
                AlignmentWeight = runtimeData.AlignmentWeight,
                TargetWeight = runtimeData.TargetWeight,
                ObstacleAversionDistance = runtimeData.ObstacleAversionDistance,
                DefaultMoveSpeed = runtimeData.DefaultMoveSpeed,
                DefaultAnimationSpeed = runtimeData.DefaultAnimationSpeed,
                MaxVerticalAngle = runtimeData.MaxVerticalAngle,
                MaxTurnRate = runtimeData.MaxTurnRate,
                SeabedBound = runtimeData.SeabedBound,
                Predator = runtimeData.Predator,
                Prey = runtimeData.Prey,
                StateTransitionSpeed = runtimeData.StateTransitionSpeed,
                StateChangeTimerMin = runtimeData.StateChangeTimerMin,
                StateChangeTimerMax = runtimeData.StateChangeTimerMax,
                BoneAnimated = runtimeData.BoneAnimated,
                NumberOfLODs = runtimeData.NumberOfLODs,
                SpeedModifierMin = runtimeData.SpeedModifierMin,
                SpeedModifierMax = runtimeData.SpeedModifierMax,
                SpeedJitterAmplitude = runtimeData.SpeedJitterAmplitude,
                SpeedJitterFrequency = runtimeData.SpeedJitterFrequency,
                MeshLargestDimension = runtimeData.MeshLargestDimension
            };
        }

        public static void BuildScreenDisplayRange(in BoidSchoolRuntimeData runtimeData, int boidIndex, int boidTotal, out float4 boidScreenDisplayStart, out float4 boidScreenDisplayEnd)
        {
            boidScreenDisplayStart = new float4();
            boidScreenDisplayEnd = new float4();

            for (int i = 0; i < runtimeData.ViewsCount; i++)
            {
                bool visibleInView = false;
                if (boidTotal > 0)
                {
                    int visibleCount = (int)(boidTotal * (runtimeData.ViewVisibilityPercentages[i] / 100.0f));
                    if (boidIndex < visibleCount)
                    {
                        visibleInView = true;
                    }
                }

                if (visibleInView)
                {
                    float startFloat = (1.0f / runtimeData.ViewsCount) * i;
                    float endFloat = (1.0f / runtimeData.ViewsCount) * (i + 1);
                    if (i == 0)
                    {
                        boidScreenDisplayStart.x = startFloat;
                        boidScreenDisplayEnd.x = endFloat;
                    }
                    else if (i == 1)
                    {
                        boidScreenDisplayStart.y = startFloat;
                        boidScreenDisplayEnd.y = endFloat;
                    }
                    else if (i == 2)
                    {
                        boidScreenDisplayStart.z = startFloat;
                        boidScreenDisplayEnd.z = endFloat;
                    }
                    else if (i == 3)
                    {
                        boidScreenDisplayStart.w = startFloat;
                        boidScreenDisplayEnd.w = endFloat;
                    }
                }
            }
        }
    }

    [BurstCompile]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(DistanceCullingSystem))]
    [UpdateBefore(typeof(BoidSchoolSpawnPrototypeSetupSystem))]
    public partial struct BoidSchoolRuntimeSyncSystem : ISystem
    {
        private EntityQuery sceneDataQuery;

        public void OnCreate(ref SystemState state)
        {
            sceneDataQuery = state.EntityManager.CreateEntityQuery(typeof(SceneData));
            state.RequireForUpdate(sceneDataQuery);
        }

        public void OnUpdate(ref SystemState state)
        {
            SceneData sceneData = sceneDataQuery.GetSingleton<SceneData>();

            foreach (var (school, runtime) in SystemAPI.Query<RefRO<BoidSchoolComponent>, RefRW<BoidSchoolRuntimeData>>())
            {
                int schoolIndex = BoidSchoolRuntimeUtility.ComputeSchoolIndex(school.ValueRO.DynamicEntityId, school.ValueRO.BoidSchoolId);
                float cullingMaxDistance = MainScene.CalculateCullingMaxDistance(
                    school.ValueRO.MeshLargestDimension,
                    sceneData.CullingStartMeshSize,
                    sceneData.CullingStartDistance,
                    sceneData.CullingEndMeshSize,
                    sceneData.CullingEndDistance);

                runtime.ValueRW = new BoidSchoolRuntimeData
                {
                    DynamicEntityId = school.ValueRO.DynamicEntityId,
                    BoidSchoolId = school.ValueRO.BoidSchoolId,
                    SchoolIndex = schoolIndex,
                    Target = school.ValueRO.Target,
                    BoundsCenter = school.ValueRO.Boundary.BoundsCenter,
                    BoundsMin = school.ValueRO.Boundary.BoundsMin,
                    BoundsMax = school.ValueRO.Boundary.BoundsMax,
                    Boundary = school.ValueRO.Boundary,
                    SeparationWeight = school.ValueRO.SeparationWeight,
                    AlignmentWeight = school.ValueRO.AlignmentWeight,
                    TargetWeight = school.ValueRO.TargetWeight,
                    ObstacleAversionDistance = school.ValueRO.ObstacleAversionDistance,
                    DefaultMoveSpeed = school.ValueRO.Speed,
                    DefaultAnimationSpeed = school.ValueRO.AnimationSpeed,
                    MaxVerticalAngle = school.ValueRO.MaxVerticalAngle,
                    MaxTurnRate = school.ValueRO.MaxTurnRate,
                    SeabedBound = school.ValueRO.SeabedBound,
                    Predator = school.ValueRO.Predator,
                    Prey = school.ValueRO.Prey,
                    WaterCurrentInfluence = school.ValueRO.WaterCurrentInfluence,
                    CellRadius = school.ValueRO.CellRadius,
                    StateTransitionSpeed = school.ValueRO.StateTransitionSpeed,
                    StateChangeTimerMin = school.ValueRO.StateChangeTimerMin,
                    StateChangeTimerMax = school.ValueRO.StateChangeTimerMax,
                    BoneAnimated = school.ValueRO.BoneAnimated,
                    NumberOfLODs = school.ValueRO.NumberOfLODs,
                    SpeedModifierMin = school.ValueRO.SpeedModifierMin,
                    SpeedModifierMax = school.ValueRO.SpeedModifierMax,
                    SpeedJitterAmplitude = school.ValueRO.SpeedJitterAmplitude,
                    SpeedJitterFrequency = school.ValueRO.SpeedJitterFrequency,
                    SpawnClustering = school.ValueRO.SpawnClustering,
                    ScaleMin = school.ValueRO.ScaleMin,
                    ScaleMax = school.ValueRO.ScaleMax,
                    MeshLargestDimension = school.ValueRO.MeshLargestDimension,
                    CullingMaxDistance = cullingMaxDistance,
                    ViewsCount = school.ValueRO.ViewsCount,
                    ViewVisibilityPercentages = school.ValueRO.ViewVisibilityPercentages,
                    AnimationSpeed = school.ValueRO.AnimationSpeed,
                    SineWavelength = school.ValueRO.SineWavelength,
                    SineDeformationAmplitude = school.ValueRO.SineDeformationAmplitude,
                    Secondary1AnimationAmplitude = school.ValueRO.Secondary1AnimationAmplitude,
                    InvertSecondary1Animation = school.ValueRO.InvertSecondary1Animation,
                    Secondary2AnimationAmplitude = school.ValueRO.Secondary2AnimationAmplitude,
                    InvertSecondary2Animation = school.ValueRO.InvertSecondary2Animation,
                    SideToSideAmplitude = school.ValueRO.SideToSideAmplitude,
                    YawAmplitude = school.ValueRO.YawAmplitude,
                    RollingSpineAmplitude = school.ValueRO.RollingSpineAmplitude,
                    MeshZMin = school.ValueRO.MeshZMin,
                    MeshZMax = school.ValueRO.MeshZMax,
                    PositiveYClip = school.ValueRO.PositiveYClip,
                    NegativeYClip = school.ValueRO.NegativeYClip
                };
            }
        }
    }

    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BoidSchoolRuntimeSyncSystem))]
    [UpdateBefore(typeof(BoidSchoolSpawnSystem))]
    public partial struct BoidSchoolSpawnPrototypeSetupSystem : ISystem
    {
        private EntityQuery boidSchoolQuery;

        public void OnCreate(ref SystemState state)
        {
            boidSchoolQuery = SystemAPI.QueryBuilder()
                .WithAll<BoidSchoolComponent, BoidSchoolRuntimeData, BoidSchoolSpawnPrototype>()
                .Build();
        }

        public void OnUpdate(ref SystemState state)
        {
            NativeArray<Entity> schoolEntities = boidSchoolQuery.ToEntityArray(Allocator.Temp);
            NativeArray<BoidSchoolComponent> schoolComponents = boidSchoolQuery.ToComponentDataArray<BoidSchoolComponent>(Allocator.Temp);
            NativeArray<BoidSchoolRuntimeData> runtimeDataArray = boidSchoolQuery.ToComponentDataArray<BoidSchoolRuntimeData>(Allocator.Temp);
            NativeArray<BoidSchoolSpawnPrototype> spawnPrototypeArray = boidSchoolQuery.ToComponentDataArray<BoidSchoolSpawnPrototype>(Allocator.Temp);

            try
            {
                for (int i = 0; i < schoolEntities.Length; i++)
                {
                    Entity schoolEntity = schoolEntities[i];
                    BoidSchoolComponent schoolComponent = schoolComponents[i];
                    BoidSchoolRuntimeData runtimeData = runtimeDataArray[i];
                    BoidSchoolSpawnPrototype spawnPrototypeData = spawnPrototypeArray[i];

                    if (schoolComponent.DestroyRequested)
                    {
                        continue;
                    }

                    if (spawnPrototypeData.Value != Entity.Null && state.EntityManager.Exists(spawnPrototypeData.Value))
                    {
                        continue;
                    }

                    Assert.IsTrue(state.EntityManager.Exists(schoolComponent.BoidPrototype), "BoidSchoolSpawnPrototypeSetupSystem requires a valid base boid prototype.");
                    Entity spawnPrototype = state.EntityManager.Instantiate(schoolComponent.BoidPrototype);
                    if (state.EntityManager.HasComponent<Prefab>(spawnPrototype) == false)
                    {
                        state.EntityManager.AddComponent<Prefab>(spawnPrototype);
                    }

                    state.EntityManager.SetName(spawnPrototype, "BoidSpawnPrototype_" + runtimeData.DynamicEntityId + "_" + runtimeData.BoidSchoolId);
                    state.EntityManager.SetSharedComponentManaged(spawnPrototype, BoidSchoolRuntimeUtility.BuildLegacyBoidShared(runtimeData));
                    state.EntityManager.SetComponentData(spawnPrototype, new CullingComponent { MaxDistance = runtimeData.CullingMaxDistance });
                    state.EntityManager.SetComponentData(spawnPrototype, new LODState { CurrentLOD = 0 });
                    state.EntityManager.SetComponentData(spawnPrototype, new BoidSchoolMember
                    {
                        SchoolEntity = schoolEntity,
                        SchoolIndex = runtimeData.SchoolIndex,
                        DynamicEntityId = runtimeData.DynamicEntityId,
                        BoidSchoolId = runtimeData.BoidSchoolId
                    });
                    state.EntityManager.SetComponentData(spawnPrototype, new CurrentVectorOverride { Value = float3.zero });
                    state.EntityManager.SetComponentData(spawnPrototype, new AccumulatedTimeOverride { Value = 0.0f });
                    state.EntityManager.SetComponentData(spawnPrototype, new MeshZMinOverride { Value = runtimeData.MeshZMin });
                    state.EntityManager.SetComponentData(spawnPrototype, new MeshZMaxOverride { Value = runtimeData.MeshZMax });
                    state.EntityManager.SetComponentData(spawnPrototype, new AnimationSpeedOverride { Value = runtimeData.AnimationSpeed });
                    state.EntityManager.SetComponentData(spawnPrototype, new SineWavelengthOverride { Value = runtimeData.SineWavelength });
                    state.EntityManager.SetComponentData(spawnPrototype, new SineDeformationAmplitudeOverride { Value = runtimeData.SineDeformationAmplitude });
                    state.EntityManager.SetComponentData(spawnPrototype, new Secondary1AnimationAmplitudeOverride { Value = runtimeData.Secondary1AnimationAmplitude });
                    state.EntityManager.SetComponentData(spawnPrototype, new InvertSecondary1AnimationOverride { Value = runtimeData.InvertSecondary1Animation });
                    state.EntityManager.SetComponentData(spawnPrototype, new Secondary2AnimationAmplitudeOverride { Value = runtimeData.Secondary2AnimationAmplitude });
                    state.EntityManager.SetComponentData(spawnPrototype, new InvertSecondary2AnimationOverride { Value = runtimeData.InvertSecondary2Animation });
                    state.EntityManager.SetComponentData(spawnPrototype, new SideToSideAmplitudeOverride { Value = runtimeData.SideToSideAmplitude });
                    state.EntityManager.SetComponentData(spawnPrototype, new YawAmplitudeOverride { Value = runtimeData.YawAmplitude });
                    state.EntityManager.SetComponentData(spawnPrototype, new RollingSpineAmplitudeOverride { Value = runtimeData.RollingSpineAmplitude });
                    state.EntityManager.SetComponentData(spawnPrototype, new PositiveYClipOverride { Value = runtimeData.PositiveYClip });
                    state.EntityManager.SetComponentData(spawnPrototype, new NegativeYClipOverride { Value = runtimeData.NegativeYClip });

                    if (runtimeData.SeabedBound)
                    {
                        if (state.EntityManager.HasComponent<OpenWaterBoidTag>(spawnPrototype))
                        {
                            state.EntityManager.RemoveComponent<OpenWaterBoidTag>(spawnPrototype);
                        }
                        if (state.EntityManager.HasComponent<SeabedBoidTag>(spawnPrototype) == false)
                        {
                            state.EntityManager.AddComponent<SeabedBoidTag>(spawnPrototype);
                        }
                    }
                    else
                    {
                        if (state.EntityManager.HasComponent<SeabedBoidTag>(spawnPrototype))
                        {
                            state.EntityManager.RemoveComponent<SeabedBoidTag>(spawnPrototype);
                        }
                        if (state.EntityManager.HasComponent<OpenWaterBoidTag>(spawnPrototype) == false)
                        {
                            state.EntityManager.AddComponent<OpenWaterBoidTag>(spawnPrototype);
                        }
                    }

                    if (runtimeData.BoneAnimated)
                    {
                        if (state.EntityManager.HasComponent<DisableRendering>(spawnPrototype) == false)
                        {
                            state.EntityManager.AddComponent<DisableRendering>(spawnPrototype);
                        }
                    }
                    else
                    {
                        if (state.EntityManager.HasComponent<DisableRendering>(spawnPrototype))
                        {
                            state.EntityManager.RemoveComponent<DisableRendering>(spawnPrototype);
                        }
                    }

                    if (runtimeData.Predator)
                    {
                        if (state.EntityManager.HasComponent<BoidPredator>(spawnPrototype) == false)
                        {
                            state.EntityManager.AddComponent<BoidPredator>(spawnPrototype);
                        }
                    }
                    else
                    {
                        if (state.EntityManager.HasComponent<BoidPredator>(spawnPrototype))
                        {
                            state.EntityManager.RemoveComponent<BoidPredator>(spawnPrototype);
                        }
                    }

                    if (runtimeData.Prey)
                    {
                        if (state.EntityManager.HasComponent<BoidPrey>(spawnPrototype) == false)
                        {
                            state.EntityManager.AddComponent<BoidPrey>(spawnPrototype);
                        }
                        if (state.EntityManager.HasComponent<EscapingPredator>(spawnPrototype) == false)
                        {
                            state.EntityManager.AddComponent<EscapingPredator>(spawnPrototype);
                        }
                        state.EntityManager.SetComponentEnabled<EscapingPredator>(spawnPrototype, false);
                    }
                    else
                    {
                        if (state.EntityManager.HasComponent<BoidPrey>(spawnPrototype))
                        {
                            state.EntityManager.RemoveComponent<BoidPrey>(spawnPrototype);
                        }
                        if (state.EntityManager.HasComponent<EscapingPredator>(spawnPrototype))
                        {
                            state.EntityManager.RemoveComponent<EscapingPredator>(spawnPrototype);
                        }
                    }

                    state.EntityManager.SetComponentEnabled<BoidUnique>(spawnPrototype, false);
                    state.EntityManager.SetComponentEnabled<BoidSpawnPending>(spawnPrototype, true);

                    spawnPrototypeData.Value = spawnPrototype;
                    state.EntityManager.SetComponentData(schoolEntity, spawnPrototypeData);
                }
            }
            finally
            {
                schoolEntities.Dispose();
                schoolComponents.Dispose();
                runtimeDataArray.Dispose();
                spawnPrototypeArray.Dispose();
            }
        }
    }

    /// <summary>
    /// System responsible for managing boid schools and their member boids.
    /// Boid schools own their spawned boids, cached spawn prototype, and per-school runtime settings.
    /// </summary>
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BoidSchoolSpawnPrototypeSetupSystem))]
    [UpdateBefore(typeof(BoidSystem))]
    public partial struct BoidSchoolSpawnSystem : ISystem
    {
        private int previousStaticGroupsCount;
        private EntityQuery fixedStepTimeQuery;
        private EntityQuery sceneDataQuery;
        private EntityQuery seabedSurfaceQuery;
        private EntityQuery staticGroupsQuery;
        private EntityQuery boidSchoolQuery;

        public void OnCreate(ref SystemState state)
        {
            fixedStepTimeQuery = state.EntityManager.CreateEntityQuery(typeof(BoidFixedStepTime));
            sceneDataQuery = state.EntityManager.CreateEntityQuery(typeof(SceneData));
            seabedSurfaceQuery = state.EntityManager.CreateEntityQuery(typeof(SeabedSurfaceData));
            staticGroupsQuery = SystemAPI.QueryBuilder().WithAll<StaticEntitiesGroupComponent>().Build();
            boidSchoolQuery = SystemAPI.QueryBuilder()
                .WithAll<BoidSchoolComponent, BoidSchoolRuntimeData, BoidSchoolSpawnPrototype>()
                .Build();

            state.RequireForUpdate(fixedStepTimeQuery);
            state.RequireForUpdate(sceneDataQuery);
            state.RequireForUpdate(boidSchoolQuery);
        }

        public void OnUpdate(ref SystemState state)
        {
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
            var world = state.World.Unmanaged;
            SceneData sceneData = sceneDataQuery.GetSingleton<SceneData>();
            BoidFixedStepTime fixedStepTime = fixedStepTimeQuery.GetSingleton<BoidFixedStepTime>();
            int fixedStepCount = fixedStepTime.StepCount;
            float fixedStep = fixedStepTime.FixedStep;

            int staticGroupsCount = staticGroupsQuery.CalculateEntityCount();
            if (previousStaticGroupsCount > 0 && staticGroupsCount == 0)
            {
                foreach (var (schoolRO, schoolEntity) in SystemAPI.Query<RefRO<BoidSchoolComponent>>().WithEntityAccess())
                {
                    BoidSchoolComponent school = schoolRO.ValueRO;
                    school.TargetRepositionIteration = 0;
                    school.TargetRepositionTimer = 0.0f;
                    entityCommandBuffer.SetComponent(schoolEntity, school);
                }
            }
            previousStaticGroupsCount = staticGroupsCount;

            NativeArray<Entity> schoolEntities = boidSchoolQuery.ToEntityArray(Allocator.Temp);
            NativeArray<BoidSchoolComponent> schoolComponents = boidSchoolQuery.ToComponentDataArray<BoidSchoolComponent>(Allocator.Temp);
            NativeArray<BoidSchoolRuntimeData> schoolRuntimeData = boidSchoolQuery.ToComponentDataArray<BoidSchoolRuntimeData>(Allocator.Temp);
            NativeArray<BoidSchoolSpawnPrototype> schoolSpawnPrototypes = boidSchoolQuery.ToComponentDataArray<BoidSchoolSpawnPrototype>(Allocator.Temp);

            try
            {
                for (int schoolIndex = 0; schoolIndex < schoolEntities.Length; schoolIndex++)
                {
                    Entity schoolEntity = schoolEntities[schoolIndex];
                    BoidSchoolComponent school = schoolComponents[schoolIndex];
                    BoidSchoolRuntimeData runtimeData = schoolRuntimeData[schoolIndex];
                    BoidSchoolSpawnPrototype spawnPrototype = schoolSpawnPrototypes[schoolIndex];
                    DynamicBuffer<BoidSchoolOwnedBoid> ownedBoids = state.EntityManager.GetBuffer<BoidSchoolOwnedBoid>(schoolEntity);

                    CompactOwnedBoids(state.EntityManager, ownedBoids);

                    if (school.DestroyRequested)
                    {
                        if (school.Target != Entity.Null)
                        {
                            entityCommandBuffer.DestroyEntity(school.Target);
                        }

                        if (spawnPrototype.Value != Entity.Null && state.EntityManager.Exists(spawnPrototype.Value))
                        {
                            entityCommandBuffer.DestroyEntity(spawnPrototype.Value);
                        }

                        for (int i = 0; i < ownedBoids.Length; i++)
                        {
                            entityCommandBuffer.DestroyEntity(ownedBoids[i].Value);
                        }

                        entityCommandBuffer.DestroyEntity(schoolEntity);
                        continue;
                    }

                    BoidSchoolComponent boidSchoolCopy = school;

                    if (school.Target == Entity.Null)
                    {
                        Entity targetEntity = entityCommandBuffer.Instantiate(school.BoidTargetPrefab);
                        entityCommandBuffer.SetComponent(targetEntity, new BoidTarget
                        {
                            BoidSchoolId = school.BoidSchoolId,
                            DynamicEntityId = school.DynamicEntityId
                        });
                        entityCommandBuffer.SetName(targetEntity, "BoidTarget_" + school.DynamicEntityId + "_" + school.BoidSchoolId);

                        float3 pos = GenerateDeterministicPositionWithinBoundary(
                            runtimeData.Boundary,
                            (uint)school.DynamicEntityId,
                            (uint)school.BoidSchoolId,
                            0u);
                        entityCommandBuffer.SetComponent(targetEntity, new LocalTransform
                        {
                            Position = pos,
                            Rotation = quaternion.identity,
                            Scale = 1.0f
                        });

                        boidSchoolCopy.Target = targetEntity;
                    }
                    else
                    {
                        if (school.Count != school.RequestedCount)
                        {
                            if (school.RequestedCount > school.Count)
                            {
                                Assert.IsTrue(spawnPrototype.Value != Entity.Null && state.EntityManager.Exists(spawnPrototype.Value), "BoidSchoolSpawnSystem requires a valid school-specific spawn prototype before spawning boids.");
                                int amountToInstantiate = school.RequestedCount - school.Count;
                                var boidEntities = CollectionHelper.CreateNativeArray<Entity, RewindableAllocator>(amountToInstantiate, ref world.UpdateAllocator);
                                state.EntityManager.Instantiate(spawnPrototype.Value, boidEntities);

                                for (int i = 0; i < boidEntities.Length; i++)
                                {
                                    ownedBoids.Add(new BoidSchoolOwnedBoid
                                    {
                                        Value = boidEntities[i]
                                    });
                                }

                                SeabedSurfaceData seabedSurfaceData = default;
                                if (runtimeData.SeabedBound)
                                {
                                    Assert.IsTrue(seabedSurfaceQuery.CalculateEntityCount() == 1, "BoidSchoolSpawnSystem requires exactly one SeabedSurfaceData when spawning seabed-bound boids.");
                                    seabedSurfaceData = seabedSurfaceQuery.GetSingleton<SeabedSurfaceData>();
                                    Assert.IsTrue(seabedSurfaceData.HeightmapDataBlobRef.IsCreated, "BoidSchoolSpawnSystem requires valid seabed height data when spawning seabed-bound boids.");
                                    Assert.IsTrue(seabedSurfaceData.NormalDataBlobRef.IsCreated, "BoidSchoolSpawnSystem requires valid seabed normal data when spawning seabed-bound boids.");
                                }

                                var initializeSpawnedBoidsJob = new InitializeSpawnedBoids
                                {
                                    LocalToWorldFromEntity = SystemAPI.GetComponentLookup<LocalToWorld>(),
                                    BoidUniqueFromEntity = SystemAPI.GetComponentLookup<BoidUnique>(),
                                    SpawnPendingFromEntity = SystemAPI.GetComponentLookup<BoidSpawnPending>(),
                                    AnimationRandomOffsetFromEntity = SystemAPI.GetComponentLookup<AnimationRandomOffsetOverride>(),
                                    Entities = boidEntities,
                                    RuntimeData = runtimeData,
                                    SeabedSurface = seabedSurfaceData
                                };
                                state.Dependency = initializeSpawnedBoidsJob.Schedule(amountToInstantiate, 64, state.Dependency);
                                boidSchoolCopy.Count = school.RequestedCount;
                            }
                            else
                            {
                                int entitiesToDestroy = school.Count - school.RequestedCount;
                                int boidIndex = ownedBoids.Length - 1;
                                while (boidIndex >= 0 && entitiesToDestroy > 0)
                                {
                                    Entity boidEntity = ownedBoids[boidIndex].Value;
                                    ownedBoids.RemoveAt(boidIndex);
                                    if (state.EntityManager.Exists(boidEntity))
                                    {
                                        entityCommandBuffer.DestroyEntity(boidEntity);
                                        entitiesToDestroy--;
                                    }
                                    boidIndex--;
                                }

                                boidSchoolCopy.Count = school.RequestedCount;
                            }
                        }
                        else if (school.ShaderUpdateRequested)
                        {
                            int boidTotal = ownedBoids.Length;
                            for (int i = 0; i < ownedBoids.Length; i++)
                            {
                                Entity boidEntity = ownedBoids[i].Value;
                                if (state.EntityManager.Exists(boidEntity) == false)
                                {
                                    continue;
                                }

                                BoidSchoolRuntimeUtility.BuildScreenDisplayRange(runtimeData, i, boidTotal, out float4 screenDisplayStart, out float4 screenDisplayEnd);
                                entityCommandBuffer.SetComponent(boidEntity, new ScreenDisplayStartOverride { Value = screenDisplayStart });
                                entityCommandBuffer.SetComponent(boidEntity, new ScreenDisplayEndOverride { Value = screenDisplayEnd });
                            }

                            boidSchoolCopy.ShaderUpdateRequested = false;
                        }
                    }

                    if (school.Target != Entity.Null)
                    {
                        if (school.TargetRepositionTimer <= 0.0f)
                        {
                            Entity targetEntity = school.Target;
                            float3 currentTargetPosition = state.EntityManager.GetComponentData<LocalToWorld>(targetEntity).Position;
                            float3 newTargetPosition = GenerateDeterministicPositionWithinBoundary(
                                runtimeData.Boundary,
                                (uint)school.DynamicEntityId,
                                (uint)school.BoidSchoolId,
                                (uint)(1 + school.TargetRepositionIteration));

                            uint seed = math.hash(new uint3((uint)school.DynamicEntityId, (uint)school.BoidSchoolId, (uint)school.TargetRepositionIteration));
                            Unity.Mathematics.Random random = Unity.Mathematics.Random.CreateFromIndex(seed);
                            float repositionDuration = GetRandomFloat(ref random, school.StateChangeTimerMin, school.StateChangeTimerMax);
                            boidSchoolCopy.TargetRepositionTimer = repositionDuration;
                            boidSchoolCopy.TargetRepositionIteration = school.TargetRepositionIteration + 1;

                            entityCommandBuffer.SetComponent(targetEntity, new BoidTarget
                            {
                                BoidSchoolId = school.BoidSchoolId,
                                DynamicEntityId = school.DynamicEntityId,
                                StartPosition = currentTargetPosition,
                                EndPosition = newTargetPosition,
                                LerpDuration = repositionDuration,
                                LerpTimer = 0.0f
                            });

                            for (int i = 0; i < ownedBoids.Length; i++)
                            {
                                Entity boidEntity = ownedBoids[i].Value;
                                if (state.EntityManager.Exists(boidEntity) == false)
                                {
                                    continue;
                                }

                                bool isEscapingPredator = false;
                                if (state.EntityManager.HasComponent<EscapingPredator>(boidEntity))
                                {
                                    isEscapingPredator = state.EntityManager.IsComponentEnabled<EscapingPredator>(boidEntity);
                                }

                                if (isEscapingPredator == false)
                                {
                                    BoidUnique boidUnique = state.EntityManager.GetComponentData<BoidUnique>(boidEntity);
                                    uint speedSeed = math.hash(new uint4(
                                        (uint)school.DynamicEntityId,
                                        (uint)school.BoidSchoolId,
                                        (uint)boidEntity.Index,
                                        (uint)boidSchoolCopy.TargetRepositionIteration));
                                    Unity.Mathematics.Random speedRng = Unity.Mathematics.Random.CreateFromIndex(speedSeed);
                                    boidUnique.TargetSpeedModifier = speedRng.NextFloat(
                                        school.SpeedModifierMin,
                                        school.SpeedModifierMax);
                                    entityCommandBuffer.SetComponent(boidEntity, boidUnique);
                                }
                            }
                        }
                        else
                        {
                            Entity targetEntity = school.Target;
                            BoidTarget boidTarget = state.EntityManager.GetComponentData<BoidTarget>(targetEntity);
                            Assert.IsTrue(boidTarget.LerpDuration > 0.0f, "Boid target interpolation duration must be positive.");
                            for (int fixedStepIndex = 0; fixedStepIndex < fixedStepCount; fixedStepIndex++)
                            {
                                boidTarget.LerpTimer += fixedStep;
                                boidSchoolCopy.TargetRepositionTimer -= fixedStep;
                            }

                            if (boidSchoolCopy.TargetRepositionTimer < 0.0f)
                            {
                                boidSchoolCopy.TargetRepositionTimer = 0.0f;
                            }

                            float t = math.saturate(boidTarget.LerpTimer / boidTarget.LerpDuration);
                            float3 newPosition = math.lerp(boidTarget.StartPosition, boidTarget.EndPosition, t);

                            entityCommandBuffer.SetComponent(targetEntity, new LocalTransform
                            {
                                Position = newPosition,
                                Rotation = quaternion.identity,
                                Scale = 1.0f
                            });
                            entityCommandBuffer.SetComponent(targetEntity, boidTarget);
                        }
                    }

                    entityCommandBuffer.SetComponent(schoolEntity, boidSchoolCopy);
                }
            }
            finally
            {
                schoolEntities.Dispose();
                schoolComponents.Dispose();
                schoolRuntimeData.Dispose();
                schoolSpawnPrototypes.Dispose();
            }

            entityCommandBuffer.Playback(state.EntityManager);
        }

        private static void CompactOwnedBoids(EntityManager entityManager, DynamicBuffer<BoidSchoolOwnedBoid> ownedBoids)
        {
            int writeIndex = 0;
            for (int readIndex = 0; readIndex < ownedBoids.Length; readIndex++)
            {
                Entity entity = ownedBoids[readIndex].Value;
                if (entityManager.Exists(entity) == false)
                {
                    continue;
                }

                if (writeIndex != readIndex)
                {
                    ownedBoids[writeIndex] = ownedBoids[readIndex];
                }
                writeIndex++;
            }

            if (writeIndex < ownedBoids.Length)
            {
                ownedBoids.RemoveRange(writeIndex, ownedBoids.Length - writeIndex);
            }
        }

        private static float GetRandomFloat(ref Unity.Mathematics.Random random, float min, float max)
        {
            return random.NextFloat(min, max);
        }

        private static float3 GenerateDeterministicPositionWithinBoundary(in BoidBoundaryData boundary, uint dynamicEntityId, uint boidSchoolId, uint salt)
        {
            uint seed = math.hash(new uint3(dynamicEntityId, boidSchoolId, salt));
            Unity.Mathematics.Random random = Unity.Mathematics.Random.CreateFromIndex(seed);
            for (int attempt = 0; attempt < 32; attempt++)
            {
                float3 position = new float3(
                    random.NextFloat(boundary.BoundsMin.x, boundary.BoundsMax.x),
                    random.NextFloat(boundary.BoundsMin.y, boundary.BoundsMax.y),
                    random.NextFloat(boundary.BoundsMin.z, boundary.BoundsMax.z));
                if (BoidBoundaryUtility.Contains(boundary, position))
                {
                    return position;
                }
            }

            float3 fallbackPosition = boundary.BoundsCenter;
            BoidBoundaryUtility.TryProjectInside(boundary, fallbackPosition, out fallbackPosition, out float3 _, out float _);
            Assert.IsTrue(BoidBoundaryUtility.Contains(boundary, fallbackPosition), "BoidSchoolSpawnSystem could not find a valid point inside boid bounds.");
            return fallbackPosition;
        }
    }

    /// <summary>
    /// Initializes newly spawned boids off the main thread, including terrain-aligned seabed placement.
    /// Boids are enabled for simulation only after their first transform has been written.
    /// </summary>
    [BurstCompile]
    internal struct InitializeSpawnedBoids : IJobParallelFor
    {
        [NativeDisableContainerSafetyRestriction]
        [NativeDisableParallelForRestriction]
        public ComponentLookup<LocalToWorld> LocalToWorldFromEntity;

        [NativeDisableContainerSafetyRestriction]
        [NativeDisableParallelForRestriction]
        public ComponentLookup<BoidUnique> BoidUniqueFromEntity;

        [NativeDisableContainerSafetyRestriction]
        [NativeDisableParallelForRestriction]
        public ComponentLookup<BoidSpawnPending> SpawnPendingFromEntity;

        [NativeDisableContainerSafetyRestriction]
        [NativeDisableParallelForRestriction]
        public ComponentLookup<AnimationRandomOffsetOverride> AnimationRandomOffsetFromEntity;

        [ReadOnly] public NativeArray<Entity> Entities;
        [ReadOnly] public BoidSchoolRuntimeData RuntimeData;
        [ReadOnly] public SeabedSurfaceData SeabedSurface;

        public void Execute(int i)
        {
            Entity entity = Entities[i];
            uint seed = math.hash(new uint3((uint)RuntimeData.DynamicEntityId, (uint)RuntimeData.BoidSchoolId, (uint)i));
            Unity.Mathematics.Random random = Unity.Mathematics.Random.CreateFromIndex(seed);

            float randomAngle = random.NextFloat(0.0f, 2.0f * math.PI);
            float3 initialForward = new float3(math.sin(randomAngle), 0.0f, math.cos(randomAngle));

            float3 boundsCenter = RuntimeData.BoundsCenter;
            float3 boundsExtent = (RuntimeData.BoundsMax - RuntimeData.BoundsMin) * 0.5f;
            float oneMinusClustering = 1.0f - math.saturate(RuntimeData.SpawnClustering);
            float maxSpawnRadius = math.cmax(boundsExtent) * oneMinusClustering * oneMinusClustering;

            float radius = random.NextFloat(0.0f, maxSpawnRadius);
            float theta = random.NextFloat(0.0f, 2.0f * math.PI);
            float phi = random.NextFloat(0.0f, math.PI);

            float x = radius * math.sin(phi) * math.cos(theta);
            float y = radius * math.sin(phi) * math.sin(theta);
            float z = radius * math.cos(phi);

            float3 position = boundsCenter + new float3(x, y, z);
            position = math.clamp(position, RuntimeData.BoundsMin, RuntimeData.BoundsMax);
            if (BoidBoundaryUtility.Contains(RuntimeData.Boundary, position) == false)
            {
                position = GenerateSpawnPositionInBoundary(RuntimeData, ref random);
            }
            if (RuntimeData.SeabedBound)
            {
                position.y = boundsCenter.y;
            }

            float minScale = RuntimeData.ScaleMin;
            float maxScale = RuntimeData.ScaleMax;
            if (minScale <= 0.0f)
            {
                minScale = 0.7f;
            }
            if (maxScale < minScale)
            {
                maxScale = minScale;
            }

            float scale = math.round(random.NextFloat(minScale, maxScale) * 100.0f) / 100.0f;
            float3 scaleVector = new float3(scale, scale, scale);

            float3 finalPosition = position;
            quaternion finalRotation = quaternion.LookRotationSafe(initialForward, math.up());
            if (RuntimeData.SeabedBound)
            {
                SeabedSurfaceUtility.SampleSurface(SeabedSurface, position, out float3 seabedPosition, out float3 seabedNormal);
                finalPosition = seabedPosition;
                finalRotation = SeabedSurfaceUtility.AlignForwardToSurface(initialForward, seabedNormal, initialForward);
            }

            LocalToWorldFromEntity[entity] = new LocalToWorld
            {
                Value = float4x4.TRS(finalPosition, finalRotation, scaleVector)
            };

            AnimationRandomOffsetFromEntity[entity] = new AnimationRandomOffsetOverride
            {
                Value = random.NextFloat(-100.0f, 100.0f)
            };

            float3 finalForward = math.normalizesafe(math.mul(finalRotation, new float3(0.0f, 0.0f, 1.0f)), initialForward);
            BoidUnique boidUnique = BoidUniqueFromEntity[entity];
            boidUnique.Disabled = false;
            boidUnique.MoveSpeedModifier = 1.0f;
            boidUnique.TargetSpeedModifier = 1.0f;
            boidUnique.MaxVerticalAngleOffset = random.NextFloat(-15.0f, 20.0f);
            boidUnique.TargetVector = float3.zero;
            boidUnique.PreviousHeading = finalForward;
            boidUnique.BendRefHeading = finalForward;
            BoidUniqueFromEntity[entity] = boidUnique;
            BoidUniqueFromEntity.SetComponentEnabled(entity, true);
            SpawnPendingFromEntity.SetComponentEnabled(entity, false);
        }

        private static float3 GenerateSpawnPositionInBoundary(in BoidSchoolRuntimeData runtimeData, ref Unity.Mathematics.Random random)
        {
            for (int attempt = 0; attempt < 32; attempt++)
            {
                float3 position = new float3(
                    random.NextFloat(runtimeData.BoundsMin.x, runtimeData.BoundsMax.x),
                    random.NextFloat(runtimeData.BoundsMin.y, runtimeData.BoundsMax.y),
                    random.NextFloat(runtimeData.BoundsMin.z, runtimeData.BoundsMax.z));
                if (BoidBoundaryUtility.Contains(runtimeData.Boundary, position))
                {
                    return position;
                }
            }

            float3 fallbackPosition = runtimeData.BoundsCenter;
            BoidBoundaryUtility.TryProjectInside(runtimeData.Boundary, fallbackPosition, out fallbackPosition, out float3 _, out float _);
            Assert.IsTrue(BoidBoundaryUtility.Contains(runtimeData.Boundary, fallbackPosition), "InitializeSpawnedBoids could not find a valid point inside boid bounds.");
            return fallbackPosition;
        }
    }
}
