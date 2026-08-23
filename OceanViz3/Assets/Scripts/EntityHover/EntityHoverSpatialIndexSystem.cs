using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine.Assertions;

namespace OceanViz3
{
    /// <summary>
    /// Size-bucketed spatial hashing used by entity mouse-over.
    /// Dynamic traversal bounds are maintained here while BoidSystem owns its dynamic candidate index.
    /// The retained static path is rebuilt only after static population or placement changes
    /// and only when a request explicitly includes static entities.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BoidSystem))]
    [UpdateAfter(typeof(StaticEntitySpawnSystem))]
    public partial class EntityHoverSpatialIndexSystem : SystemBase
    {
        private const int InitialCapacity = 1024;
        private const int MaximumStaticCellsPerEntity = 8;

        private NativeParallelMultiHashMap<int, Entity> staticIndex;
        private NativeParallelMultiHashMap<int, Entity> staticPreciseIndex;
        private NativeList<EntityHoverTraversalBounds> dynamicTraversalBounds;
        private NativeList<EntityHoverTraversalBounds> staticTraversalBounds;
        private EntityQuery staticEntitiesQuery;
        private EntityQuery staticGroupsQuery;
        private JobHandle activeReadHandle;
        private int indexedStaticCount = -1;
        private bool staticRebuildRequested = true;

        public NativeParallelMultiHashMap<int, Entity> StaticIndex => staticIndex;
        public NativeParallelMultiHashMap<int, Entity> StaticPreciseIndex => staticPreciseIndex;
        public NativeArray<EntityHoverTraversalBounds> DynamicTraversalBounds => dynamicTraversalBounds.AsArray();
        public NativeArray<EntityHoverTraversalBounds> StaticTraversalBounds => staticTraversalBounds.AsArray();

        protected override void OnCreate()
        {
            staticIndex = new NativeParallelMultiHashMap<int, Entity>(InitialCapacity, Allocator.Persistent);
            staticPreciseIndex = new NativeParallelMultiHashMap<int, Entity>(InitialCapacity, Allocator.Persistent);
            dynamicTraversalBounds = new NativeList<EntityHoverTraversalBounds>(32, Allocator.Persistent);
            staticTraversalBounds = new NativeList<EntityHoverTraversalBounds>(32, Allocator.Persistent);

            staticEntitiesQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<StaticEntityShared>(),
                    ComponentType.ReadOnly<StaticEntityHoverMember>(),
                    ComponentType.ReadOnly<LocalToWorld>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Prefab>()
                },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            staticGroupsQuery = GetEntityQuery(ComponentType.ReadOnly<StaticEntitiesGroupComponent>());

            if (!SystemAPI.HasSingleton<EntityHoverRequest>())
            {
                Entity requestEntity = EntityManager.CreateEntity(typeof(EntityHoverRequest));
                EntityManager.SetName(requestEntity, "Entity Hover Request");
            }

            if (!SystemAPI.HasSingleton<EntityHoverResult>())
            {
                Entity resultEntity = EntityManager.CreateEntity(typeof(EntityHoverResult));
                EntityManager.SetName(resultEntity, "Entity Hover Result");
            }
        }

        protected override void OnUpdate()
        {
            EntityHoverRequest request = SystemAPI.GetSingleton<EntityHoverRequest>();
            if (!request.Active)
            {
                return;
            }

            if (!TryFinishActiveRead())
            {
                return;
            }

            RefreshDynamicTraversalBounds();
            if (!request.IncludeStaticEntities)
            {
                return;
            }

            int staticCount = staticEntitiesQuery.CalculateEntityCount();
            if (staticCount != indexedStaticCount)
            {
                staticRebuildRequested = true;
            }

            if (staticRebuildRequested)
            {
                if (!StaticStreamingIsStable())
                {
                    return;
                }

                RebuildStaticIndex(staticCount);
                indexedStaticCount = staticCount;
                staticRebuildRequested = false;
            }
        }

        private bool StaticStreamingIsStable()
        {
            using NativeArray<StaticEntitiesGroupComponent> groups =
                staticGroupsQuery.ToComponentDataArray<StaticEntitiesGroupComponent>(Allocator.Temp);
            for (int i = 0; i < groups.Length; i++)
            {
                StaticEntitiesGroupComponent group = groups[i];
                if (group.GeneratedCount != group.RequestedCount || group.StreamingRefreshRequested)
                {
                    return false;
                }
            }

            return true;
        }

        public void RegisterRead(JobHandle readHandle)
        {
            activeReadHandle = JobHandle.CombineDependencies(activeReadHandle, readHandle);
        }

        public void RequestStaticRebuild()
        {
            staticRebuildRequested = true;
        }

