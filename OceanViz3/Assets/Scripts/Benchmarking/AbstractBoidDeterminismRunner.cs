using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace OceanViz3.Benchmarking
{
    /// <summary>
    /// Runs an asset-free boid determinism check using abstract dynamic boid schools.
    /// Trigger with -oceanvizBoidDeterminism and optional -boidDeterminismOutput,
    /// -boidDeterminismSteps, and -boidDeterminismIntervalSteps command-line arguments.
    /// </summary>
    public class AbstractBoidDeterminismRunner : MonoBehaviour
    {
        private const string RunnerFlag = "-oceanvizBoidDeterminism";
        private const string OutputArg = "-boidDeterminismOutput";
        private const string StepsArg = "-boidDeterminismSteps";
        private const string IntervalStepsArg = "-boidDeterminismIntervalSteps";
        private const int DefaultTotalSteps = 3600;
        private const int DefaultIntervalSteps = 600;
        private const float MaxRunSeconds = 120.0f;

        private enum RunnerState
        {
            WaitingForWorld,
            WaitingForSpawn,
            Running,
            Finished
        }

        private string outputPath;
        private int totalSteps;
        private int intervalSteps;
        private RunnerState state;
        private EntityManager entityManager;
        private EntityQuery fixedStepTimeQuery;
        private EntityQuery schoolQuery;
        private bool hasFixedStepTimeQuery;
        private bool hasSchoolQuery;
        private Entity stepControlEntity;
        private int nextSnapshotStep;
        private float startedAtRealtime;
        private AbstractBoidDeterminismResult result;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void StartFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (!HasArgument(args, RunnerFlag))
            {
                return;
            }

            GameObject runnerObject = new GameObject("Abstract Boid Determinism Runner");
            DontDestroyOnLoad(runnerObject);
            AbstractBoidDeterminismRunner runner = runnerObject.AddComponent<AbstractBoidDeterminismRunner>();
            runner.Initialize(args);
        }

        private static bool HasArgument(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetArgumentValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return string.Empty;
        }

        private static int GetArgumentInt(string[] args, string name, int fallback)
        {
            string value = GetArgumentValue(args, name);
            if (string.IsNullOrEmpty(value))
            {
                return fallback;
            }

            int parsed;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            Debug.Assert(false, "Invalid integer command-line argument " + name + ": " + value);
            return fallback;
        }

        private void Initialize(string[] args)
        {
            outputPath = GetArgumentValue(args, OutputArg);
            if (string.IsNullOrEmpty(outputPath))
            {
                outputPath = Path.Combine(Application.persistentDataPath, "abstract-boid-determinism-result.json");
            }

            totalSteps = math.max(1, GetArgumentInt(args, StepsArg, DefaultTotalSteps));
            intervalSteps = math.max(1, GetArgumentInt(args, IntervalStepsArg, DefaultIntervalSteps));
            state = RunnerState.WaitingForWorld;
            startedAtRealtime = Time.realtimeSinceStartup;

            result = new AbstractBoidDeterminismResult();
            result.unityVersion = Application.unityVersion;
            result.dateUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            result.totalSteps = totalSteps;
            result.intervalSteps = intervalSteps;
            result.snapshots = new AbstractBoidSnapshotResult[CalculateSnapshotCapacity(totalSteps, intervalSteps)];

            Debug.Log("[AbstractBoidDeterminism] Started. Steps: " + totalSteps + ", interval: " + intervalSteps + ", output: " + outputPath);
        }

        private static int CalculateSnapshotCapacity(int steps, int interval)
        {
            int count = steps / interval;
            if (steps % interval != 0)
            {
                count++;
            }

            if (steps == 0 || steps % interval == 0)
            {
                return count;
            }

            return count;
        }

        private void Update()
        {
            if (state == RunnerState.Finished)
            {
                return;
            }

            if (Time.realtimeSinceStartup - startedAtRealtime > MaxRunSeconds)
            {
                Finish(true, "Timed out in state " + state + ".");
                return;
            }

            if (state == RunnerState.WaitingForWorld)
            {
                TryCreateAbstractSetup();
                return;
            }

            if (state == RunnerState.WaitingForSpawn)
            {
                WaitForSpawn();
                return;
            }

            if (state == RunnerState.Running)
            {
            }
        }

        private void TryCreateAbstractSetup()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                return;
            }

            entityManager = world.EntityManager;
            fixedStepTimeQuery = entityManager.CreateEntityQuery(typeof(BoidFixedStepTime));
            hasFixedStepTimeQuery = true;
            if (fixedStepTimeQuery.CalculateEntityCount() != 1)
            {
                return;
            }

            CleanupExistingAbstractEntities();
            EnsureSceneData();
            ResetFixedStepTime();
            CreateStepControl();

            Entity preyPrototype = CreateBoidPrototype("AbstractPreyPrototype");
            Entity predatorPrototype = CreateBoidPrototype("AbstractPredatorPrototype");
            Entity preyTarget = CreateTarget("AbstractPreyTarget", 1001, 1);
            Entity predatorTarget = CreateTarget("AbstractPredatorTarget", 1002, 1);

            CreateSchool("Abstract Sea Bass", 1001, 1, 160, preyPrototype, preyTarget, CreateSeaBassLikeSchool(1001, 1, preyTarget));
            CreateSchool("Abstract Marlin", 1002, 1, 12, predatorPrototype, predatorTarget, CreateMarlinLikeSchool(1002, 1, predatorTarget));

            schoolQuery = entityManager.CreateEntityQuery(typeof(AbstractBoidDeterminismTag), typeof(BoidSchoolComponent), typeof(BoidSchoolOwnedBoid));
            hasSchoolQuery = true;
            state = RunnerState.WaitingForSpawn;
            Debug.Log("[AbstractBoidDeterminism] Abstract schools created.");
        }

        private void CleanupExistingAbstractEntities()
        {
            EntityQueryDesc queryDesc = new EntityQueryDesc();
            queryDesc.All = new ComponentType[] { typeof(AbstractBoidDeterminismTag) };
            queryDesc.Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab | EntityQueryOptions.IgnoreComponentEnabledState;
            EntityQuery query = entityManager.CreateEntityQuery(queryDesc);
            entityManager.DestroyEntity(query);
            query.Dispose();
        }

        private void EnsureSceneData()
        {
            EntityQuery sceneDataQuery = entityManager.CreateEntityQuery(typeof(SceneData));
            if (sceneDataQuery.CalculateEntityCount() == 0)
            {
                Entity sceneDataEntity = entityManager.CreateEntity(typeof(SceneData));
                entityManager.SetName(sceneDataEntity, "Abstract Determinism Scene Data");
                entityManager.SetComponentData(sceneDataEntity, CreateSceneData());
                sceneDataQuery.Dispose();
                return;
            }

            Entity sceneEntity = sceneDataQuery.GetSingletonEntity();
            entityManager.SetComponentData(sceneEntity, CreateSceneData());
            sceneDataQuery.Dispose();
        }

        private static SceneData CreateSceneData()
        {
            return new SceneData
            {
                CameraPosition = new float3(0.0f, 0.0f, -25.0f),
                CullingStartMeshSize = 1.0f,
                CullingStartDistance = 100000.0f,
                CullingEndMeshSize = 10.0f,
                CullingEndDistance = 100000.0f
            };
        }

        private void ResetFixedStepTime()
        {
            Entity fixedStepEntity = fixedStepTimeQuery.GetSingletonEntity();
            entityManager.SetComponentData(fixedStepEntity, new BoidFixedStepTime
            {
                Accumulator = 0.0f,
                FixedStep = BoidFixedStepTime.DefaultFixedStep,
                FixedElapsedTime = 0.0f,
                CurrentFrameStartElapsedTime = 0.0f,
                StepCount = 0,
                MaxStepsPerFrame = BoidFixedStepTime.DefaultMaxStepsPerFrame
            });
        }

        private void CreateStepControl()
        {
            stepControlEntity = entityManager.CreateEntity(typeof(AbstractBoidDeterminismTag), typeof(AbstractBoidDeterminismStepControl));
            entityManager.SetName(stepControlEntity, "Abstract Boid Determinism Step Control");
            entityManager.SetComponentData(stepControlEntity, new AbstractBoidDeterminismStepControl
            {
                Enabled = false,
                CompletedStepCount = 0,
                FixedStep = BoidFixedStepTime.DefaultFixedStep
            });
        }

        private Entity CreateBoidPrototype(string entityName)
        {
            Entity entity = entityManager.CreateEntity(
                typeof(Prefab),
                typeof(AbstractBoidDeterminismTag),
                typeof(LocalToWorld),
                typeof(BoidUnique),
                typeof(BoidSchoolMember),
                typeof(BoidSpawnPending),
                typeof(LODState),
                typeof(CullingComponent),
                typeof(CurrentVectorOverride),
                typeof(AccumulatedTimeOverride),
                typeof(AnimationSpeedOverride),
                typeof(AnimationRandomOffsetOverride),
                typeof(ScreenDisplayStartOverride),
                typeof(ScreenDisplayEndOverride),
                typeof(MetalnessOverride),
                typeof(SineWavelengthOverride),
                typeof(SineDeformationAmplitudeOverride),
                typeof(Secondary1AnimationAmplitudeOverride),
                typeof(InvertSecondary1AnimationOverride),
                typeof(Secondary2AnimationAmplitudeOverride),
                typeof(InvertSecondary2AnimationOverride),
                typeof(SideToSideAmplitudeOverride),
                typeof(YawAmplitudeOverride),
                typeof(RollingSpineAmplitudeOverride),
                typeof(MeshZMinOverride),
                typeof(MeshZMaxOverride),
                typeof(PositiveYClipOverride),
                typeof(NegativeYClipOverride));

            entityManager.SetName(entity, entityName);
            entityManager.SetComponentData(entity, new LocalToWorld { Value = float4x4.identity });
            entityManager.SetComponentData(entity, new BoidUnique
            {
                Disabled = false,
                MoveSpeedModifier = 1.0f,
                TargetSpeedModifier = 1.0f,
                MaxVerticalAngleOffset = 0.0f,
                TargetVector = float3.zero,
                PreviousHeading = new float3(0.0f, 0.0f, 1.0f),
                BendRefHeading = new float3(0.0f, 0.0f, 1.0f)
            });
            entityManager.SetComponentData(entity, new BoidSchoolMember
            {
                SchoolEntity = Entity.Null,
                SchoolIndex = -1,
                DynamicEntityId = -1,
                BoidSchoolId = -1
            });
            entityManager.SetComponentData(entity, new CullingComponent { MaxDistance = 100000.0f });
            entityManager.SetComponentData(entity, new LODState { CurrentLOD = 0 });
            entityManager.SetComponentEnabled<BoidUnique>(entity, false);
            entityManager.SetComponentEnabled<BoidSpawnPending>(entity, false);
            return entity;
        }

        private Entity CreateTarget(string entityName, int dynamicEntityId, int boidSchoolId)
        {
            BoidBoundaryData boundary = CreateBoundary();
            Entity entity = entityManager.CreateEntity(
                typeof(AbstractBoidDeterminismTag),
                typeof(BoidTarget),
                typeof(LocalTransform),
                typeof(LocalToWorld));

            entityManager.SetName(entity, entityName);
            entityManager.SetComponentData(entity, new BoidTarget
            {
                DynamicEntityId = dynamicEntityId,
                BoidSchoolId = boidSchoolId,
                StartPosition = boundary.BoundsCenter,
                EndPosition = boundary.BoundsCenter,
                LerpDuration = 1.0f,
                LerpTimer = 0.0f
            });
            entityManager.SetComponentData(entity, new LocalTransform
            {
                Position = boundary.BoundsCenter,
                Rotation = quaternion.identity,
                Scale = 1.0f
            });
            entityManager.SetComponentData(entity, new LocalToWorld
            {
                Value = float4x4.TRS(boundary.BoundsCenter, quaternion.identity, new float3(1.0f, 1.0f, 1.0f))
            });
            return entity;
        }

        private void CreateSchool(string entityName, int dynamicEntityId, int boidSchoolId, int requestedCount, Entity prototype, Entity target, BoidSchoolComponent school)
        {
            Entity entity = entityManager.CreateEntity(
                typeof(AbstractBoidDeterminismTag),
                typeof(BoidSchoolComponent),
                typeof(BoidSchoolRuntimeData),
                typeof(BoidSchoolSpawnPrototype));

            entityManager.SetName(entity, entityName);
            school.DynamicEntityId = dynamicEntityId;
            school.BoidSchoolId = boidSchoolId;
            school.BoidPrototype = prototype;
            school.BoidTargetPrefab = Entity.Null;
            school.Target = target;
            school.Count = 0;
            school.RequestedCount = requestedCount;
            school.DestroyRequested = false;
            school.ShaderUpdateRequested = false;
            entityManager.SetComponentData(entity, school);
            entityManager.SetComponentData(entity, new BoidSchoolRuntimeData
            {
                DynamicEntityId = dynamicEntityId,
                BoidSchoolId = boidSchoolId,
                SchoolIndex = BoidSchoolRuntimeUtility.ComputeSchoolIndex(dynamicEntityId, boidSchoolId),
                WaterCurrentInfluence = -1.0f,
                Target = target
            });
            entityManager.SetComponentData(entity, new BoidSchoolSpawnPrototype { Value = Entity.Null });
            entityManager.AddBuffer<BoidSchoolOwnedBoid>(entity);
        }

        private static BoidSchoolComponent CreateSeaBassLikeSchool(int dynamicEntityId, int boidSchoolId, Entity target)
        {
            BoidBoundaryData boundary = CreateBoundary();
            return new BoidSchoolComponent
            {
                DynamicEntityId = dynamicEntityId,
                BoidSchoolId = boidSchoolId,
                BoundsCenter = boundary.BoundsCenter,
                BoundsSize = boundary.BoundsMax - boundary.BoundsMin,
                Boundary = boundary,
                Target = target,
                SeparationWeight = 0.01f,
                AlignmentWeight = 0.6f,
                TargetWeight = 0.3f,
                ObstacleAversionDistance = 2.0f,
                Speed = 0.6f,
                MaxVerticalAngle = 30.0f,
                SeabedBound = false,
                Predator = false,
                Prey = true,
                CellRadius = 4.0f,
                MaxTurnRate = 1.0f,
                StateTransitionSpeed = 3.0f,
                StateChangeTimerMin = 2.0f,
                StateChangeTimerMax = 4.0f,
                WaterCurrentInfluence = -1.0f,
                BoneAnimated = false,
                NumberOfLODs = 0,
                SpeedModifierMin = 0.3f,
                SpeedModifierMax = 3.0f,
                SpawnClustering = 0.8f,
                ScaleMin = 0.45f,
                ScaleMax = 1.85f,
                SpeedJitterAmplitude = 0.08f,
                SpeedJitterFrequency = 0.6f,
                ViewsCount = 1,
                ViewVisibilityPercentages = new float4(100.0f, 100.0f, 100.0f, 100.0f),
                AnimationSpeed = 7.0f,
                MeshLargestDimension = 1.0f,
                MeshZMin = -0.5f,
                MeshZMax = 0.5f,
                PositiveYClip = 1.0f,
                NegativeYClip = 0.55f
            };
        }

        private static BoidSchoolComponent CreateMarlinLikeSchool(int dynamicEntityId, int boidSchoolId, Entity target)
        {
            BoidBoundaryData boundary = CreateBoundary();
            return new BoidSchoolComponent
            {
                DynamicEntityId = dynamicEntityId,
                BoidSchoolId = boidSchoolId,
                BoundsCenter = boundary.BoundsCenter,
                BoundsSize = boundary.BoundsMax - boundary.BoundsMin,
                Boundary = boundary,
                Target = target,
                SeparationWeight = 2.5f,
                AlignmentWeight = 0.0f,
                TargetWeight = 2.0f,
                ObstacleAversionDistance = 2.0f,
                Speed = 3.0f,
                MaxVerticalAngle = 30.0f,
                SeabedBound = false,
                Predator = true,
                Prey = false,
                CellRadius = 8.0f,
                MaxTurnRate = 1.0f,
                StateTransitionSpeed = 0.5f,
                StateChangeTimerMin = 1.0f,
                StateChangeTimerMax = 10.0f,
                WaterCurrentInfluence = -1.0f,
                BoneAnimated = false,
                NumberOfLODs = 0,
                SpeedModifierMin = 0.5f,
                SpeedModifierMax = 1.5f,
                SpawnClustering = 0.8f,
                ScaleMin = 0.45f,
                ScaleMax = 1.85f,
                SpeedJitterAmplitude = 0.08f,
                SpeedJitterFrequency = 0.6f,
                ViewsCount = 1,
                ViewVisibilityPercentages = new float4(100.0f, 100.0f, 100.0f, 100.0f),
                AnimationSpeed = 5.0f,
                MeshLargestDimension = 4.0f,
                MeshZMin = -2.0f,
                MeshZMax = 2.0f,
                PositiveYClip = 1.0f,
                NegativeYClip = 0.362f
            };
        }

        private static BoidBoundaryData CreateBoundary()
        {
            BoidBoundaryData boundary = BoidBoundaryData.CreateDefaultBox(float3.zero, new float3(80.0f, 36.0f, 80.0f));
            boundary.Hardness = 0.4f;
            return boundary;
        }

        private void WaitForSpawn()
        {
            if (!AllRequestedBoidsSpawned())
            {
                return;
            }

            nextSnapshotStep = intervalSteps;
            if (nextSnapshotStep > totalSteps)
            {
                nextSnapshotStep = totalSteps;
            }

            state = RunnerState.Running;
            entityManager.SetComponentData(stepControlEntity, new AbstractBoidDeterminismStepControl
            {
                Enabled = true,
                CompletedStepCount = 0,
                FixedStep = BoidFixedStepTime.DefaultFixedStep
            });
            Debug.Log("[AbstractBoidDeterminism] Spawn complete. Recording snapshots.");
        }

        private void LateUpdate()
        {
            if (state != RunnerState.Running)
            {
                return;
            }

            RecordDueSnapshots();
        }

        private bool AllRequestedBoidsSpawned()
        {
            NativeArray<Entity> schools = schoolQuery.ToEntityArray(Allocator.Temp);
            try
            {
                if (schools.Length != 2)
                {
                    return false;
                }

                for (int i = 0; i < schools.Length; i++)
                {
                    Entity schoolEntity = schools[i];
                    BoidSchoolComponent school = entityManager.GetComponentData<BoidSchoolComponent>(schoolEntity);
                    DynamicBuffer<BoidSchoolOwnedBoid> ownedBoids = entityManager.GetBuffer<BoidSchoolOwnedBoid>(schoolEntity);
                    if (ownedBoids.Length != school.RequestedCount)
                    {
                        return false;
                    }

                    for (int boidIndex = 0; boidIndex < ownedBoids.Length; boidIndex++)
                    {
                        Entity boidEntity = ownedBoids[boidIndex].Value;
                        if (!entityManager.Exists(boidEntity))
                        {
                            return false;
                        }
                        if (!entityManager.HasComponent<BoidUnique>(boidEntity))
                        {
                            return false;
                        }
                        if (!entityManager.IsComponentEnabled<BoidUnique>(boidEntity))
                        {
                            return false;
                        }
                    }
                }
            }
            finally
            {
                schools.Dispose();
            }

            return true;
        }

        private void RecordDueSnapshots()
        {
            AbstractBoidDeterminismStepControl stepControl = entityManager.GetComponentData<AbstractBoidDeterminismStepControl>(stepControlEntity);
            int currentStep = stepControl.CompletedStepCount;
            while (currentStep >= nextSnapshotStep)
            {
                AddSnapshot(nextSnapshotStep);
                if (nextSnapshotStep >= totalSteps)
                {
                    Finish(false, string.Empty);
                    return;
                }

                nextSnapshotStep += intervalSteps;
                if (nextSnapshotStep > totalSteps)
                {
                    nextSnapshotStep = totalSteps;
                }
            }
        }

        private void AddSnapshot(int step)
        {
            int index = result.snapshotCount;
            Debug.Assert(index < result.snapshots.Length, "Too many boid determinism snapshots.");
            if (index >= result.snapshots.Length)
            {
                return;
            }

            ulong hash = ComputeSnapshotHash(step, out int boidCount, out TrackedBoidSnapshot trackedBoid);
            result.snapshots[index] = new AbstractBoidSnapshotResult
            {
                step = step,
                hash = hash.ToString("X16", CultureInfo.InvariantCulture),
                boidCount = boidCount,
                trackedDynamicEntityId = trackedBoid.DynamicEntityId,
                trackedBoidSchoolId = trackedBoid.BoidSchoolId,
                trackedBoidIndex = trackedBoid.BoidIndex,
                trackedPosition = trackedBoid.Position,
                trackedForward = trackedBoid.Forward
            };
            result.snapshotCount++;

            Debug.Log("[AbstractBoidDeterminism] Snapshot step " + step + " hash " + result.snapshots[index].hash + ".");
        }

        private ulong ComputeSnapshotHash(int step, out int boidCount, out TrackedBoidSnapshot trackedBoid)
        {
            SnapshotHasher hasher = new SnapshotHasher();
            hasher.AddInt(step);
            boidCount = 0;
            trackedBoid = new TrackedBoidSnapshot
            {
                DynamicEntityId = -1,
                BoidSchoolId = -1,
                BoidIndex = -1,
                Position = Vector3.zero,
                Forward = Vector3.forward
            };

            NativeArray<Entity> schools = schoolQuery.ToEntityArray(Allocator.Temp);
            NativeArray<BoidSchoolComponent> schoolComponents = schoolQuery.ToComponentDataArray<BoidSchoolComponent>(Allocator.Temp);
            try
            {
                SortSchools(schools, schoolComponents);

                for (int schoolIndex = 0; schoolIndex < schools.Length; schoolIndex++)
                {
                    Entity schoolEntity = schools[schoolIndex];
                    BoidSchoolComponent school = schoolComponents[schoolIndex];
                    DynamicBuffer<BoidSchoolOwnedBoid> ownedBoids = entityManager.GetBuffer<BoidSchoolOwnedBoid>(schoolEntity);

                    hasher.AddInt(school.DynamicEntityId);
                    hasher.AddInt(school.BoidSchoolId);
                    hasher.AddInt(ownedBoids.Length);

                    for (int boidIndex = 0; boidIndex < ownedBoids.Length; boidIndex++)
                    {
                        Entity boidEntity = ownedBoids[boidIndex].Value;
                        LocalToWorld localToWorld = entityManager.GetComponentData<LocalToWorld>(boidEntity);
                        BoidUnique boidUnique = entityManager.GetComponentData<BoidUnique>(boidEntity);
                        float3 position = localToWorld.Position;
                        float3 forward = math.normalizesafe(localToWorld.Forward, new float3(0.0f, 0.0f, 1.0f));

                        hasher.AddInt(school.DynamicEntityId);
                        hasher.AddInt(school.BoidSchoolId);
                        hasher.AddInt(boidIndex);
                        hasher.AddFloat3(position);
                        hasher.AddFloat3(forward);
                        hasher.AddFloat(boidUnique.MoveSpeedModifier);
                        hasher.AddFloat(boidUnique.TargetSpeedModifier);
                        hasher.AddFloat3(boidUnique.TargetVector);
                        hasher.AddFloat3(boidUnique.PreviousHeading);

                        if (trackedBoid.BoidIndex < 0 && school.Prey)
                        {
                            trackedBoid = new TrackedBoidSnapshot
                            {
                                DynamicEntityId = school.DynamicEntityId,
                                BoidSchoolId = school.BoidSchoolId,
                                BoidIndex = boidIndex,
                                Position = new Vector3(position.x, position.y, position.z),
                                Forward = new Vector3(forward.x, forward.y, forward.z)
                            };
                        }

                        boidCount++;
                    }
                }
            }
            finally
            {
                schools.Dispose();
                schoolComponents.Dispose();
            }

            return hasher.Value;
        }

        private static void SortSchools(NativeArray<Entity> schools, NativeArray<BoidSchoolComponent> schoolComponents)
        {
            for (int i = 0; i < schoolComponents.Length - 1; i++)
            {
                for (int j = i + 1; j < schoolComponents.Length; j++)
                {
                    bool swap = schoolComponents[j].DynamicEntityId < schoolComponents[i].DynamicEntityId;
                    if (schoolComponents[j].DynamicEntityId == schoolComponents[i].DynamicEntityId)
                    {
                        swap = schoolComponents[j].BoidSchoolId < schoolComponents[i].BoidSchoolId;
                    }

                    if (swap)
                    {
                        BoidSchoolComponent school = schoolComponents[i];
                        schoolComponents[i] = schoolComponents[j];
                        schoolComponents[j] = school;

                        Entity entity = schools[i];
                        schools[i] = schools[j];
                        schools[j] = entity;
                    }
                }
            }
        }

        private void Finish(bool timedOut, string failureMessage)
        {
            result.timedOut = timedOut;
            result.failureMessage = failureMessage;
            result.finalBoidCount = CountAbstractBoids();
            result.wallClockSeconds = Time.realtimeSinceStartup - startedAtRealtime;
            WriteResult();
            state = RunnerState.Finished;

            if (timedOut)
            {
                Debug.LogError("[AbstractBoidDeterminism] Failed: " + failureMessage);
            }
            else
            {
                Debug.Log("[AbstractBoidDeterminism] Finished. Snapshots: " + result.snapshotCount + ", boids: " + result.finalBoidCount + ".");
            }

            if (Application.isBatchMode)
            {
                Application.Quit(timedOut ? 2 : 0);
            }
        }

        private int CountAbstractBoids()
        {
            if (!hasSchoolQuery)
            {
                return 0;
            }

            int count = 0;
            NativeArray<Entity> schools = schoolQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < schools.Length; i++)
                {
                    DynamicBuffer<BoidSchoolOwnedBoid> ownedBoids = entityManager.GetBuffer<BoidSchoolOwnedBoid>(schools[i]);
                    count += ownedBoids.Length;
                }
            }
            finally
            {
                schools.Dispose();
            }

            return count;
        }

        private void WriteResult()
        {
            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonUtility.ToJson(result, true);
            File.WriteAllText(outputPath, json, Encoding.UTF8);
        }

        private void OnDestroy()
        {
            if (hasFixedStepTimeQuery)
            {
                fixedStepTimeQuery.Dispose();
                hasFixedStepTimeQuery = false;
            }
            if (hasSchoolQuery)
            {
                schoolQuery.Dispose();
                hasSchoolQuery = false;
            }
        }

        private struct TrackedBoidSnapshot
        {
            public int DynamicEntityId;
            public int BoidSchoolId;
            public int BoidIndex;
            public Vector3 Position;
            public Vector3 Forward;
        }

        private struct SnapshotHasher
        {
            private const ulong Offset = 14695981039346656037UL;
            private const ulong Prime = 1099511628211UL;

            public ulong Value;

            public void AddInt(int value)
            {
                AddUInt(unchecked((uint)value));
            }

            public void AddFloat(float value)
            {
                AddUInt(unchecked((uint)BitConverter.SingleToInt32Bits(value)));
            }

            public void AddFloat3(float3 value)
            {
                AddFloat(value.x);
                AddFloat(value.y);
                AddFloat(value.z);
            }

            private void AddUInt(uint value)
            {
                if (Value == 0UL)
                {
                    Value = Offset;
                }

                Value ^= value & 0xFFUL;
                Value *= Prime;
                Value ^= (value >> 8) & 0xFFUL;
                Value *= Prime;
                Value ^= (value >> 16) & 0xFFUL;
                Value *= Prime;
                Value ^= (value >> 24) & 0xFFUL;
                Value *= Prime;
            }
        }
    }

    public struct AbstractBoidDeterminismTag : IComponentData
    {
    }

    public struct AbstractBoidDeterminismStepControl : IComponentData
    {
        public bool Enabled;
        public int CompletedStepCount;
        public float FixedStep;
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BoidFixedStepSystem))]
    [UpdateBefore(typeof(BoidSchoolRuntimeSyncSystem))]
    public partial struct AbstractBoidDeterminismStepControlSystem : ISystem
    {
        private EntityQuery fixedStepTimeQuery;
        private EntityQuery stepControlQuery;

        public void OnCreate(ref SystemState state)
        {
            fixedStepTimeQuery = state.EntityManager.CreateEntityQuery(typeof(BoidFixedStepTime));
            stepControlQuery = state.EntityManager.CreateEntityQuery(typeof(AbstractBoidDeterminismStepControl));
            state.RequireForUpdate(fixedStepTimeQuery);
            state.RequireForUpdate(stepControlQuery);
        }

        public void OnUpdate(ref SystemState state)
        {
            AbstractBoidDeterminismStepControl stepControl = stepControlQuery.GetSingleton<AbstractBoidDeterminismStepControl>();
            if (!stepControl.Enabled)
            {
                return;
            }

            BoidFixedStepTime fixedStepTime = fixedStepTimeQuery.GetSingleton<BoidFixedStepTime>();
            float fixedStep = stepControl.FixedStep;
            if (fixedStep <= 0.0f)
            {
                fixedStep = BoidFixedStepTime.DefaultFixedStep;
            }

            fixedStepTime.Accumulator = 0.0f;
            fixedStepTime.FixedStep = fixedStep;
            fixedStepTime.CurrentFrameStartElapsedTime = stepControl.CompletedStepCount * fixedStep;
            fixedStepTime.FixedElapsedTime = (stepControl.CompletedStepCount + 1) * fixedStep;
            fixedStepTime.StepCount = 1;
            fixedStepTime.MaxStepsPerFrame = 1;
            fixedStepTimeQuery.SetSingleton(fixedStepTime);

            stepControl.CompletedStepCount++;
            stepControlQuery.SetSingleton(stepControl);
        }
    }

    [Serializable]
    public class AbstractBoidDeterminismResult
    {
        public string unityVersion;
        public string dateUtc;
        public int totalSteps;
        public int intervalSteps;
        public int snapshotCount;
        public int finalBoidCount;
        public bool timedOut;
        public string failureMessage;
        public float wallClockSeconds;
        public AbstractBoidSnapshotResult[] snapshots;
    }

    [Serializable]
    public class AbstractBoidSnapshotResult
    {
        public int step;
        public string hash;
        public int boidCount;
        public int trackedDynamicEntityId;
        public int trackedBoidSchoolId;
        public int trackedBoidIndex;
        public Vector3 trackedPosition;
        public Vector3 trackedForward;
    }
}
