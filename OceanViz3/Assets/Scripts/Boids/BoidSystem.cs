using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Rendering;
using Unity.Assertions;

namespace OceanViz3
{
    public struct BoidFixedStepTime : IComponentData
    {
        public const float DefaultFixedStep = 1.0f / 60.0f;
        public const int DefaultMaxStepsPerFrame = 4;

        public float Accumulator;
        public float FixedStep;
        public float FixedElapsedTime;
        public float CurrentFrameStartElapsedTime;
        public int StepCount;
        public int MaxStepsPerFrame;
    }

    /// <summary>
    /// Converts variable render-frame time into fixed boid simulation steps.
    /// Large frames are clamped so boids do not try to simulate an unbounded backlog after a stall.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(BoidSchoolRuntimeSyncSystem))]
    public partial struct BoidFixedStepSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            Entity entity = state.EntityManager.CreateEntity(typeof(BoidFixedStepTime));
            state.EntityManager.SetName(entity, "Boid Fixed Step Time");
            state.EntityManager.SetComponentData(entity, new BoidFixedStepTime
            {
                Accumulator = 0.0f,
                FixedStep = BoidFixedStepTime.DefaultFixedStep,
                FixedElapsedTime = 0.0f,
                CurrentFrameStartElapsedTime = 0.0f,
                StepCount = 0,
                MaxStepsPerFrame = BoidFixedStepTime.DefaultMaxStepsPerFrame
            });
        }

        public void OnUpdate(ref SystemState state)
        {
            BoidFixedStepTime time = SystemAPI.GetSingleton<BoidFixedStepTime>();
            Assert.IsTrue(time.FixedStep > 0.0f, "Boid fixed step must be positive.");
            Assert.IsTrue(time.MaxStepsPerFrame > 0, "Boid max steps per frame must be positive.");

            float realDeltaTime = math.max(0.0f, SystemAPI.Time.DeltaTime);
            float maxAccumulatedTime = time.FixedStep * time.MaxStepsPerFrame;
            time.Accumulator = math.min(time.Accumulator + realDeltaTime, maxAccumulatedTime);
            time.CurrentFrameStartElapsedTime = time.FixedElapsedTime;
            time.StepCount = 0;

            while (time.Accumulator >= time.FixedStep && time.StepCount < time.MaxStepsPerFrame)
            {
                time.Accumulator -= time.FixedStep;
                time.FixedElapsedTime += time.FixedStep;
                time.StepCount++;
            }

            if (time.StepCount == time.MaxStepsPerFrame)
            {
                time.Accumulator = 0.0f;
            }

            SystemAPI.SetSingleton(time);
        }
    }

    /// <summary>
    /// System that calculates boid flocking behavior and updates positions using one global open-water pass and one global seabed pass.
    /// School-wide behavior is read from school-owned runtime data instead of per-boid shared component filters.
    /// </summary>
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(TransformSystemGroup))]
    public partial struct BoidSystem : ISystem
    {
        private const int INITIAL_HOVER_INDEX_CAPACITY = 1024;
        private const float BEND_GAIN = 0.5f;
        private const float BEND_MAX_ABS = 0.3f;
        private const float BEND_FLIP_TIME_SEC = 0.4f;
        private const float BEND_SLEW_RATE = (2.0f * BEND_MAX_ABS) / BEND_FLIP_TIME_SEC;
        private const float BEND_ANGVEL_DEADZONE = 0.08f;
        private const float BEND_RETURN_SPEED_MULTIPLIER = 4.0f;
        private const float BEND_ZERO_EPSILON = 0.008f;
        private const float PREDATOR_SIZE_TO_RADIUS_FACTOR = 1.0f;

        private EntityQuery fixedStepTimeQuery;
        private EntityQuery sceneDataQuery;
        private EntityQuery seabedSurfaceQuery;
        private EntityQuery schoolRuntimeQuery;
        private EntityQuery openWaterBoidsQuery;
        private EntityQuery seabedBoidsQuery;
        private EntityQuery obstacleQuery;
        private EntityQuery predatorQuery;
        private EntityQuery waterCurrentQuery;
        private EntityQuery hoverRequestQuery;
        private NativeParallelMultiHashMap<int, Entity> dynamicHoverIndex;
        private JobHandle dynamicHoverReadHandle;
        private JobHandle dynamicHoverWriteHandle;

        public NativeParallelMultiHashMap<int, Entity> DynamicHoverIndex => dynamicHoverIndex;

        public void OnCreate(ref SystemState state)
        {
            fixedStepTimeQuery = state.EntityManager.CreateEntityQuery(typeof(BoidFixedStepTime));
            sceneDataQuery = state.EntityManager.CreateEntityQuery(typeof(SceneData));
            seabedSurfaceQuery = state.EntityManager.CreateEntityQuery(typeof(SeabedSurfaceData));
            schoolRuntimeQuery = SystemAPI.QueryBuilder()
                .WithAll<BoidSchoolRuntimeData>()
                .Build();

            openWaterBoidsQuery = SystemAPI.QueryBuilder()
                .WithAllRW<LocalToWorld>()
                .WithAllRW<CurrentVectorOverride>()
                .WithAllRW<BoidUnique>()
                .WithAll<BoidSchoolMember, OpenWaterBoidTag>()
                .WithAll<AccumulatedTimeOverride, AnimationSpeedOverride>()
                .Build();

            seabedBoidsQuery = SystemAPI.QueryBuilder()
                .WithAllRW<LocalToWorld>()
                .WithAllRW<CurrentVectorOverride>()
                .WithAllRW<BoidUnique>()
                .WithAll<BoidSchoolMember, SeabedBoidTag>()
                .WithAll<AccumulatedTimeOverride, AnimationSpeedOverride>()
                .Build();

            obstacleQuery = SystemAPI.QueryBuilder()
                .WithAll<BoidObstacle, LocalToWorld>()
                .Build();

            predatorQuery = SystemAPI.QueryBuilder()
                .WithAll<BoidSchoolMember, BoidUnique, BoidPredator, LocalToWorld>()
                .Build();

            waterCurrentQuery = SystemAPI.QueryBuilder()
                .WithAll<WaterCurrentSettings>()
                .Build();
            hoverRequestQuery = state.EntityManager.CreateEntityQuery(typeof(EntityHoverRequest));
            dynamicHoverIndex = new NativeParallelMultiHashMap<int, Entity>(
                INITIAL_HOVER_INDEX_CAPACITY,
                Allocator.Persistent);

            state.RequireForUpdate(fixedStepTimeQuery);
            state.RequireForUpdate(sceneDataQuery);
            state.RequireForUpdate(schoolRuntimeQuery);
        }

        public void OnDestroy(ref SystemState state)
        {
            state.Dependency.Complete();
            dynamicHoverReadHandle.Complete();
            dynamicHoverWriteHandle.Complete();
            if (dynamicHoverIndex.IsCreated)
            {
                dynamicHoverIndex.Dispose();
            }
        }

        public JobHandle GetDynamicHoverReadDependency()
        {
            return dynamicHoverWriteHandle;
        }

        public void RegisterDynamicHoverRead(JobHandle readHandle)
        {
            dynamicHoverReadHandle = JobHandle.CombineDependencies(
                dynamicHoverReadHandle,
                readHandle);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            int openWaterBoidCount = openWaterBoidsQuery.CalculateEntityCount();
            int seabedBoidCount = seabedBoidsQuery.CalculateEntityCount();
            int totalBoidCount = openWaterBoidCount + seabedBoidCount;
            int obstacleCount = obstacleQuery.CalculateEntityCount();
            int predatorCount = predatorQuery.CalculateEntityCount();
            int schoolCount = schoolRuntimeQuery.CalculateEntityCount();

            if (totalBoidCount == 0 || schoolCount == 0)
            {
                return;
            }

            BoidFixedStepTime fixedStepTime = fixedStepTimeQuery.GetSingleton<BoidFixedStepTime>();
            if (fixedStepTime.StepCount <= 0)
            {
                return;
            }

            Assert.IsTrue(fixedStepTime.FixedElapsedTime >= fixedStepTime.CurrentFrameStartElapsedTime, "Boid fixed elapsed time cannot go backwards.");

            WaterCurrentSettings waterCurrentSettings = default;
            int waterCurrentCount = waterCurrentQuery.CalculateEntityCount();
            Assert.IsTrue(waterCurrentCount <= 1, "BoidSystem expects at most one WaterCurrentSettings singleton.");
            if (waterCurrentCount == 1)
            {
                waterCurrentSettings = waterCurrentQuery.GetSingleton<WaterCurrentSettings>();
            }

            float fixedStep = fixedStepTime.FixedStep;
            float fixedElapsedTime = fixedStepTime.CurrentFrameStartElapsedTime;
            for (int stepIndex = 0; stepIndex < fixedStepTime.StepCount; stepIndex++)
            {
                fixedElapsedTime += fixedStep;
                state.Dependency = ScheduleBoidSimulationStep(
                    ref state,
                    fixedStep,
                    fixedElapsedTime,
                    openWaterBoidCount,
                    seabedBoidCount,
                    obstacleCount,
                    predatorCount,
                    waterCurrentSettings);
            }
        }

        private JobHandle ScheduleBoidSimulationStep(
            ref SystemState state,
            float deltaTime,
            float elapsedTime,
            int openWaterBoidCount,
            int seabedBoidCount,
            int obstacleCount,
            int predatorCount,
            WaterCurrentSettings waterCurrentSettings)
        {
            var world = state.WorldUnmanaged;

            // Target positions are read on the main thread from LocalToWorld.
            // Finish any prior fixed-step transform writes before taking that snapshot.
            state.Dependency.Complete();

            HoverSpatialIndexWriter hoverIndexWriter = new HoverSpatialIndexWriter
            {
                Enabled = false,
                Writer = dynamicHoverIndex.AsParallelWriter()
            };
            if (hoverRequestQuery.CalculateEntityCount() == 1 &&
                hoverRequestQuery.GetSingleton<EntityHoverRequest>().Active)
            {
                TryBeginDynamicHoverBuild(
                    openWaterBoidCount + seabedBoidCount,
                    out hoverIndexWriter);
            }

            var localToWorldLookup = SystemAPI.GetComponentLookup<LocalToWorld>(true);
            var schoolRuntimeLookup = SystemAPI.GetComponentLookup<BoidSchoolRuntimeData>(true);
            SeabedSurfaceData seabedSurfaceData = default;
            if (seabedBoidCount > 0)
            {
                seabedSurfaceData = seabedSurfaceQuery.GetSingleton<SeabedSurfaceData>();
                Assert.IsTrue(seabedSurfaceData.HeightmapDataBlobRef.IsCreated, "BoidSystem found seabed-bound boids without valid terrain surface height data.");
                Assert.IsTrue(seabedSurfaceData.NormalDataBlobRef.IsCreated, "BoidSystem found seabed-bound boids without valid terrain surface normal data.");
            }

            NativeArray<BoidSchoolRuntimeData> schoolRuntimeData = schoolRuntimeQuery.ToComponentDataArray<BoidSchoolRuntimeData>(Allocator.TempJob);
            NativeParallelHashMap<int, int> schoolIndexToRuntimeIndex = new NativeParallelHashMap<int, int>(math.max(1, schoolRuntimeData.Length), Allocator.TempJob);
            NativeArray<float3> targetPositions = new NativeArray<float3>(schoolRuntimeData.Length, Allocator.TempJob);

            for (int i = 0; i < schoolRuntimeData.Length; i++)
            {
                schoolIndexToRuntimeIndex.Add(schoolRuntimeData[i].SchoolIndex, i);
                float3 targetPosition = schoolRuntimeData[i].BoundsCenter;
                Entity targetEntity = schoolRuntimeData[i].Target;
                if (targetEntity != Entity.Null && localToWorldLookup.HasComponent(targetEntity))
                {
                    targetPosition = localToWorldLookup[targetEntity].Position;
                }
                targetPositions[i] = targetPosition;
            }

            var copyObstaclePositions = CollectionHelper.CreateNativeArray<float3, RewindableAllocator>(obstacleCount, ref world.UpdateAllocator);
            var copyObstacleSizes = CollectionHelper.CreateNativeArray<float3, RewindableAllocator>(obstacleCount, ref world.UpdateAllocator);
            var copyPredatorPositions = CollectionHelper.CreateNativeArray<float3, RewindableAllocator>(predatorCount, ref world.UpdateAllocator);
            var copyPredatorSizes = CollectionHelper.CreateNativeArray<float, RewindableAllocator>(predatorCount, ref world.UpdateAllocator);

            var obstacleChunkBaseEntityIndexArray = obstacleQuery.CalculateBaseEntityIndexArrayAsync(
                world.UpdateAllocator.ToAllocator, state.Dependency,
                out var obstacleChunkBaseIndexJobHandle);
            var predatorChunkBaseEntityIndexArray = predatorQuery.CalculateBaseEntityIndexArrayAsync(
                world.UpdateAllocator.ToAllocator, state.Dependency,
                out var predatorChunkBaseIndexJobHandle);

            var initialObstacleJob = new InitialPerObstacleJob
            {
                ChunkBaseEntityIndices = obstacleChunkBaseEntityIndexArray,
                ObstaclePositions = copyObstaclePositions,
                ObstacleSizes = copyObstacleSizes
            };
            var initialObstacleJobHandle = initialObstacleJob.ScheduleParallel(obstacleQuery, obstacleChunkBaseIndexJobHandle);

            var initialPredatorJob = new InitialPerPredatorJob
            {
                ChunkBaseEntityIndices = predatorChunkBaseEntityIndexArray,
                PredatorPositions = copyPredatorPositions,
                PredatorSizes = copyPredatorSizes,
                SchoolRuntimeLookup = schoolRuntimeLookup
            };
            var initialPredatorJobHandle = initialPredatorJob.ScheduleParallel(predatorQuery, predatorChunkBaseIndexJobHandle);

            var sharedEnvironmentJobHandle = JobHandle.CombineDependencies(initialObstacleJobHandle, initialPredatorJobHandle);
            JobHandle combinedBoidHandle = sharedEnvironmentJobHandle;

            if (openWaterBoidCount > 0)
            {
                JobHandle openWaterHandle = ScheduleOpenWaterPass(
                    ref state,
                    world,
                    deltaTime,
                    elapsedTime,
                    copyObstaclePositions,
                    copyObstacleSizes,
                    copyPredatorPositions,
                    copyPredatorSizes,
                    schoolRuntimeLookup,
                    schoolRuntimeData,
                    schoolIndexToRuntimeIndex,
                    targetPositions,
                    waterCurrentSettings,
                    hoverIndexWriter,
                    sharedEnvironmentJobHandle);
                combinedBoidHandle = JobHandle.CombineDependencies(combinedBoidHandle, openWaterHandle);
            }

            if (seabedBoidCount > 0)
            {
                JobHandle seabedHandle = ScheduleSeabedPass(
                    ref state,
                    world,
                    deltaTime,
                    elapsedTime,
                    copyObstaclePositions,
                    copyObstacleSizes,
                    copyPredatorPositions,
                    copyPredatorSizes,
                    schoolRuntimeLookup,
                    schoolRuntimeData,
                    schoolIndexToRuntimeIndex,
                    targetPositions,
                    seabedSurfaceData,
                    hoverIndexWriter,
                    sharedEnvironmentJobHandle);
                combinedBoidHandle = JobHandle.CombineDependencies(combinedBoidHandle, seabedHandle);
            }

            JobHandle runtimeDisposeHandle = schoolRuntimeData.Dispose(combinedBoidHandle);
            JobHandle targetDisposeHandle = targetPositions.Dispose(combinedBoidHandle);
            JobHandle mapDisposeHandle = schoolIndexToRuntimeIndex.Dispose(combinedBoidHandle);
            JobHandle disposeHandle = JobHandle.CombineDependencies(runtimeDisposeHandle, targetDisposeHandle, mapDisposeHandle);
            JobHandle finalHandle = JobHandle.CombineDependencies(combinedBoidHandle, disposeHandle);
            if (hoverIndexWriter.Enabled)
            {
                dynamicHoverWriteHandle = JobHandle.CombineDependencies(
                    dynamicHoverWriteHandle,
                    finalHandle);
            }
            return finalHandle;
        }

        private bool TryBeginDynamicHoverBuild(
            int entityCount,
            out HoverSpatialIndexWriter writer)
        {
            writer = new HoverSpatialIndexWriter
            {
                Enabled = false,
                Writer = dynamicHoverIndex.AsParallelWriter()
            };
            if (!dynamicHoverReadHandle.IsCompleted ||
                !dynamicHoverWriteHandle.IsCompleted)
            {
                return false;
            }

            dynamicHoverReadHandle.Complete();
            dynamicHoverReadHandle = default;
            dynamicHoverWriteHandle.Complete();
            dynamicHoverWriteHandle = default;

            int targetCapacity = math.max(
                INITIAL_HOVER_INDEX_CAPACITY,
                entityCount);
            if (dynamicHoverIndex.Capacity < targetCapacity)
            {
                dynamicHoverIndex.Capacity = targetCapacity;
            }
            dynamicHoverIndex.Clear();
            writer = new HoverSpatialIndexWriter
            {
                Enabled = true,
                Writer = dynamicHoverIndex.AsParallelWriter()
            };
            return true;
        }

        private JobHandle ScheduleOpenWaterPass(
            ref SystemState state,
            WorldUnmanaged world,
            float deltaTime,
            float elapsedTime,
            NativeArray<float3> obstaclePositions,
            NativeArray<float3> obstacleSizes,
            NativeArray<float3> predatorPositions,
            NativeArray<float> predatorSizes,
            ComponentLookup<BoidSchoolRuntimeData> schoolRuntimeLookup,
            NativeArray<BoidSchoolRuntimeData> schoolRuntimeData,
            NativeParallelHashMap<int, int> schoolIndexToRuntimeIndex,
            NativeArray<float3> targetPositions,
            WaterCurrentSettings waterCurrentSettings,
            HoverSpatialIndexWriter hoverIndexWriter,
            JobHandle dependency)
        {
            int boidCount = openWaterBoidsQuery.CalculateEntityCount();
            var hashMap = new NativeParallelMultiHashMap<int, int>(boidCount, world.UpdateAllocator.ToAllocator);
            var cellIndices = CollectionHelper.CreateNativeArray<int, RewindableAllocator>(boidCount, ref world.UpdateAllocator);
            var cellCount = CollectionHelper.CreateNativeArray<int, RewindableAllocator>(boidCount, ref world.UpdateAllocator);
            var cellObstaclePositionIndex = CollectionHelper.CreateNativeArray<int, RewindableAllocator>(boidCount, ref world.UpdateAllocator);
            var cellObstacleDistance = CollectionHelper.CreateNativeArray<float, RewindableAllocator>(boidCount, ref world.UpdateAllocator);
            var cellPredatorPositionIndex = CollectionHelper.CreateNativeArray<int, RewindableAllocator>(boidCount, ref world.UpdateAllocator);
            var cellPredatorDistance = CollectionHelper.CreateNativeArray<float, RewindableAllocator>(boidCount, ref world.UpdateAllocator);
            var cellAlignment = CollectionHelper.CreateNativeArray<float3, RewindableAllocator>(boidCount, ref world.UpdateAllocator);
            var cellSeparation = CollectionHelper.CreateNativeArray<float3, RewindableAllocator>(boidCount, ref world.UpdateAllocator);

            var boidChunkBaseEntityIndexArray = openWaterBoidsQuery.CalculateBaseEntityIndexArrayAsync(
                world.UpdateAllocator.ToAllocator,
                state.Dependency,
                out var boidChunkBaseIndexJobHandle);

            var initialBoidJob = new InitialPerBoidJob
            {
                ChunkBaseEntityIndices = boidChunkBaseEntityIndexArray,
                CellAlignment = cellAlignment,
                CellSeparation = cellSeparation,
                ParallelHashMap = hashMap.AsParallelWriter(),
                SchoolRuntimeLookup = schoolRuntimeLookup,
                HoverGroupLookup = SystemAPI.GetComponentLookup<EntityHoverGroup>(true),
                HoverIndexWriter = hoverIndexWriter
            };
            var initialBoidJobHandle = initialBoidJob.ScheduleParallel(openWaterBoidsQuery, boidChunkBaseIndexJobHandle);

            var initialCellCountJob = new MemsetNativeArray<int>
            {
                Source = cellCount,
                Value = 1
            };
            var initialCellCountJobHandle = initialCellCountJob.Schedule(boidCount, 64, state.Dependency);

            var initialCellBarrierJobHandle = JobHandle.CombineDependencies(initialBoidJobHandle, initialCellCountJobHandle);
            var mergeCellsBarrierJobHandle = JobHandle.CombineDependencies(initialCellBarrierJobHandle, dependency);

            var mergeCellsJob = new MergeCells
            {
                CellIndices = cellIndices,
                CellAlignment = cellAlignment,
                CellSeparation = cellSeparation,
                ObstaclePositions = obstaclePositions,
                CellObstacleDistance = cellObstacleDistance,
                CellObstaclePositionIndex = cellObstaclePositionIndex,
                PredatorPositions = predatorPositions,
                CellPredatorDistance = cellPredatorDistance,
                CellPredatorPositionIndex = cellPredatorPositionIndex,
                CellCount = cellCount
            };
            var mergeCellsJobHandle = mergeCellsJob.Schedule(hashMap, 64, mergeCellsBarrierJobHandle);

            var steerOpenWaterBoidJob = new SteerOpenWaterBoidJob
            {
                ChunkBaseEntityIndices = boidChunkBaseEntityIndexArray,
                CellIndices = cellIndices,
                CellCount = cellCount,
                CellAlignment = cellAlignment,
                CellSeparation = cellSeparation,
                ObstaclePositions = obstaclePositions,
                CellObstacleDistance = cellObstacleDistance,
                CellObstaclePositionIndex = cellObstaclePositionIndex,
                ObstacleDimensions = obstacleSizes,
                PredatorPositions = predatorPositions,
                PredatorSizes = predatorSizes,
                CellPredatorDistance = cellPredatorDistance,
                CellPredatorPositionIndex = cellPredatorPositionIndex,
                SchoolRuntimeData = schoolRuntimeData,
                SchoolIndexToRuntimeIndex = schoolIndexToRuntimeIndex,
                TargetPositions = targetPositions,
                WaterCurrent = waterCurrentSettings,
                DeltaTime = deltaTime,
                ElapsedTime = elapsedTime
            };
            JobHandle finalTransformJobHandle = steerOpenWaterBoidJob.ScheduleParallel(openWaterBoidsQuery, mergeCellsJobHandle);

            var postSteerBoidJob = new PostSteerBoidJob
            {
                DeltaTime = deltaTime,
                ElapsedTime = elapsedTime,
                BendGain = BEND_GAIN,
                AngularVelocityDeadzone = BEND_ANGVEL_DEADZONE,
                MaxBendAbs = BEND_MAX_ABS,
                VectorTransitionSpeed = BEND_SLEW_RATE,
                SchoolRuntimeData = schoolRuntimeData,
                SchoolIndexToRuntimeIndex = schoolIndexToRuntimeIndex
            };
            JobHandle postSteerJobHandle = postSteerBoidJob.ScheduleParallel(openWaterBoidsQuery, finalTransformJobHandle);
            openWaterBoidsQuery.AddDependency(postSteerJobHandle);
            return postSteerJobHandle;
        }

        private JobHandle ScheduleSeabedPass(
            ref SystemState state,
            WorldUnmanaged world,
            float deltaTime,
            float elapsedTime,
            NativeArray<float3> obstaclePositions,
            NativeArray<float3> obstacleSizes,
            NativeArray<float3> predatorPositions,
            NativeArray<float> predatorSizes,
            ComponentLookup<BoidSchoolRuntimeData> schoolRuntimeLookup,
            NativeArray<BoidSchoolRuntimeData> schoolRuntimeData,
            NativeParallelHashMap<int, int> schoolIndexToRuntimeIndex,
            NativeArray<float3> targetPositions,
            SeabedSurfaceData seabedSurfaceData,
            HoverSpatialIndexWriter hoverIndexWriter,
            JobHandle dependency)
        {
            int boidCount = seabedBoidsQuery.CalculateEntityCount();
            var hashMap = new NativeParallelMultiHashMap<int, int>(boidCount, world.UpdateAllocator.ToAllocator);
            var cellIndices = CollectionHelper.CreateNativeArray<int, RewindableAllocator>(boidCount, ref world.UpdateAllocator);
            var cellCount = CollectionHelper.CreateNativeArray<int, RewindableAllocator>(boidCount, ref world.UpdateAllocator);
            var cellObstaclePositionIndex = CollectionHelper.CreateNativeArray<int, RewindableAllocator>(boidCount, ref world.UpdateAllocator);
            var cellObstacleDistance = CollectionHelper.CreateNativeArray<float, RewindableAllocator>(boidCount, ref world.UpdateAllocator);
            var cellPredatorPositionIndex = CollectionHelper.CreateNativeArray<int, RewindableAllocator>(boidCount, ref world.UpdateAllocator);
            var cellPredatorDistance = CollectionHelper.CreateNativeArray<float, RewindableAllocator>(boidCount, ref world.UpdateAllocator);
            var cellAlignment = CollectionHelper.CreateNativeArray<float3, RewindableAllocator>(boidCount, ref world.UpdateAllocator);
            var cellSeparation = CollectionHelper.CreateNativeArray<float3, RewindableAllocator>(boidCount, ref world.UpdateAllocator);

            var boidChunkBaseEntityIndexArray = seabedBoidsQuery.CalculateBaseEntityIndexArrayAsync(
                world.UpdateAllocator.ToAllocator,
                state.Dependency,
                out var boidChunkBaseIndexJobHandle);

            var initialBoidJob = new InitialPerBoidJob
            {
                ChunkBaseEntityIndices = boidChunkBaseEntityIndexArray,
                CellAlignment = cellAlignment,
                CellSeparation = cellSeparation,
                ParallelHashMap = hashMap.AsParallelWriter(),
                SchoolRuntimeLookup = schoolRuntimeLookup,
                HoverGroupLookup = SystemAPI.GetComponentLookup<EntityHoverGroup>(true),
                HoverIndexWriter = hoverIndexWriter
            };
            var initialBoidJobHandle = initialBoidJob.ScheduleParallel(seabedBoidsQuery, boidChunkBaseIndexJobHandle);

            var initialCellCountJob = new MemsetNativeArray<int>
            {
                Source = cellCount,
                Value = 1
            };
            var initialCellCountJobHandle = initialCellCountJob.Schedule(boidCount, 64, state.Dependency);

            var initialCellBarrierJobHandle = JobHandle.CombineDependencies(initialBoidJobHandle, initialCellCountJobHandle);
            var mergeCellsBarrierJobHandle = JobHandle.CombineDependencies(initialCellBarrierJobHandle, dependency);

            var mergeCellsJob = new MergeCells
            {
                CellIndices = cellIndices,
                CellAlignment = cellAlignment,
                CellSeparation = cellSeparation,
                ObstaclePositions = obstaclePositions,
                CellObstacleDistance = cellObstacleDistance,
                CellObstaclePositionIndex = cellObstaclePositionIndex,
                PredatorPositions = predatorPositions,
                CellPredatorDistance = cellPredatorDistance,
                CellPredatorPositionIndex = cellPredatorPositionIndex,
                CellCount = cellCount
            };
            var mergeCellsJobHandle = mergeCellsJob.Schedule(hashMap, 64, mergeCellsBarrierJobHandle);

            var steerSeabedBoidJob = new SteerSeabedBoidJob
            {
                ChunkBaseEntityIndices = boidChunkBaseEntityIndexArray,
                CellIndices = cellIndices,
                CellCount = cellCount,
                CellAlignment = cellAlignment,
                CellSeparation = cellSeparation,
                ObstaclePositions = obstaclePositions,
                CellObstacleDistance = cellObstacleDistance,
                CellObstaclePositionIndex = cellObstaclePositionIndex,
                ObstacleDimensions = obstacleSizes,
                PredatorPositions = predatorPositions,
                PredatorSizes = predatorSizes,
                CellPredatorDistance = cellPredatorDistance,
                CellPredatorPositionIndex = cellPredatorPositionIndex,
                SchoolRuntimeData = schoolRuntimeData,
                SchoolIndexToRuntimeIndex = schoolIndexToRuntimeIndex,
                TargetPositions = targetPositions,
                SeabedSurface = seabedSurfaceData,
                DeltaTime = deltaTime,
                ElapsedTime = elapsedTime
            };
            JobHandle finalTransformJobHandle = steerSeabedBoidJob.ScheduleParallel(seabedBoidsQuery, mergeCellsJobHandle);

            var postSteerBoidJob = new PostSteerBoidJob
            {
                DeltaTime = deltaTime,
                ElapsedTime = elapsedTime,
                BendGain = BEND_GAIN,
                AngularVelocityDeadzone = BEND_ANGVEL_DEADZONE,
                MaxBendAbs = BEND_MAX_ABS,
                VectorTransitionSpeed = BEND_SLEW_RATE,
                SchoolRuntimeData = schoolRuntimeData,
                SchoolIndexToRuntimeIndex = schoolIndexToRuntimeIndex
            };
            JobHandle postSteerJobHandle = postSteerBoidJob.ScheduleParallel(seabedBoidsQuery, finalTransformJobHandle);
            seabedBoidsQuery.AddDependency(postSteerJobHandle);
            return postSteerJobHandle;
        }

        private static float HashToUnitFloat(uint hash)
        {
            uint mantissa = hash & 0x00FFFFFFu;
            return mantissa * (1.0f / 16777215.0f);
        }

        private static bool TryGetSchoolRuntime(int schoolIndex, NativeParallelHashMap<int, int> schoolIndexToRuntimeIndex, NativeArray<BoidSchoolRuntimeData> schoolRuntimeData, NativeArray<float3> targetPositions, out BoidSchoolRuntimeData runtimeData, out float3 targetPosition)
        {
            runtimeData = default;
            targetPosition = float3.zero;
            if (schoolIndexToRuntimeIndex.TryGetValue(schoolIndex, out int runtimeIndex) == false)
            {
                return false;
            }

            runtimeData = schoolRuntimeData[runtimeIndex];
            if (targetPositions.IsCreated && runtimeIndex < targetPositions.Length)
            {
                targetPosition = targetPositions[runtimeIndex];
            }
            return true;
        }

        private static float3 GetPerBoidTargetOffset(Entity entity, in BoidSchoolRuntimeData runtimeData, float elapsedTime, bool seabedBound)
        {
            uint phaseXHash = math.hash(new uint4((uint)runtimeData.DynamicEntityId, (uint)runtimeData.BoidSchoolId, (uint)entity.Index, 11u));
            uint phaseYHash = math.hash(new uint4((uint)runtimeData.DynamicEntityId, (uint)runtimeData.BoidSchoolId, (uint)entity.Index, 23u));
            uint phaseZHash = math.hash(new uint4((uint)runtimeData.DynamicEntityId, (uint)runtimeData.BoidSchoolId, (uint)entity.Index, 37u));
            uint freqXHash = math.hash(new uint4((uint)runtimeData.DynamicEntityId, (uint)runtimeData.BoidSchoolId, (uint)entity.Index, 101u));
            uint freqYHash = math.hash(new uint4((uint)runtimeData.DynamicEntityId, (uint)runtimeData.BoidSchoolId, (uint)entity.Index, 211u));
            uint freqZHash = math.hash(new uint4((uint)runtimeData.DynamicEntityId, (uint)runtimeData.BoidSchoolId, (uint)entity.Index, 307u));

            float phaseX = HashToUnitFloat(phaseXHash) * (2.0f * math.PI);
            float phaseY = HashToUnitFloat(phaseYHash) * (2.0f * math.PI);
            float phaseZ = HashToUnitFloat(phaseZHash) * (2.0f * math.PI);

            float freqX = 0.85f * math.lerp(0.71f, 1.39f, HashToUnitFloat(freqXHash));
            float freqY = 0.85f * math.lerp(0.77f, 1.43f, HashToUnitFloat(freqYHash));
            float freqZ = 0.85f * math.lerp(0.83f, 1.49f, HashToUnitFloat(freqZHash));

            float3 offsetDirection = new float3(
                math.sin(elapsedTime * freqX + phaseX),
                math.sin(elapsedTime * freqY + phaseY),
                math.sin(elapsedTime * freqZ + phaseZ));
            offsetDirection = math.normalizesafe(offsetDirection, new float3(1.0f, 0.0f, 0.0f));

            if (seabedBound)
            {
                offsetDirection.y = 0.0f;
                offsetDirection = math.normalizesafe(offsetDirection, new float3(1.0f, 0.0f, 0.0f));
                return offsetDirection * 5.75f;
            }

            return offsetDirection * 7.5f;
        }

        [BurstCompile]
        partial struct InitialPerBoidJob : IJobEntity
        {
            [ReadOnly] public NativeArray<int> ChunkBaseEntityIndices;
            [NativeDisableParallelForRestriction] public NativeArray<float3> CellAlignment;
            [NativeDisableParallelForRestriction] public NativeArray<float3> CellSeparation;
            public NativeParallelMultiHashMap<int, int>.ParallelWriter ParallelHashMap;
            [ReadOnly] public ComponentLookup<BoidSchoolRuntimeData> SchoolRuntimeLookup;
            [ReadOnly] public ComponentLookup<EntityHoverGroup> HoverGroupLookup;
            public HoverSpatialIndexWriter HoverIndexWriter;

            void Execute(
                [ChunkIndexInQuery] int chunkIndexInQuery,
                [EntityIndexInChunk] int entityIndexInChunk,
                Entity entity,
                in LocalToWorld localToWorld,
                in BoidSchoolMember boidSchoolMember)
            {
                int entityIndexInQuery = ChunkBaseEntityIndices[chunkIndexInQuery] + entityIndexInChunk;
                CellAlignment[entityIndexInQuery] = localToWorld.Forward;
                CellSeparation[entityIndexInQuery] = localToWorld.Position;

                BoidSchoolRuntimeData runtimeData = SchoolRuntimeLookup[boidSchoolMember.SchoolEntity];
                float inverseBoidCellRadius = 1.0f / math.max(0.001f, runtimeData.CellRadius);
                int3 cell = (int3)math.floor(localToWorld.Position * inverseBoidCellRadius);
                int hash = (int)math.hash(new int4(boidSchoolMember.SchoolIndex, cell.x, cell.y, cell.z));
                ParallelHashMap.Add(hash, entityIndexInQuery);

                if (HoverIndexWriter.Enabled &&
                    HoverGroupLookup.HasComponent(boidSchoolMember.SchoolEntity))
                {
                    HoverIndexWriter.Add(
                        entity,
                        localToWorld,
                        HoverGroupLookup[boidSchoolMember.SchoolEntity]);
                }
            }
        }

        [BurstCompile]
        partial struct InitialPerObstacleJob : IJobEntity
        {
            [ReadOnly] public NativeArray<int> ChunkBaseEntityIndices;
            [NativeDisableParallelForRestriction] public NativeArray<float3> ObstaclePositions;
            [NativeDisableParallelForRestriction] public NativeArray<float3> ObstacleSizes;

            void Execute([ChunkIndexInQuery] int chunkIndexInQuery, [EntityIndexInChunk] int entityIndexInChunk, in LocalToWorld localToWorld, in BoidObstacle obstacle)
            {
                int entityIndexInQuery = ChunkBaseEntityIndices[chunkIndexInQuery] + entityIndexInChunk;
                ObstaclePositions[entityIndexInQuery] = localToWorld.Position;
                ObstacleSizes[entityIndexInQuery] = obstacle.Dimensions;
            }
        }

        [BurstCompile]
        partial struct InitialPerPredatorJob : IJobEntity
        {
            [ReadOnly] public NativeArray<int> ChunkBaseEntityIndices;
            [NativeDisableParallelForRestriction] public NativeArray<float3> PredatorPositions;
            [NativeDisableParallelForRestriction] public NativeArray<float> PredatorSizes;
            [ReadOnly] public ComponentLookup<BoidSchoolRuntimeData> SchoolRuntimeLookup;

            void Execute([ChunkIndexInQuery] int chunkIndexInQuery, [EntityIndexInChunk] int entityIndexInChunk, in LocalToWorld localToWorld, in BoidPredator predator, in BoidSchoolMember boidSchoolMember)
            {
                int entityIndexInQuery = ChunkBaseEntityIndices[chunkIndexInQuery] + entityIndexInChunk;
                PredatorPositions[entityIndexInQuery] = localToWorld.Position;
                BoidSchoolRuntimeData runtimeData = SchoolRuntimeLookup[boidSchoolMember.SchoolEntity];
                PredatorSizes[entityIndexInQuery] = runtimeData.MeshLargestDimension;
            }
        }

        [BurstCompile]
        struct MergeCells : IJobNativeParallelMultiHashMapMergedSharedKeyIndices
        {
            public NativeArray<int> CellIndices;
            public NativeArray<float3> CellAlignment;
            public NativeArray<float3> CellSeparation;
            [ReadOnly] public NativeArray<float3> ObstaclePositions;
            public NativeArray<int> CellObstaclePositionIndex;
            public NativeArray<float> CellObstacleDistance;
            [ReadOnly] public NativeArray<float3> PredatorPositions;
            public NativeArray<int> CellPredatorPositionIndex;
            public NativeArray<float> CellPredatorDistance;
            public NativeArray<int> CellCount;

            void NearestPosition(NativeArray<float3> positions, float3 position, out int nearestPositionIndex, out float nearestDistance)
            {
                nearestPositionIndex = 0;
                nearestDistance = math.lengthsq(position - positions[0]);
                for (int i = 1; i < positions.Length; i++)
                {
                    float distance = math.lengthsq(position - positions[i]);
                    bool nearest = distance < nearestDistance;
                    nearestDistance = math.select(nearestDistance, distance, nearest);
                    nearestPositionIndex = math.select(nearestPositionIndex, i, nearest);
                }

                nearestDistance = math.sqrt(nearestDistance);
            }

            public void ExecuteFirst(int index)
            {
                float3 position = CellSeparation[index] / CellCount[index];

                if (ObstaclePositions.Length > 0)
                {
                    int obstaclePositionIndex;
                    float obstacleDistance;
                    NearestPosition(ObstaclePositions, position, out obstaclePositionIndex, out obstacleDistance);
                    CellObstaclePositionIndex[index] = obstaclePositionIndex;
                    CellObstacleDistance[index] = obstacleDistance;
                }
                else
                {
                    CellObstaclePositionIndex[index] = -1;
                    CellObstacleDistance[index] = float.MaxValue;
                }

                if (PredatorPositions.Length > 0)
                {
                    int predatorPositionIndex;
                    float predatorDistance;
                    NearestPosition(PredatorPositions, position, out predatorPositionIndex, out predatorDistance);
                    CellPredatorPositionIndex[index] = predatorPositionIndex;
                    CellPredatorDistance[index] = predatorDistance;
                }
                else
                {
                    CellPredatorPositionIndex[index] = -1;
                    CellPredatorDistance[index] = float.MaxValue;
                }

                CellIndices[index] = index;
            }

            public void ExecuteNext(int cellIndex, int index)
            {
                CellCount[cellIndex] += 1;
                CellAlignment[cellIndex] += CellAlignment[index];
                CellSeparation[cellIndex] += CellSeparation[index];
                CellIndices[index] = cellIndex;
            }
        }

        [BurstCompile]
        partial struct SteerOpenWaterBoidJob : IJobEntity
        {
            [ReadOnly] public NativeArray<int> ChunkBaseEntityIndices;
            [ReadOnly] public NativeArray<int> CellIndices;
            [ReadOnly] public NativeArray<int> CellCount;
            [ReadOnly] public NativeArray<float3> CellAlignment;
            [ReadOnly] public NativeArray<float3> CellSeparation;
            [ReadOnly] public NativeArray<float3> ObstaclePositions;
            [ReadOnly] public NativeArray<float> CellObstacleDistance;
            [ReadOnly] public NativeArray<int> CellObstaclePositionIndex;
            [ReadOnly] public NativeArray<float3> ObstacleDimensions;
            [ReadOnly] public NativeArray<float3> PredatorPositions;
            [ReadOnly] public NativeArray<float> PredatorSizes;
            [ReadOnly] public NativeArray<float> CellPredatorDistance;
            [ReadOnly] public NativeArray<int> CellPredatorPositionIndex;
            [ReadOnly] public NativeArray<BoidSchoolRuntimeData> SchoolRuntimeData;
            [ReadOnly] public NativeParallelHashMap<int, int> SchoolIndexToRuntimeIndex;
            [ReadOnly] public NativeArray<float3> TargetPositions;
            [ReadOnly] public WaterCurrentSettings WaterCurrent;
            [ReadOnly] public float DeltaTime;
            [ReadOnly] public float ElapsedTime;

            private const float BoundsForce = 0.01f;

            void Execute([ChunkIndexInQuery] int chunkIndexInQuery, [EntityIndexInChunk] int entityIndexInChunk, Entity entity, ref LocalToWorld localToWorld, ref BoidUnique boidUnique, in BoidSchoolMember boidSchoolMember)
            {
                if (boidUnique.Disabled)
                {
                    return;
                }

                if (BoidSystem.TryGetSchoolRuntime(boidSchoolMember.SchoolIndex, SchoolIndexToRuntimeIndex, SchoolRuntimeData, TargetPositions, out BoidSchoolRuntimeData runtimeData, out float3 targetPosition) == false)
                {
                    return;
                }

                int entityIndexInQuery = ChunkBaseEntityIndices[chunkIndexInQuery] + entityIndexInChunk;
                float3 forward = localToWorld.Forward;
                float3 currentPosition = localToWorld.Position;
                int cellIndex = CellIndices[entityIndexInQuery];
                int neighborCount = CellCount[cellIndex];
                float3 alignment = CellAlignment[cellIndex];
                float3 separation = CellSeparation[cellIndex];
                int nearestObstaclePositionIndex = CellObstaclePositionIndex[cellIndex];
                float nearestObstacleDistance = CellObstacleDistance[cellIndex];
                float3 nearestObstaclePosition = currentPosition;
                if (nearestObstaclePositionIndex >= 0)
                {
                    nearestObstaclePosition = ObstaclePositions[nearestObstaclePositionIndex];
                }

                float3 alignmentResult = runtimeData.AlignmentWeight * math.normalizesafe((alignment / neighborCount) - forward);
                float3 separationResult = runtimeData.SeparationWeight * math.normalizesafe((currentPosition * neighborCount) - separation);
                float3 perBoidTargetOffset = BoidSystem.GetPerBoidTargetOffset(entity, runtimeData, ElapsedTime, false);
                float3 boidTargetPosition = targetPosition + perBoidTargetOffset;
                float3 targetHeading = runtimeData.TargetWeight * math.normalizesafe(boidTargetPosition - currentPosition);

                float obstacleEffectiveRadius = 0.0f;
                float3 avoidObstacleHeading = float3.zero;
                float nearestObstacleDistanceFromRadius = float.MaxValue;
                if (nearestObstaclePositionIndex >= 0)
                {
                    obstacleEffectiveRadius = runtimeData.ObstacleAversionDistance + ObstacleDimensions[nearestObstaclePositionIndex].x;
                    float3 obstacleSteering = currentPosition - nearestObstaclePosition;
                    avoidObstacleHeading = (nearestObstaclePosition + math.normalizesafe(obstacleSteering) * obstacleEffectiveRadius) - currentPosition;
                    nearestObstacleDistanceFromRadius = nearestObstacleDistance - obstacleEffectiveRadius;
                }

                bool preyInPredatorRange = false;
                bool predatorDataAvailable = false;
                float3 avoidPredatorHeading = float3.zero;
                float nearestPredatorDistance = 0.0f;
                float nearestPredatorDistanceFromRadius = 0.0f;

                if (runtimeData.Prey && PredatorPositions.Length > 0)
                {
                    int nearestPredatorPositionIndex = CellPredatorPositionIndex[cellIndex];
                    if (nearestPredatorPositionIndex >= 0)
                    {
                        nearestPredatorDistance = CellPredatorDistance[cellIndex];
                        float3 nearestPredatorPosition = PredatorPositions[nearestPredatorPositionIndex];
                        float predatorHalf = 0.0f;
                        if (PredatorSizes.Length > 0)
                        {
                            predatorHalf = PredatorSizes[nearestPredatorPositionIndex] * PREDATOR_SIZE_TO_RADIUS_FACTOR;
                        }

                        float predatorDetectionRange = runtimeData.ObstacleAversionDistance + 2.0f + predatorHalf;
                        preyInPredatorRange = nearestPredatorDistance < predatorDetectionRange;

                        float predatorEffectiveRadius = runtimeData.ObstacleAversionDistance + predatorHalf;
                        float3 predatorSteering = currentPosition - nearestPredatorPosition;
                        avoidPredatorHeading = (nearestPredatorPosition + math.normalizesafe(predatorSteering) * predatorEffectiveRadius) - currentPosition;
                        nearestPredatorDistanceFromRadius = nearestPredatorDistance - predatorEffectiveRadius;
                        predatorDataAvailable = true;
                    }
                }

                float3 normalHeading = math.normalizesafe(alignmentResult + separationResult + targetHeading);
                float obstacleBlendWidth = 0.001f;
                float tObstacle = 0.0f;
                if (nearestObstaclePositionIndex >= 0)
                {
                    obstacleBlendWidth = math.max(0.001f, obstacleEffectiveRadius * 0.5f);
                    tObstacle = math.saturate(-nearestObstacleDistanceFromRadius / obstacleBlendWidth);
                }

                float tPredator = 0.0f;
                float3 blendedAvoidHeading = normalHeading;
                if (nearestObstaclePositionIndex >= 0)
                {
                    blendedAvoidHeading = avoidObstacleHeading;
                }
                if (predatorDataAvailable)
                {
                    int nearestPredatorPositionIndex2 = CellPredatorPositionIndex[cellIndex];
                    float predatorHalf2 = 0.0f;
                    if (PredatorSizes.Length > 0 && nearestPredatorPositionIndex2 >= 0 && nearestPredatorPositionIndex2 < PredatorSizes.Length)
                    {
                        predatorHalf2 = PredatorSizes[nearestPredatorPositionIndex2] * PREDATOR_SIZE_TO_RADIUS_FACTOR;
                    }

                    float predatorEffectiveRadius = runtimeData.ObstacleAversionDistance + predatorHalf2;
                    float predatorBlendWidth = math.max(0.001f, predatorEffectiveRadius * 0.5f);
                    tPredator = math.saturate(-nearestPredatorDistanceFromRadius / predatorBlendWidth);

                    float switchBand = math.max(obstacleBlendWidth, predatorBlendWidth);
                    float switchWeight = 0.5f + (nearestObstacleDistance - nearestPredatorDistance) / (2.0f * switchBand);
                    switchWeight = math.saturate(switchWeight);
                    blendedAvoidHeading = math.normalizesafe(math.lerp(avoidObstacleHeading, avoidPredatorHeading, switchWeight));
                }

                float tAvoid = math.max(tObstacle, tPredator);
                float3 targetForward = math.normalizesafe(math.lerp(normalHeading, blendedAvoidHeading, tAvoid));
                float3 nextHeading = math.normalizesafe(forward + DeltaTime * (targetForward - forward) * runtimeData.MaxTurnRate);

                float3 horizontalHeading = new float3(nextHeading.x, 0.0f, nextHeading.z);
                float horizontalMagnitude = math.length(horizontalHeading);
                if (horizontalMagnitude > 0.0001f)
                {
                    float currentVerticalAngle = math.degrees(math.asin(nextHeading.y));
                    float maxVerticalAngle = math.max(0.0f, runtimeData.MaxVerticalAngle + boidUnique.MaxVerticalAngleOffset);
                    float clampedVerticalAngle = math.clamp(currentVerticalAngle, -maxVerticalAngle, maxVerticalAngle);
                    float3 newHeading = math.normalize(horizontalHeading) * math.cos(math.radians(clampedVerticalAngle));
                    newHeading.y = math.sin(math.radians(clampedVerticalAngle));
                    nextHeading = math.normalize(newHeading);
                }

                ApplyBoundarySteering(runtimeData, currentPosition, ref nextHeading);

                float angle = math.acos(math.clamp(math.dot(math.normalize(forward), math.normalize(nextHeading)), -1.0f, 1.0f));
                float maxAngleThisFrame = runtimeData.MaxTurnRate * DeltaTime;
                if (angle > maxAngleThisFrame)
                {
                    float3 axis = math.normalize(math.cross(forward, nextHeading));
                    quaternion maxRotation = quaternion.AxisAngle(axis, maxAngleThisFrame);
                    nextHeading = math.rotate(maxRotation, forward);
                }

                if (runtimeData.Prey)
                {
                    if (preyInPredatorRange)
                    {
                        boidUnique.TargetSpeedModifier = 3.0f;
                    }
                    else if (boidUnique.TargetSpeedModifier > runtimeData.SpeedModifierMax)
                    {
                        boidUnique.TargetSpeedModifier = runtimeData.SpeedModifierMax;
                    }
                }

                float3 localScale = localToWorld.Value.Scale();
                float3 swimVelocity = nextHeading * runtimeData.DefaultMoveSpeed * boidUnique.MoveSpeedModifier;
                float3 currentVelocity = WaterCurrentUtility.SampleHorizontalCurrentVelocity(
                    WaterCurrent,
                    currentPosition,
                    localScale,
                    ElapsedTime,
                    runtimeData.DefaultMoveSpeed,
                    runtimeData.MeshLargestDimension,
                    runtimeData.ScaleMin,
                    runtimeData.WaterCurrentInfluence,
                    runtimeData.Predator);
                float3 nextPosition = currentPosition + ((swimVelocity + currentVelocity) * DeltaTime);
                nextPosition = ApplyBoundaryPositionCorrection(runtimeData, currentPosition, nextPosition, ref nextHeading);
                localToWorld.Value = float4x4.TRS(
                    nextPosition,
                    quaternion.LookRotationSafe(nextHeading, math.up()),
                    localScale);
            }

            private static void ApplyBoundarySteering(in BoidSchoolRuntimeData runtimeData, float3 currentPosition, ref float3 nextHeading)
            {
                if (runtimeData.Boundary.Hardness <= 0.0f)
                {
                    return;
                }

                if (BoidBoundaryUtility.TryProjectInside(runtimeData.Boundary, currentPosition, out float3 projectedPosition, out float3 _, out float distanceOutside) == false)
                {
                    return;
                }

                float boundsMargin = math.max(0.001f, runtimeData.CellRadius * 2.0f);
                float tBound = math.saturate(distanceOutside / boundsMargin);
                float3 directionInside = math.normalizesafe(projectedPosition - currentPosition, runtimeData.BoundsCenter - currentPosition);
                nextHeading = math.normalizesafe(nextHeading + directionInside * (BoundsForce * tBound));
            }

            private static float3 ApplyBoundaryPositionCorrection(in BoidSchoolRuntimeData runtimeData, float3 currentPosition, float3 nextPosition, ref float3 nextHeading)
            {
                float hardness = math.saturate(runtimeData.Boundary.Hardness);
                if (hardness <= 0.0f)
                {
                    return nextPosition;
                }

                if (BoidBoundaryUtility.TryProjectInside(runtimeData.Boundary, nextPosition, out float3 projectedPosition, out float3 outwardNormal, out float _) == false)
                {
                    return nextPosition;
                }

                float correctionStrength = hardness * hardness * hardness;
                if (correctionStrength <= 0.0001f)
                {
                    return nextPosition;
                }

                float3 correctedPosition = math.lerp(nextPosition, projectedPosition, correctionStrength);
                float outwardAmount = math.dot(nextHeading, outwardNormal);
                if (outwardAmount > 0.0f)
                {
                    float3 fallbackHeading = math.normalizesafe(runtimeData.BoundsCenter - currentPosition, nextHeading);
                    nextHeading = math.normalizesafe(nextHeading - (outwardNormal * outwardAmount * correctionStrength), fallbackHeading);
                }

                return correctedPosition;
            }
        }

        [BurstCompile]
        partial struct SteerSeabedBoidJob : IJobEntity
        {
            private const float BoundsForce = 0.01f;

            [ReadOnly] public NativeArray<int> ChunkBaseEntityIndices;
            [ReadOnly] public NativeArray<int> CellIndices;
            [ReadOnly] public NativeArray<int> CellCount;
            [ReadOnly] public NativeArray<float3> CellAlignment;
            [ReadOnly] public NativeArray<float3> CellSeparation;
            [ReadOnly] public NativeArray<float3> ObstaclePositions;
            [ReadOnly] public NativeArray<float> CellObstacleDistance;
            [ReadOnly] public NativeArray<int> CellObstaclePositionIndex;
            [ReadOnly] public NativeArray<float3> ObstacleDimensions;
            [ReadOnly] public NativeArray<float3> PredatorPositions;
            [ReadOnly] public NativeArray<float> PredatorSizes;
            [ReadOnly] public NativeArray<float> CellPredatorDistance;
            [ReadOnly] public NativeArray<int> CellPredatorPositionIndex;
            [ReadOnly] public NativeArray<BoidSchoolRuntimeData> SchoolRuntimeData;
            [ReadOnly] public NativeParallelHashMap<int, int> SchoolIndexToRuntimeIndex;
            [ReadOnly] public NativeArray<float3> TargetPositions;
            [ReadOnly] public SeabedSurfaceData SeabedSurface;
            [ReadOnly] public float DeltaTime;
            [ReadOnly] public float ElapsedTime;

            void Execute([ChunkIndexInQuery] int chunkIndexInQuery, [EntityIndexInChunk] int entityIndexInChunk, Entity entity, ref LocalToWorld localToWorld, ref BoidUnique boidUnique, in BoidSchoolMember boidSchoolMember)
            {
                if (boidUnique.Disabled)
                {
                    return;
                }

                if (BoidSystem.TryGetSchoolRuntime(boidSchoolMember.SchoolIndex, SchoolIndexToRuntimeIndex, SchoolRuntimeData, TargetPositions, out BoidSchoolRuntimeData runtimeData, out float3 targetPosition) == false)
                {
                    return;
                }

                int entityIndexInQuery = ChunkBaseEntityIndices[chunkIndexInQuery] + entityIndexInChunk;
                float3 worldForward = localToWorld.Forward;
                float3 forward = new float3(worldForward.x, 0.0f, worldForward.z);
                float3 currentPosition = new float3(localToWorld.Position.x, 0.0f, localToWorld.Position.z);
                int cellIndex = CellIndices[entityIndexInQuery];
                int neighborCount = CellCount[cellIndex];
                float3 alignment = new float3(CellAlignment[cellIndex].x, 0.0f, CellAlignment[cellIndex].z);
                float3 separation = new float3(CellSeparation[cellIndex].x, 0.0f, CellSeparation[cellIndex].z);
                float nearestObstacleDistance = CellObstacleDistance[cellIndex];
                int nearestObstaclePositionIndex = CellObstaclePositionIndex[cellIndex];
                float3 nearestObstaclePosition = currentPosition;
                if (nearestObstaclePositionIndex >= 0)
                {
                    nearestObstaclePosition = new float3(ObstaclePositions[nearestObstaclePositionIndex].x, 0.0f, ObstaclePositions[nearestObstaclePositionIndex].z);
                }
                float3 nearestTargetPosition = new float3(targetPosition.x, 0.0f, targetPosition.z);

                bool preyInPredatorRange = false;
                if (runtimeData.Prey && PredatorPositions.Length > 0)
                {
                    int nearestPredatorPositionDataIndex = CellPredatorPositionIndex[cellIndex];
                    if (nearestPredatorPositionDataIndex >= 0)
                    {
                        float nearestPredatorDist = CellPredatorDistance[cellIndex];
                        float predatorHalf2D = 0.0f;
                        if (PredatorSizes.Length > 0 && nearestPredatorPositionDataIndex < PredatorSizes.Length)
                        {
                            predatorHalf2D = PredatorSizes[nearestPredatorPositionDataIndex] * PREDATOR_SIZE_TO_RADIUS_FACTOR;
                        }

                        float predatorDetectionRange = runtimeData.ObstacleAversionDistance + 15.0f + predatorHalf2D;
                        preyInPredatorRange = nearestPredatorDist < predatorDetectionRange;
                    }
                }

                float3 alignmentResult = runtimeData.AlignmentWeight * math.normalizesafe((alignment / neighborCount) - forward);
                float3 separationResult = runtimeData.SeparationWeight * math.normalizesafe((currentPosition * neighborCount) - separation);
                float3 perBoidTargetOffset = BoidSystem.GetPerBoidTargetOffset(entity, runtimeData, ElapsedTime, true);
                float3 boidTargetPosition = nearestTargetPosition + perBoidTargetOffset;
                float3 targetHeading = runtimeData.TargetWeight * math.normalizesafe(boidTargetPosition - currentPosition);
                float obstacleEffectiveRadius2D = 0.0f;
                float3 avoidObstacleHeading = float3.zero;
                float nearestObstacleDistanceFromRadius = float.MaxValue;
                if (nearestObstaclePositionIndex >= 0)
                {
                    float3 obstacleSteering = currentPosition - nearestObstaclePosition;
                    obstacleEffectiveRadius2D = runtimeData.ObstacleAversionDistance + ObstacleDimensions[nearestObstaclePositionIndex].x;
                    avoidObstacleHeading = (nearestObstaclePosition + math.normalizesafe(obstacleSteering) * obstacleEffectiveRadius2D) - currentPosition;
                    nearestObstacleDistanceFromRadius = nearestObstacleDistance - obstacleEffectiveRadius2D;
                }
                float3 normalHeading = math.normalizesafe(alignmentResult + separationResult + targetHeading);

                float t2D = 0.0f;
                if (nearestObstaclePositionIndex >= 0)
                {
                    float blendWidth2D = math.max(0.001f, obstacleEffectiveRadius2D * 0.5f);
                    t2D = math.saturate(-nearestObstacleDistanceFromRadius / blendWidth2D);
                }
                float3 targetForward = math.normalizesafe(math.lerp(normalHeading, avoidObstacleHeading, t2D), normalHeading);
                float3 nextHeading = math.normalizesafe(forward + DeltaTime * (targetForward - forward) * runtimeData.MaxTurnRate);
                ApplyBoundarySteering(runtimeData, localToWorld.Position, ref nextHeading);

                float angle = math.acos(math.clamp(math.dot(math.normalize(forward), math.normalize(nextHeading)), -1.0f, 1.0f));
                float maxAngleThisFrame = runtimeData.MaxTurnRate * DeltaTime;
                if (angle > maxAngleThisFrame)
                {
                    float3 axis = math.normalize(math.cross(forward, nextHeading));
                    quaternion maxRotation = quaternion.AxisAngle(axis, maxAngleThisFrame);
                    nextHeading = math.rotate(maxRotation, forward);
                }

                if (runtimeData.Prey)
                {
                    if (preyInPredatorRange)
                    {
                        boidUnique.TargetSpeedModifier = 3.0f;
                    }
                    else if (boidUnique.TargetSpeedModifier > runtimeData.SpeedModifierMax)
                    {
                        boidUnique.TargetSpeedModifier = runtimeData.SpeedModifierMax;
                    }
                }

                float3 localScale = localToWorld.Value.Scale();
                float3 swimVelocity = nextHeading * runtimeData.DefaultMoveSpeed * boidUnique.MoveSpeedModifier;
                float3 nextSamplePosition = new float3(
                    localToWorld.Position.x + (swimVelocity.x * DeltaTime),
                    localToWorld.Position.y,
                    localToWorld.Position.z + (swimVelocity.z * DeltaTime));
                nextSamplePosition = ApplyBoundaryPositionCorrection(runtimeData, localToWorld.Position, nextSamplePosition, ref nextHeading);
                SeabedSurfaceUtility.SampleSurface(SeabedSurface, nextSamplePosition, out float3 snappedPosition, out float3 surfaceNormal);
                quaternion surfaceRotation = SeabedSurfaceUtility.AlignForwardToSurface(
                    new float3(nextHeading.x, 0.0f, nextHeading.z),
                    surfaceNormal,
                    localToWorld.Forward);

                localToWorld.Value = float4x4.TRS(
                    snappedPosition,
                    surfaceRotation,
                    localScale);
            }

            private static void ApplyBoundarySteering(in BoidSchoolRuntimeData runtimeData, float3 currentPosition, ref float3 nextHeading)
            {
                if (runtimeData.Boundary.Hardness <= 0.0f)
                {
                    return;
                }

                if (BoidBoundaryUtility.TryProjectInside(runtimeData.Boundary, currentPosition, out float3 projectedPosition, out float3 _, out float distanceOutside) == false)
                {
                    return;
                }

                float boundsMargin = math.max(0.001f, runtimeData.CellRadius * 2.0f);
                float tBound = math.saturate(distanceOutside / boundsMargin);
                float3 directionInside = projectedPosition - currentPosition;
                directionInside.y = 0.0f;
                directionInside = math.normalizesafe(directionInside, new float3(runtimeData.BoundsCenter.x - currentPosition.x, 0.0f, runtimeData.BoundsCenter.z - currentPosition.z));
                nextHeading = math.normalizesafe(nextHeading + directionInside * (BoundsForce * tBound));
            }

            private static float3 ApplyBoundaryPositionCorrection(in BoidSchoolRuntimeData runtimeData, float3 currentPosition, float3 nextPosition, ref float3 nextHeading)
            {
                float hardness = math.saturate(runtimeData.Boundary.Hardness);
                if (hardness <= 0.0f)
                {
                    return nextPosition;
                }

                if (BoidBoundaryUtility.TryProjectInside(runtimeData.Boundary, nextPosition, out float3 projectedPosition, out float3 outwardNormal, out float _) == false)
                {
                    return nextPosition;
                }

                float correctionStrength = hardness * hardness * hardness;
                if (correctionStrength <= 0.0001f)
                {
                    return nextPosition;
                }

                float3 correctedPosition = math.lerp(nextPosition, projectedPosition, correctionStrength);
                outwardNormal.y = 0.0f;
                outwardNormal = math.normalizesafe(outwardNormal);
                float outwardAmount = math.dot(nextHeading, outwardNormal);
                if (outwardAmount > 0.0f)
                {
                    float3 fallbackHeading = math.normalizesafe(new float3(runtimeData.BoundsCenter.x - currentPosition.x, 0.0f, runtimeData.BoundsCenter.z - currentPosition.z), nextHeading);
                    nextHeading = math.normalizesafe(nextHeading - (outwardNormal * outwardAmount * correctionStrength), fallbackHeading);
                }

                return correctedPosition;
            }
        }

        [BurstCompile]
        partial struct PostSteerBoidJob : IJobEntity
        {
            public float DeltaTime;
            public float ElapsedTime;
            public float BendGain;
            public float AngularVelocityDeadzone;
            public float MaxBendAbs;
            public float VectorTransitionSpeed;
            [ReadOnly] public NativeArray<BoidSchoolRuntimeData> SchoolRuntimeData;
            [ReadOnly] public NativeParallelHashMap<int, int> SchoolIndexToRuntimeIndex;

            private static float HashToUnitFloat(uint hash)
            {
                uint mantissa = hash & 0x00FFFFFFu;
                return mantissa * (1.0f / 16777215.0f);
            }

            void Execute(
                Entity entity,
                ref BoidUnique boidUnique,
                in BoidSchoolMember boidSchoolMember,
                in LocalToWorld localToWorld,
                ref AccumulatedTimeOverride accumulatedTimeOverride,
                ref AnimationSpeedOverride animationSpeedOverride,
                ref CurrentVectorOverride currentVector)
            {
                if (BoidSystem.TryGetSchoolRuntime(boidSchoolMember.SchoolIndex, SchoolIndexToRuntimeIndex, SchoolRuntimeData, default, out BoidSchoolRuntimeData runtimeData, out float3 _) == false)
                {
                    return;
                }

                UpdateTargetVector(ref boidUnique, localToWorld);
                UpdateAnimationTime(ref accumulatedTimeOverride, ref animationSpeedOverride, runtimeData, boidUnique);
                SmoothSpeed(entity, ref boidUnique, runtimeData);
                SmoothVector(ref currentVector, boidUnique);
            }

            private void UpdateTargetVector(ref BoidUnique boidUnique, in LocalToWorld localToWorld)
            {
                float3 currentForward = math.normalizesafe(localToWorld.Forward);
                float3 previousForward = math.normalizesafe(boidUnique.PreviousHeading);
                float3 up = new float3(0, 1, 0);
                float3 currentForwardHorizontal = math.normalizesafe(new float3(currentForward.x, 0, currentForward.z));
                float3 previousForwardHorizontal = math.normalizesafe(new float3(previousForward.x, 0, previousForward.z));
                float deltaAngle = SignedAngleBetween(previousForwardHorizontal, currentForwardHorizontal, up);
                float angularVelocity = 0.0f;
                if (DeltaTime > 0.0f)
                {
                    angularVelocity = deltaAngle / DeltaTime;
                }

                if (AngularVelocityDeadzone > 0.0f)
                {
                    float absW = math.abs(angularVelocity);
                    if (absW <= AngularVelocityDeadzone)
                    {
                        angularVelocity = 0.0f;
                    }
                    else
                    {
                        float sign = math.sign(angularVelocity);
                        angularVelocity = sign * (absW - AngularVelocityDeadzone);
                    }
                }

                float bend = angularVelocity * BendGain;
                bend = math.clamp(bend, -MaxBendAbs, MaxBendAbs);
                boidUnique.TargetVector = new float3(bend, 0, 0);
                boidUnique.PreviousHeading = currentForward;
            }

            private static float SignedAngleBetween(float3 from, float3 to, float3 axis)
            {
                float unsignedAngle = math.acos(math.clamp(math.dot(from, to), -1.0f, 1.0f));
                float3 crossProduct = math.cross(from, to);
                float sign = math.sign(math.dot(crossProduct, axis));
                return unsignedAngle * sign;
            }

            private void UpdateAnimationTime(ref AccumulatedTimeOverride accumulatedTimeOverride, ref AnimationSpeedOverride animationSpeedOverride, in BoidSchoolRuntimeData runtimeData, in BoidUnique boidUnique)
            {
                accumulatedTimeOverride.Value += DeltaTime * runtimeData.DefaultAnimationSpeed * boidUnique.MoveSpeedModifier;
                animationSpeedOverride.Value = runtimeData.DefaultAnimationSpeed * boidUnique.MoveSpeedModifier;
                if (accumulatedTimeOverride.Value >= 1000000.0f)
                {
                    accumulatedTimeOverride.Value -= 1000000.0f;
                }
            }

            private void SmoothSpeed(Entity entity, ref BoidUnique boidUnique, in BoidSchoolRuntimeData runtimeData)
            {
                float targetSpeed = boidUnique.TargetSpeedModifier;
                if (runtimeData.SpeedJitterAmplitude > 0.0f && runtimeData.SpeedJitterFrequency > 0.0f)
                {
                    if (targetSpeed <= runtimeData.SpeedModifierMax)
                    {
                        uint phaseHash = math.hash(new uint4((uint)runtimeData.DynamicEntityId, (uint)runtimeData.BoidSchoolId, (uint)entity.Index, 17u));
                        float phase = HashToUnitFloat(phaseHash) * (2.0f * math.PI);
                        float oscillation = math.sin(phase + ElapsedTime * runtimeData.SpeedJitterFrequency);
                        float scale = 1.0f + runtimeData.SpeedJitterAmplitude * oscillation;
                        targetSpeed *= scale;
                        targetSpeed = math.clamp(targetSpeed, runtimeData.SpeedModifierMin, runtimeData.SpeedModifierMax);
                    }
                }

                float k = math.max(0.0001f, runtimeData.StateTransitionSpeed);
                float alpha = 1.0f - math.exp(-k * DeltaTime);
                boidUnique.MoveSpeedModifier = math.lerp(boidUnique.MoveSpeedModifier, targetSpeed, alpha);
            }

            private void SmoothVector(ref CurrentVectorOverride currentVector, in BoidUnique boidUnique)
            {
                float xTarget = math.clamp(boidUnique.TargetVector.x, -BEND_MAX_ABS, BEND_MAX_ABS);
                float dynamicTransitionSpeed = VectorTransitionSpeed;
                if (math.abs(xTarget) <= BEND_ZERO_EPSILON)
                {
                    xTarget = 0.0f;
                    dynamicTransitionSpeed = VectorTransitionSpeed * BEND_RETURN_SPEED_MULTIPLIER;
                }

                float3 target = new float3(xTarget, 0.0f, 0.0f);
                float3 diff = target - currentVector.Value;
                float maxStep = dynamicTransitionSpeed * DeltaTime;
                float dist = math.length(diff);
                if (dist <= maxStep)
                {
                    currentVector.Value = target;
                }
                else
                {
                    float3 dir = diff / dist;
                    currentVector.Value += dir * maxStep;
                }
            }
        }
    }

    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial struct BoidLODSystem : ISystem
    {
        private const float DEFAULT_LOD1_DISTANCE = LODDebugSettings.DefaultLOD1Distance;
        private const float DEFAULT_LOD2_DISTANCE = LODDebugSettings.DefaultLOD2Distance;

        private EntityQuery sceneDataQuery;
        private EntityQuery lodDebugSettingsQuery;
        private EntityQuery missingLODStateQuery;

        [BurstCompile]
        private partial struct LODJob : IJobEntity
        {
            [ReadOnly] public float3 CameraPosition;
            [ReadOnly] public int ForcedLOD;
            [ReadOnly] public float LOD1DistanceSq;
            [ReadOnly] public float LOD2DistanceSq;
            [ReadOnly] public ComponentLookup<BoidSchoolRuntimeData> SchoolRuntimeLookup;

            void Execute(in BoidSchoolMember boidSchoolMember, in LocalToWorld localToWorld, ref MaterialMeshInfo materialMeshInfo, ref LODState lodState)
            {
                BoidSchoolRuntimeData runtimeData = SchoolRuntimeLookup[boidSchoolMember.SchoolEntity];
                if (runtimeData.NumberOfLODs <= 0)
                {
                    return;
                }

                int lodLevel = 0;
                if (ForcedLOD >= 0)
                {
                    lodLevel = ForcedLOD;
                }
                else
                {
                    float distanceToCameraSq = math.distancesq(localToWorld.Position, CameraPosition);
                    if (runtimeData.NumberOfLODs > 2 && distanceToCameraSq > LOD2DistanceSq)
                    {
                        lodLevel = 2;
                    }
                    else if (runtimeData.NumberOfLODs > 1 && distanceToCameraSq > LOD1DistanceSq)
                    {
                        lodLevel = 1;
                    }
                }

                lodLevel = math.clamp(lodLevel, 0, runtimeData.NumberOfLODs - 1);
                if (lodState.CurrentLOD == lodLevel)
                {
                    return;
                }

                lodState.CurrentLOD = lodLevel;
                materialMeshInfo = MaterialMeshInfo.FromRenderMeshArrayIndices(0, lodLevel);
            }
        }

        public void OnCreate(ref SystemState state)
        {
            sceneDataQuery = state.EntityManager.CreateEntityQuery(typeof(SceneData));
            lodDebugSettingsQuery = state.EntityManager.CreateEntityQuery(typeof(LODDebugSettings));
            missingLODStateQuery = SystemAPI.QueryBuilder()
                .WithAll<BoidSchoolMember, LocalToWorld, MaterialMeshInfo>()
                .WithNone<LODState>()
                .Build();

            if (lodDebugSettingsQuery.CalculateEntityCount() == 0)
            {
                Entity lodDebugSettingsEntity = state.EntityManager.CreateEntity(typeof(LODDebugSettings));
                state.EntityManager.SetName(lodDebugSettingsEntity, "LOD Debug Settings");
                state.EntityManager.SetComponentData(lodDebugSettingsEntity, LODDebugSettings.CreateDefault());
            }

            state.RequireForUpdate(sceneDataQuery);
            state.RequireForUpdate(lodDebugSettingsQuery);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            int missingLODStateCount = missingLODStateQuery.CalculateEntityCount();
            if (missingLODStateCount > 0)
            {
                state.EntityManager.AddComponent<LODState>(missingLODStateQuery);
            }

            SceneData sceneData = sceneDataQuery.GetSingleton<SceneData>();
            LODDebugSettings lodDebugSettings = lodDebugSettingsQuery.GetSingleton<LODDebugSettings>();
            int forcedLOD = lodDebugSettings.ForcedLOD;
            float lod1Distance = DEFAULT_LOD1_DISTANCE;
            float lod2Distance = DEFAULT_LOD2_DISTANCE;

            if (lodDebugSettings.DebugOverridesEnabled)
            {
                lod1Distance = lodDebugSettings.LOD1Distance;
                lod2Distance = lodDebugSettings.LOD2Distance;
            }

            if (forcedLOD < LODDebugSettings.AutoLOD)
            {
                forcedLOD = LODDebugSettings.AutoLOD;
            }
            if (lod1Distance < 0.0f)
            {
                lod1Distance = 0.0f;
            }
            if (lod2Distance < lod1Distance)
            {
                lod2Distance = lod1Distance;
            }

            var job = new LODJob
            {
                CameraPosition = sceneData.CameraPosition,
                ForcedLOD = forcedLOD,
                LOD1DistanceSq = lod1Distance * lod1Distance,
                LOD2DistanceSq = lod2Distance * lod2Distance,
                SchoolRuntimeLookup = SystemAPI.GetComponentLookup<BoidSchoolRuntimeData>(true)
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }
    }
}