        private bool TryFinishActiveRead()
        {
            if (!activeReadHandle.IsCompleted)
            {
                return false;
            }

            activeReadHandle.Complete();
            activeReadHandle = default;
            return true;
        }

        private void RebuildStaticIndex(int entityCount)
        {
            using NativeArray<Entity> entities = staticEntitiesQuery.ToEntityArray(Allocator.Temp);
            using NativeArray<StaticEntityHoverMember> members =
                staticEntitiesQuery.ToComponentDataArray<StaticEntityHoverMember>(Allocator.Temp);
            using NativeArray<LocalToWorld> transforms = staticEntitiesQuery.ToComponentDataArray<LocalToWorld>(Allocator.Temp);

            Assert.IsTrue(
                entityCount <= int.MaxValue / MaximumStaticCellsPerEntity,
                "[EntityHoverSpatialIndexSystem] Precise static hover index capacity overflow.");
            EnsureCapacity(ref staticPreciseIndex, entityCount * MaximumStaticCellsPerEntity);
            staticIndex.Clear();
            staticPreciseIndex.Clear();
            staticTraversalBounds.Clear();
            using NativeParallelHashSet<StaticEntityHoverCellKey> occupiedGroupCells =
                new NativeParallelHashSet<StaticEntityHoverCellKey>(
                    math.max(InitialCapacity, entityCount),
                    Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                Assert.IsTrue(
                    members[i].GroupEntity != Entity.Null &&
                    EntityManager.HasComponent<EntityHoverGroup>(members[i].GroupEntity),
                    "[EntityHoverSpatialIndexSystem] Static entity has no valid hover group.");
                EntityHoverGroup group = EntityManager.GetComponentData<EntityHoverGroup>(members[i].GroupEntity);
                CalculateStaticWorldBounds(
                    transforms[i],
                    group,
                    out float3 worldCenter,
                    out float3 worldExtents);

                AddFarStaticCells(
                    occupiedGroupCells,
                    members[i].GroupEntity,
                    worldCenter - worldExtents,
                    worldCenter + worldExtents,
                    worldExtents);

                int bucket = HoverSpatialIndexUtility.CalculateBucket(math.length(worldExtents));
                float cellSize = HoverSpatialIndexUtility.GetCellSize(bucket);
                int3 minimumCell = (int3)math.floor((worldCenter - worldExtents) / cellSize);
                int3 maximumCell = (int3)math.floor((worldCenter + worldExtents) / cellSize);
                int3 cellCount = maximumCell - minimumCell + 1;
                Assert.IsTrue(
                    cellCount.x * cellCount.y * cellCount.z <= MaximumStaticCellsPerEntity,
                    "[EntityHoverSpatialIndexSystem] Static bounds exceed their precise size bucket.");
                for (int z = minimumCell.z; z <= maximumCell.z; z++)
                {
                    for (int y = minimumCell.y; y <= maximumCell.y; y++)
                    {
                        for (int x = minimumCell.x; x <= maximumCell.x; x++)
                        {
                            int preciseKey = HoverSpatialIndexUtility.CalculateKey(bucket, new int3(x, y, z));
                            staticPreciseIndex.Add(preciseKey, entities[i]);
                        }
                    }
                }

                AddOrExpandTraversalBounds(
                    ref staticTraversalBounds,
                    members[i].GroupEntity,
                    worldCenter - worldExtents,
                    worldCenter + worldExtents);
            }

            EnsureCapacity(ref staticIndex, occupiedGroupCells.Count());
            foreach (StaticEntityHoverCellKey occupiedGroupCell in occupiedGroupCells)
            {
                staticIndex.Add(
                    occupiedGroupCell.SpatialKey,
                    occupiedGroupCell.GroupEntity);
            }
        }

        private static void AddFarStaticCells(
            NativeParallelHashSet<StaticEntityHoverCellKey> occupiedGroupCells,
            Entity groupEntity,
            float3 worldMinimum,
            float3 worldMaximum,
            float3 worldExtents)
        {
            int bucket = HoverSpatialIndexUtility.CalculateHorizontalFootprintBucket(worldExtents);
            float cellSize = HoverSpatialIndexUtility.GetCellSize(bucket);
            int3 minimumCell = (int3)math.floor(worldMinimum / cellSize);
            int3 maximumCell = (int3)math.floor(worldMaximum / cellSize);
            for (int z = minimumCell.z; z <= maximumCell.z; z++)
            {
                for (int y = minimumCell.y; y <= maximumCell.y; y++)
                {
                    for (int x = minimumCell.x; x <= maximumCell.x; x++)
                    {
                        int spatialKey = HoverSpatialIndexUtility.CalculateKey(
                            bucket,
                            new int3(x, y, z));
                        occupiedGroupCells.Add(new StaticEntityHoverCellKey
                        {
                            SpatialKey = spatialKey,
                            GroupEntity = groupEntity
                        });
                    }
                }
            }
        }

        private static void CalculateStaticWorldBounds(
            in LocalToWorld transform,
            in EntityHoverGroup group,
            out float3 worldCenter,
            out float3 worldExtents)
        {
            worldCenter = math.transform(
                transform.Value,
                group.LocalBoundsCenter * HoverSpatialIndexUtility.MaximumViewScale);
            worldExtents = HoverSpatialIndexUtility.CalculateWorldExtents(
                transform.Value,
                group.LocalBoundsExtents * HoverSpatialIndexUtility.MaximumViewScale);
        }

        private void RefreshDynamicTraversalBounds()
        {
            dynamicTraversalBounds.Clear();
            foreach (var (runtimeData, hoverGroup, groupEntity) in
                     SystemAPI.Query<RefRO<BoidSchoolRuntimeData>, RefRO<EntityHoverGroup>>()
                         .WithEntityAccess())
            {
                float meshMargin =
                    runtimeData.ValueRO.MeshLargestDimension *
                    math.max(1.0f, runtimeData.ValueRO.ScaleMax) *
                    HoverSpatialIndexUtility.MaximumViewScale;
                float3 margin = new float3(meshMargin);
                dynamicTraversalBounds.Add(new EntityHoverTraversalBounds
                {
                    GroupEntity = groupEntity,
                    Minimum = runtimeData.ValueRO.BoundsMin - margin,
                    Maximum = runtimeData.ValueRO.BoundsMax + margin
                });
            }
        }

        private static void AddOrExpandTraversalBounds(
            ref NativeList<EntityHoverTraversalBounds> bounds,
            Entity groupEntity,
            float3 minimum,
            float3 maximum)
        {
            for (int i = 0; i < bounds.Length; i++)
            {
                EntityHoverTraversalBounds existing = bounds[i];
                if (existing.GroupEntity != groupEntity)
                {
                    continue;
                }

                existing.Minimum = math.min(existing.Minimum, minimum);
                existing.Maximum = math.max(existing.Maximum, maximum);
                bounds[i] = existing;
                return;
            }

            bounds.Add(new EntityHoverTraversalBounds
            {
                GroupEntity = groupEntity,
                Minimum = minimum,
                Maximum = maximum
            });
        }

        private static void EnsureCapacity(ref NativeParallelMultiHashMap<int, Entity> index, int required)
        {
            int targetCapacity = math.max(InitialCapacity, required);
            if (index.Capacity < targetCapacity)
            {
                index.Capacity = targetCapacity;
            }
        }

        protected override void OnDestroy()
        {
            activeReadHandle.Complete();
            if (staticIndex.IsCreated)
            {
                staticIndex.Dispose();
            }
            if (staticPreciseIndex.IsCreated)
            {
                staticPreciseIndex.Dispose();
            }
            if (dynamicTraversalBounds.IsCreated)
            {
                dynamicTraversalBounds.Dispose();
            }
            if (staticTraversalBounds.IsCreated)
            {
                staticTraversalBounds.Dispose();
            }
        }
    }

    public struct HoverSpatialIndexWriter
    {
        public bool Enabled;
        public NativeParallelMultiHashMap<int, Entity>.ParallelWriter Writer;

        public void Add(Entity entity, in LocalToWorld localToWorld, in EntityHoverGroup group)
        {
            if (!Enabled)
            {
                return;
            }

            float3 worldCenter = math.transform(
                localToWorld.Value,
                group.LocalBoundsCenter * HoverSpatialIndexUtility.MaximumViewScale);
            float worldRadius = HoverSpatialIndexUtility.CalculateWorldRadius(
                localToWorld.Value,
                group.LocalBoundsExtents * HoverSpatialIndexUtility.MaximumViewScale);
            Writer.Add(HoverSpatialIndexUtility.CalculateKey(worldCenter, worldRadius), entity);
        }
    }

    public static class HoverSpatialIndexUtility
    {
        public const float MaximumViewScale = 2.0f;
        public const int BucketCount = 5;

        public static float GetCellSize(int bucket)
        {
            if (bucket == 0)
            {
                return 2.0f;
            }
            if (bucket == 1)
            {
                return 8.0f;
            }
            if (bucket == 2)
            {
                return 32.0f;
            }
            if (bucket == 3)
            {
                return 128.0f;
            }
            return 512.0f;
        }

        public static int CalculateBucket(float radius)
        {
            if (radius <= 1.0f)
            {
                return 0;
            }
            if (radius <= 4.0f)
            {
                return 1;
            }
            if (radius <= 16.0f)
            {
                return 2;
            }
            if (radius <= 64.0f)
            {
                return 3;
            }
            return 4;
        }

        public static int CalculateHorizontalFootprintBucket(float3 worldExtents)
        {
            float horizontalRadius = math.length(new float2(worldExtents.x, worldExtents.z));
            return CalculateBucket(horizontalRadius);
        }

        public static int CalculateKey(float3 worldCenter, float radius)
        {
            int bucket = CalculateBucket(radius);
            float cellSize = GetCellSize(bucket);
            int3 cell = (int3)math.floor(worldCenter / cellSize);
            return CalculateKey(bucket, cell);
        }

        public static int CalculateKey(int bucket, int3 cell)
        {
            return (int)math.hash(new int4(cell, bucket));
        }

        public static float CalculateWorldRadius(float4x4 localToWorld, float3 localExtents)
        {
            return math.length(CalculateWorldExtents(localToWorld, localExtents));
        }

        public static float3 CalculateWorldExtents(float4x4 localToWorld, float3 localExtents)
        {
            return
                math.abs(localToWorld.c0.xyz) * localExtents.x +
                math.abs(localToWorld.c1.xyz) * localExtents.y +
                math.abs(localToWorld.c2.xyz) * localExtents.z;
        }
    }

    /// <summary>
    /// Schedules candidate-only picking and publishes the result on a later frame.
    /// It never blocks the main thread waiting for a running pick job.
    /// </summary>
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class EntityHoverPickingSystem : SystemBase
    {
        private NativeReference<EntityHoverJobResult> pendingResult;
        private JobHandle pendingHandle;
        private bool jobPending;

        protected override void OnCreate()
        {
            pendingResult = new NativeReference<EntityHoverJobResult>(Allocator.Persistent);
            RequireForUpdate<EntityHoverRequest>();
            RequireForUpdate<EntityHoverResult>();
        }

        protected override void OnUpdate()
        {
            PublishCompletedResult();

            EntityHoverRequest request = SystemAPI.GetSingleton<EntityHoverRequest>();
            if (!request.Active || jobPending)
            {
                return;
            }

            EntityHoverSpatialIndexSystem indexSystem = World.GetExistingSystemManaged<EntityHoverSpatialIndexSystem>();
            Assert.IsNotNull(indexSystem, "[EntityHoverPickingSystem] Spatial index system is required.");
            SystemHandle boidSystemHandle =
                World.Unmanaged.GetExistingUnmanagedSystem<BoidSystem>();
            Assert.AreNotEqual(
                SystemHandle.Null,
                boidSystemHandle,
                "[EntityHoverPickingSystem] BoidSystem is required.");
            ref BoidSystem boidSystem =
                ref World.Unmanaged.GetUnsafeSystemRef<BoidSystem>(boidSystemHandle);

            pendingResult.Value = new EntityHoverJobResult
            {
                Entity = Entity.Null,
                Distance = request.MaximumDistance,
                GroupId = -1,
                Kind = 0,
                RequestSequence = request.Sequence
            };

            EntityHoverPickJob pickJob = new EntityHoverPickJob
            {
                Request = request,
                DynamicIndex = boidSystem.DynamicHoverIndex,
                StaticIndex = indexSystem.StaticIndex,
                StaticPreciseIndex = indexSystem.StaticPreciseIndex,
                DynamicTraversalBounds = indexSystem.DynamicTraversalBounds,
                StaticTraversalBounds = indexSystem.StaticTraversalBounds,
                LocalToWorldLookup = GetComponentLookup<LocalToWorld>(true),
                HoverGroupLookup = GetComponentLookup<EntityHoverGroup>(true),
                BoidSchoolMemberLookup = GetComponentLookup<BoidSchoolMember>(true),
                StaticMemberLookup = GetComponentLookup<StaticEntityHoverMember>(true),
                StaticGroupLookup = GetComponentLookup<StaticEntitiesGroupComponent>(true),
                WorldRenderBoundsLookup = GetComponentLookup<WorldRenderBounds>(true),
                ScreenStartLookup = GetComponentLookup<ScreenDisplayStartOverride>(true),
                ScreenEndLookup = GetComponentLookup<ScreenDisplayEndOverride>(true),
                DisabledLookup = GetComponentLookup<Disabled>(true),
                Result = pendingResult
            };

            JobHandle readDependency = JobHandle.CombineDependencies(
                Dependency,
                boidSystem.GetDynamicHoverReadDependency());
            pendingHandle = pickJob.Schedule(readDependency);
            Dependency = pendingHandle;
            indexSystem.RegisterRead(pendingHandle);
            boidSystem.RegisterDynamicHoverRead(pendingHandle);
            jobPending = true;
        }

        private void PublishCompletedResult()
        {
            if (!jobPending || !pendingHandle.IsCompleted)
            {
                return;
            }

            pendingHandle.Complete();
            EntityHoverJobResult completed = pendingResult.Value;
            SystemAPI.SetSingleton(new EntityHoverResult
            {
                Entity = completed.Entity,
                GroupId = completed.GroupId,
                Kind = completed.Kind,
                RequestSequence = completed.RequestSequence
            });
            jobPending = false;
        }

        protected override void OnDestroy()
        {
            pendingHandle.Complete();
            if (pendingResult.IsCreated)
            {
                pendingResult.Dispose();
            }
        }
    }

    public struct EntityHoverJobResult
    {
        public Entity Entity;
        public float Distance;
        public int GroupId;
        public EntityHoverKind Kind;
        public uint RequestSequence;
    }

    [BurstCompile]
    public struct EntityHoverPickJob : IJob
    {
        private const float PreciseStaticDistance = 10.0f;

        [ReadOnly] public EntityHoverRequest Request;
        [ReadOnly] public NativeParallelMultiHashMap<int, Entity> DynamicIndex;
        [ReadOnly] public NativeParallelMultiHashMap<int, Entity> StaticIndex;
        [ReadOnly] public NativeParallelMultiHashMap<int, Entity> StaticPreciseIndex;
        [ReadOnly] public NativeArray<EntityHoverTraversalBounds> DynamicTraversalBounds;
        [ReadOnly] public NativeArray<EntityHoverTraversalBounds> StaticTraversalBounds;
        [ReadOnly] public ComponentLookup<LocalToWorld> LocalToWorldLookup;
        [ReadOnly] public ComponentLookup<EntityHoverGroup> HoverGroupLookup;
        [ReadOnly] public ComponentLookup<BoidSchoolMember> BoidSchoolMemberLookup;
        [ReadOnly] public ComponentLookup<StaticEntityHoverMember> StaticMemberLookup;
        [ReadOnly] public ComponentLookup<StaticEntitiesGroupComponent> StaticGroupLookup;
        [ReadOnly] public ComponentLookup<WorldRenderBounds> WorldRenderBoundsLookup;
        [ReadOnly] public ComponentLookup<ScreenDisplayStartOverride> ScreenStartLookup;
        [ReadOnly] public ComponentLookup<ScreenDisplayEndOverride> ScreenEndLookup;
        [ReadOnly] public ComponentLookup<Disabled> DisabledLookup;
        public NativeReference<EntityHoverJobResult> Result;

        public void Execute()
        {
            EntityHoverJobResult best = Result.Value;
            if (!TryCalculateTraversalRange(out float traversalStart, out float traversalEnd))
            {
                Result.Value = best;
                return;
            }

            for (int bucket = 0; bucket < HoverSpatialIndexUtility.BucketCount; bucket++)
            {
                TraverseBucket(bucket, traversalStart, traversalEnd, ref best);
            }
            Result.Value = best;
        }

        private void TraverseBucket(
            int bucket,
            float traversalStart,
            float traversalEnd,
            ref EntityHoverJobResult best)
        {
            float cellSize = HoverSpatialIndexUtility.GetCellSize(bucket);
            float3 traversalOrigin = Request.RayOrigin + Request.RayDirection * traversalStart;
            float traversalDistance = traversalEnd - traversalStart;
            int3 cell = (int3)math.floor(traversalOrigin / cellSize);
            int3 step = new int3(
                CalculateCellStep(Request.RayDirection.x),
                CalculateCellStep(Request.RayDirection.y),
                CalculateCellStep(Request.RayDirection.z));

            float3 nextBoundary = new float3(
                CalculateNextBoundary(cell.x, step.x, cellSize),
                CalculateNextBoundary(cell.y, step.y, cellSize),
                CalculateNextBoundary(cell.z, step.z, cellSize));

            float3 tMax = new float3(
                CalculateBoundaryDistance(traversalOrigin.x, Request.RayDirection.x, nextBoundary.x),
                CalculateBoundaryDistance(traversalOrigin.y, Request.RayDirection.y, nextBoundary.y),
                CalculateBoundaryDistance(traversalOrigin.z, Request.RayDirection.z, nextBoundary.z));
            float3 tDelta = new float3(
                CalculateCellDistance(Request.RayDirection.x, cellSize),
                CalculateCellDistance(Request.RayDirection.y, cellSize),
                CalculateCellDistance(Request.RayDirection.z, cellSize));

            float crossedCellsPerUnit =
                math.abs(Request.RayDirection.x) +
                math.abs(Request.RayDirection.y) +
                math.abs(Request.RayDirection.z);
            int maximumSteps =
                (int)math.ceil(traversalDistance / cellSize * crossedCellsPerUnit) + 3;
            float travelled = 0.0f;
            int3 previousCell = cell;
            bool hasPreviousCell = false;
            for (int cellStep = 0;
                 cellStep < maximumSteps &&
                 travelled <= traversalDistance &&
                 travelled + traversalStart <= best.Distance;
                 cellStep++)
            {
                VisitNewCells(
                    bucket,
                    cell,
                    previousCell,
                    hasPreviousCell,
                    traversalStart + travelled,
                    ref best);
                previousCell = cell;
                hasPreviousCell = true;

                if (tMax.x <= tMax.y && tMax.x <= tMax.z)
                {
                    travelled = tMax.x;
                    tMax.x += tDelta.x;
                    cell.x += step.x;
                }
                else if (tMax.y <= tMax.z)
                {
                    travelled = tMax.y;
                    tMax.y += tDelta.y;
                    cell.y += step.y;
                }
                else
                {
                    travelled = tMax.z;
                    tMax.z += tDelta.z;
                    cell.z += step.z;
                }
            }
        }

        private bool TryCalculateTraversalRange(out float start, out float end)
        {
            start = Request.MaximumDistance;
            end = 0.0f;
            bool intersectsPopulatedBounds = false;

            IncludeTraversalBounds(DynamicTraversalBounds, ref start, ref end, ref intersectsPopulatedBounds);
            if (Request.IncludeStaticEntities)
            {
                IncludeTraversalBounds(StaticTraversalBounds, ref start, ref end, ref intersectsPopulatedBounds);
            }

            start = math.max(0.0f, start);
            end = math.min(Request.MaximumDistance, end);
            return intersectsPopulatedBounds && start <= end;
        }

        private void IncludeTraversalBounds(
            in NativeArray<EntityHoverTraversalBounds> bounds,
            ref float start,
            ref float end,
            ref bool intersectsPopulatedBounds)
        {
            for (int i = 0; i < bounds.Length; i++)
            {
                if (!EntityHoverPickingMath.TryIntersectAabbRange(
                        Request.RayOrigin,
                        Request.RayDirection,
                        bounds[i].Minimum,
                        bounds[i].Maximum,
                        out float boundsStart,
                        out float boundsEnd))
                {
                    continue;
                }

                if (boundsEnd < 0.0f || boundsStart > Request.MaximumDistance)
                {
                    continue;
                }

                start = math.min(start, boundsStart);
                end = math.max(end, boundsEnd);
                intersectsPopulatedBounds = true;
            }
        }

        private void VisitNewCells(
            int bucket,
            int3 cell,
            int3 previousCell,
            bool hasPreviousCell,
            float approximateDistance,
            ref EntityHoverJobResult best)
        {
            if (Request.IncludeStaticEntities)
            {
                int staticKey = HoverSpatialIndexUtility.CalculateKey(bucket, cell);
                if (approximateDistance <= PreciseStaticDistance)
                {
                    VisitPreciseStaticCell(staticKey, ref best);
                }
                else
                {
                    VisitStaticCell(staticKey, approximateDistance, ref best);
                }
            }

            for (int z = -1; z <= 1; z++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        int3 candidateCell = cell + new int3(x, y, z);
                        if (hasPreviousCell && IsInsideNeighbourhood(candidateCell, previousCell))
                        {
                            continue;
                        }

                        int key = HoverSpatialIndexUtility.CalculateKey(bucket, candidateCell);
                        VisitDynamicCell(key, ref best);
                    }
                }
            }
        }

        private static bool IsInsideNeighbourhood(int3 cell, int3 centre)
        {
            int3 distance = math.abs(cell - centre);
            return distance.x <= 1 && distance.y <= 1 && distance.z <= 1;
        }

        private void VisitStaticCell(int key, float approximateDistance, ref EntityHoverJobResult best)
        {
            if (approximateDistance >= best.Distance ||
                !StaticIndex.TryGetFirstValue(
                    key,
                    out Entity groupEntity,
                    out NativeParallelMultiHashMapIterator<int> iterator))
            {
                return;
            }

            do
            {
                if (!HoverGroupLookup.HasComponent(groupEntity) ||
                    !StaticGroupLookup.HasComponent(groupEntity))
                {
                    continue;
                }

                StaticEntitiesGroupComponent staticGroup = StaticGroupLookup[groupEntity];
                float visibility = EntityHoverPickingMath.SelectViewValue(
                    staticGroup.ViewVisibilityPercentages,
                    Request.ViewIndex);
                if (staticGroup.DestroyRequested ||
                    staticGroup.Count <= 0 ||
                    Request.ViewIndex >= staticGroup.ViewsCount ||
                    visibility <= 0.0f)
                {
                    continue;
                }

                if (!TryGetStaticGroupIntersection(groupEntity, out float groupDistance))
                {
                    continue;
                }

                float hitDistance = math.max(approximateDistance, groupDistance);
                if (hitDistance >= best.Distance)
                {
                    continue;
                }

                EntityHoverGroup hoverGroup = HoverGroupLookup[groupEntity];
                best.Entity = groupEntity;
                best.Distance = hitDistance;
                best.GroupId = hoverGroup.GroupId;
                best.Kind = EntityHoverKind.Static;
                return;
            }
            while (StaticIndex.TryGetNextValue(out groupEntity, ref iterator));
        }

        private bool TryGetStaticGroupIntersection(Entity groupEntity, out float distance)
        {
            for (int i = 0; i < StaticTraversalBounds.Length; i++)
            {
                if (StaticTraversalBounds[i].GroupEntity != groupEntity)
                {
                    continue;
                }

                if (!EntityHoverPickingMath.TryIntersectAabbRange(
                        Request.RayOrigin,
                        Request.RayDirection,
                        StaticTraversalBounds[i].Minimum,
                        StaticTraversalBounds[i].Maximum,
                        out float nearDistance,
                        out float farDistance) ||
                    farDistance < 0.0f ||
                    nearDistance > Request.MaximumDistance)
                {
                    distance = 0.0f;
                    return false;
                }

                distance = math.max(0.0f, nearDistance);
                return true;
            }

            distance = 0.0f;
            return false;
        }

        private void VisitPreciseStaticCell(int key, ref EntityHoverJobResult best)
        {
            if (!StaticPreciseIndex.TryGetFirstValue(
                    key,
                    out Entity candidate,
                    out NativeParallelMultiHashMapIterator<int> iterator))
            {
                return;
            }

            do
            {
                if (DisabledLookup.HasComponent(candidate) ||
                    !StaticMemberLookup.HasComponent(candidate) ||
                    !WorldRenderBoundsLookup.HasComponent(candidate) ||
                    !ScreenStartLookup.HasComponent(candidate) ||
                    !ScreenEndLookup.HasComponent(candidate))
                {
                    continue;
                }

                Entity groupEntity = StaticMemberLookup[candidate].GroupEntity;
                if (groupEntity == Entity.Null || !HoverGroupLookup.HasComponent(groupEntity))
                {
                    continue;
                }

                float start = EntityHoverPickingMath.SelectViewValue(
                    ScreenStartLookup[candidate].Value,
                    Request.ViewIndex);
                float end = EntityHoverPickingMath.SelectViewValue(
                    ScreenEndLookup[candidate].Value,
                    Request.ViewIndex);
                if (end <= start ||
                    Request.NormalizedScreenX < start ||
                    Request.NormalizedScreenX > end)
                {
                    continue;
                }

                EntityHoverGroup group = HoverGroupLookup[groupEntity];
                float viewScale = EntityHoverPickingMath.SelectViewValue(
                    group.ViewScaleMultipliers,
                    Request.ViewIndex);
                AABB worldBounds = WorldRenderBoundsLookup[candidate].Value;
                float3 extents = worldBounds.Extents * math.max(1.0f, viewScale);
                if (!EntityHoverPickingMath.TryIntersectAabb(
                        Request.RayOrigin,
                        Request.RayDirection,
                        worldBounds.Center - extents,
                        worldBounds.Center + extents,
                        out float hitDistance) ||
                    hitDistance > PreciseStaticDistance ||
                    hitDistance >= best.Distance)
                {
                    continue;
                }

                best.Entity = candidate;
                best.Distance = hitDistance;
                best.GroupId = group.GroupId;
                best.Kind = EntityHoverKind.Static;
                return;
            }
            while (StaticPreciseIndex.TryGetNextValue(out candidate, ref iterator));
        }

        private void VisitDynamicCell(int key, ref EntityHoverJobResult best)
        {
            if (!DynamicIndex.TryGetFirstValue(
                    key,
                    out Entity candidate,
                    out NativeParallelMultiHashMapIterator<int> iterator))
            {
                return;
            }

            do
            {
                TestDynamicCandidate(candidate, ref best);
            }
            while (DynamicIndex.TryGetNextValue(out candidate, ref iterator));
        }

        private void TestDynamicCandidate(Entity candidate, ref EntityHoverJobResult best)
        {
            if (!LocalToWorldLookup.HasComponent(candidate) ||
                DisabledLookup.HasComponent(candidate) ||
                !ScreenStartLookup.HasComponent(candidate) ||
                !ScreenEndLookup.HasComponent(candidate))
            {
                return;
            }

            if (!BoidSchoolMemberLookup.HasComponent(candidate))
            {
                return;
            }

            Entity groupEntity = BoidSchoolMemberLookup[candidate].SchoolEntity;
            if (groupEntity == Entity.Null || !HoverGroupLookup.HasComponent(groupEntity))
            {
                return;
            }

            float start = EntityHoverPickingMath.SelectViewValue(ScreenStartLookup[candidate].Value, Request.ViewIndex);
            float end = EntityHoverPickingMath.SelectViewValue(ScreenEndLookup[candidate].Value, Request.ViewIndex);
            if (end <= start ||
                Request.NormalizedScreenX < start ||
                Request.NormalizedScreenX > end)
            {
                return;
            }

            EntityHoverGroup group = HoverGroupLookup[groupEntity];
            float viewScale = EntityHoverPickingMath.SelectViewValue(group.ViewScaleMultipliers, Request.ViewIndex);
            float4x4 inverseTransform = math.inverse(LocalToWorldLookup[candidate].Value);
            float3 localOrigin = math.transform(inverseTransform, Request.RayOrigin);
            float3 localDirection =
                math.transform(inverseTransform, Request.RayOrigin + Request.RayDirection) - localOrigin;
            float3 center = group.LocalBoundsCenter * viewScale;
            float3 extents = group.LocalBoundsExtents * viewScale;

            if (!EntityHoverPickingMath.TryIntersectAabb(
                    localOrigin,
                    localDirection,
                    center - extents,
                    center + extents,
                    out float distance))
            {
                return;
            }

            if (distance < 0.0f || distance > best.Distance)
            {
                return;
            }

            best.Entity = candidate;
            best.Distance = distance;
            best.GroupId = group.GroupId;
            best.Kind = group.Kind;
        }

        private static float CalculateBoundaryDistance(float origin, float direction, float boundary)
        {
            if (math.abs(direction) < 0.000001f)
            {
                return float.MaxValue;
            }
            return math.max(0.0f, (boundary - origin) / direction);
        }

        private static float CalculateCellDistance(float direction, float cellSize)
        {
            if (math.abs(direction) < 0.000001f)
            {
                return float.MaxValue;
            }
            return cellSize / math.abs(direction);
        }

        private static int CalculateCellStep(float direction)
        {
            if (direction >= 0.0f)
            {
                return 1;
            }
            return -1;
        }

        private static float CalculateNextBoundary(int cell, int step, float cellSize)
        {
            if (step > 0)
            {
                return (cell + 1) * cellSize;
            }
            return cell * cellSize;
        }
    }

    public static class EntityHoverPickingMath
    {
        public static bool TryIntersectAabb(
            float3 rayOrigin,
            float3 rayDirection,
            float3 minimum,
            float3 maximum,
            out float distance)
        {
            bool intersects = TryIntersectAabbRange(
                rayOrigin,
                rayDirection,
                minimum,
                maximum,
                out float nearDistance,
                out _);
            distance = nearDistance;
            return intersects;
        }

        public static bool TryIntersectAabbRange(
            float3 rayOrigin,
            float3 rayDirection,
            float3 minimum,
            float3 maximum,
            out float nearDistance,
            out float farDistance)
        {
            nearDistance = 0.0f;
            farDistance = float.MaxValue;

            if (!IntersectAxis(rayOrigin.x, rayDirection.x, minimum.x, maximum.x, ref nearDistance, ref farDistance) ||
                !IntersectAxis(rayOrigin.y, rayDirection.y, minimum.y, maximum.y, ref nearDistance, ref farDistance) ||
                !IntersectAxis(rayOrigin.z, rayDirection.z, minimum.z, maximum.z, ref nearDistance, ref farDistance))
            {
                nearDistance = 0.0f;
                farDistance = 0.0f;
                return false;
            }

            return true;
        }

        public static float SelectViewValue(float4 values, int viewIndex)
        {
            if (viewIndex == 0)
            {
                return values.x;
            }
            if (viewIndex == 1)
            {
                return values.y;
            }
            if (viewIndex == 2)
            {
                return values.z;
            }
            return values.w;
        }

        private static bool IntersectAxis(
            float origin,
            float direction,
            float minimum,
            float maximum,
            ref float nearDistance,
            ref float farDistance)
        {
            if (math.abs(direction) < 0.000001f)
            {
                return origin >= minimum && origin <= maximum;
            }

            float inverseDirection = 1.0f / direction;
            float first = (minimum - origin) * inverseDirection;
            float second = (maximum - origin) * inverseDirection;
            if (first > second)
            {
                float swap = first;
                first = second;
                second = swap;
            }

            nearDistance = math.max(nearDistance, first);
            farDistance = math.min(farDistance, second);
            return nearDistance <= farDistance;
        }
    }
}
