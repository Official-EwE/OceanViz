using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Serialization;

namespace OceanViz3
{
    public enum BoidBoundaryShapeType
    {
        Box = 0,
        Sphere = 1,
        Capsule = 2,
        ConvexMesh = 3
    }

    [Serializable]
    public struct BoidBoundaryData
    {
        public const int MaxConvexPlanes = 16;

        public BoidBoundaryShapeType ShapeType;
        public float Hardness;
        public float3 BoundsCenter;
        public float3 BoundsMin;
        public float3 BoundsMax;
        public float4x4 LocalToWorld;
        public float4x4 WorldToLocal;
        public float3 LocalCenter;
        public float3 LocalExtents;
        public float CapsuleRadius;
        public float CapsuleHeight;
        public int CapsuleAxis;
        public int ConvexPlaneCount;
        public float4 ConvexPlane0;
        public float4 ConvexPlane1;
        public float4 ConvexPlane2;
        public float4 ConvexPlane3;
        public float4 ConvexPlane4;
        public float4 ConvexPlane5;
        public float4 ConvexPlane6;
        public float4 ConvexPlane7;
        public float4 ConvexPlane8;
        public float4 ConvexPlane9;
        public float4 ConvexPlane10;
        public float4 ConvexPlane11;
        public float4 ConvexPlane12;
        public float4 ConvexPlane13;
        public float4 ConvexPlane14;
        public float4 ConvexPlane15;

        public static BoidBoundaryData CreateDefaultBox(float3 boundsCenter, float3 boundsSize)
        {
            return new BoidBoundaryData
            {
                ShapeType = BoidBoundaryShapeType.Box,
                Hardness = 0.1f,
                BoundsCenter = boundsCenter,
                BoundsMin = boundsCenter - (boundsSize * 0.5f),
                BoundsMax = boundsCenter + (boundsSize * 0.5f),
                LocalToWorld = float4x4.TRS(boundsCenter, quaternion.identity, new float3(1.0f, 1.0f, 1.0f)),
                WorldToLocal = math.inverse(float4x4.TRS(boundsCenter, quaternion.identity, new float3(1.0f, 1.0f, 1.0f))),
                LocalCenter = float3.zero,
                LocalExtents = boundsSize * 0.5f,
                CapsuleAxis = 1
            };
        }
    }

    public static class BoidBoundaryUtility
    {
        public static bool TryProjectInside(in BoidBoundaryData boundary, float3 worldPosition, out float3 projectedWorldPosition, out float3 outwardWorldNormal, out float distanceOutside)
        {
            float3 localPosition = math.transform(boundary.WorldToLocal, worldPosition);
            float3 projectedLocalPosition = localPosition;
            float3 outwardLocalNormal = float3.zero;
            distanceOutside = 0.0f;

            if (boundary.ShapeType == BoidBoundaryShapeType.Box)
            {
                float3 localMin = boundary.LocalCenter - boundary.LocalExtents;
                float3 localMax = boundary.LocalCenter + boundary.LocalExtents;
                projectedLocalPosition = math.clamp(localPosition, localMin, localMax);
                float3 localDelta = localPosition - projectedLocalPosition;
                distanceOutside = math.length(localDelta);
                outwardLocalNormal = math.normalizesafe(localDelta);
            }
            else if (boundary.ShapeType == BoidBoundaryShapeType.Sphere)
            {
                float radius = math.max(0.001f, boundary.LocalExtents.x);
                float3 localDelta = localPosition - boundary.LocalCenter;
                float localDistance = math.length(localDelta);
                if (localDistance > radius)
                {
                    outwardLocalNormal = localDelta / localDistance;
                    projectedLocalPosition = boundary.LocalCenter + (outwardLocalNormal * radius);
                    distanceOutside = localDistance - radius;
                }
            }
            else if (boundary.ShapeType == BoidBoundaryShapeType.Capsule)
            {
                ProjectCapsule(boundary, localPosition, out projectedLocalPosition, out outwardLocalNormal, out distanceOutside);
            }
            else if (boundary.ShapeType == BoidBoundaryShapeType.ConvexMesh)
            {
                ProjectConvexMesh(boundary, localPosition, out projectedLocalPosition, out outwardLocalNormal, out distanceOutside);
            }

            projectedWorldPosition = math.transform(boundary.LocalToWorld, projectedLocalPosition);
            float3 worldDelta = worldPosition - projectedWorldPosition;
            outwardWorldNormal = math.normalizesafe(worldDelta);
            if (math.lengthsq(outwardWorldNormal) < 0.0001f)
            {
                float3 normalEnd = math.transform(boundary.LocalToWorld, localPosition + outwardLocalNormal);
                outwardWorldNormal = math.normalizesafe(normalEnd - worldPosition);
            }

            return distanceOutside > 0.0001f;
        }

        public static bool Contains(in BoidBoundaryData boundary, float3 worldPosition)
        {
            TryProjectInside(boundary, worldPosition, out float3 _, out float3 _, out float distanceOutside);
            return distanceOutside <= 0.0001f;
        }

        private static void ProjectCapsule(in BoidBoundaryData boundary, float3 localPosition, out float3 projectedLocalPosition, out float3 outwardLocalNormal, out float distanceOutside)
        {
            float radius = math.max(0.001f, boundary.CapsuleRadius);
            float halfLineLength = math.max(0.0f, (boundary.CapsuleHeight * 0.5f) - radius);
            float3 axis = GetAxis(boundary.CapsuleAxis);
            float3 localOffset = localPosition - boundary.LocalCenter;
            float axisDistance = math.clamp(math.dot(localOffset, axis), -halfLineLength, halfLineLength);
            float3 closestLinePoint = boundary.LocalCenter + (axis * axisDistance);
            float3 fromLine = localPosition - closestLinePoint;
            float fromLineLength = math.length(fromLine);

            projectedLocalPosition = localPosition;
            outwardLocalNormal = float3.zero;
            distanceOutside = 0.0f;
            if (fromLineLength > radius)
            {
                outwardLocalNormal = fromLine / fromLineLength;
                projectedLocalPosition = closestLinePoint + (outwardLocalNormal * radius);
                distanceOutside = fromLineLength - radius;
            }
        }

        private static void ProjectConvexMesh(in BoidBoundaryData boundary, float3 localPosition, out float3 projectedLocalPosition, out float3 outwardLocalNormal, out float distanceOutside)
        {
            projectedLocalPosition = localPosition;
            outwardLocalNormal = float3.zero;
            distanceOutside = 0.0f;

            for (int i = 0; i < boundary.ConvexPlaneCount; i++)
            {
                float4 plane = GetConvexPlane(boundary, i);
                float signedDistance = math.dot(plane.xyz, projectedLocalPosition) + plane.w;
                if (signedDistance <= 0.0f)
                {
                    continue;
                }

                projectedLocalPosition -= plane.xyz * signedDistance;
                if (signedDistance > distanceOutside)
                {
                    distanceOutside = signedDistance;
                    outwardLocalNormal = plane.xyz;
                }
            }
        }

        private static float3 GetAxis(int axisIndex)
        {
            if (axisIndex == 0)
            {
                return new float3(1.0f, 0.0f, 0.0f);
            }
            if (axisIndex == 2)
            {
                return new float3(0.0f, 0.0f, 1.0f);
            }
            return new float3(0.0f, 1.0f, 0.0f);
        }

        public static float4 GetConvexPlane(in BoidBoundaryData boundary, int index)
        {
            if (index == 0)
            {
                return boundary.ConvexPlane0;
            }
            if (index == 1)
            {
                return boundary.ConvexPlane1;
            }
            if (index == 2)
            {
                return boundary.ConvexPlane2;
            }
            if (index == 3)
            {
                return boundary.ConvexPlane3;
            }
            if (index == 4)
            {
                return boundary.ConvexPlane4;
            }
            if (index == 5)
            {
                return boundary.ConvexPlane5;
            }
            if (index == 6)
            {
                return boundary.ConvexPlane6;
            }
            if (index == 7)
            {
                return boundary.ConvexPlane7;
            }
            if (index == 8)
            {
                return boundary.ConvexPlane8;
            }
            if (index == 9)
            {
                return boundary.ConvexPlane9;
            }
            if (index == 10)
            {
                return boundary.ConvexPlane10;
            }
            if (index == 11)
            {
                return boundary.ConvexPlane11;
            }
            if (index == 12)
            {
                return boundary.ConvexPlane12;
            }
            if (index == 13)
            {
                return boundary.ConvexPlane13;
            }
            if (index == 14)
            {
                return boundary.ConvexPlane14;
            }
            return boundary.ConvexPlane15;
        }
    }

    /// <summary>
    /// Authoring component for boid entities in the ECS system.
    /// Handles the initial setup and baking of boid properties and material overrides.
    /// </summary>
    public class BoidAuthoring : MonoBehaviour
    {
        public float DefaultCellRadius = 8.0f;
        public float DefaultSeparationWeight = 1.0f;
        public float DefaultAlignmentWeight = 1.0f;
        public float DefaultTargetWeight = 1.0f;
        public float DefaultObstacleAversionDistance = 1.0f;
        public float DefaultMoveSpeed = 0.1f;

        /// <summary>
        /// Baker class that converts the authoring MonoBehaviour into ECS components.
        /// </summary>
        class Baker : Baker<BoidAuthoring>
        {
            public override void Bake(BoidAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddSharedComponent(entity, new BoidShared
                {
                    DynamicEntityId = -1,
                    CellRadius = authoring.DefaultCellRadius,
                    SeparationWeight = authoring.DefaultSeparationWeight,
                    AlignmentWeight = authoring.DefaultAlignmentWeight,
                    TargetWeight = authoring.DefaultTargetWeight,
                    ObstacleAversionDistance = authoring.DefaultObstacleAversionDistance,
                    DefaultMoveSpeed = authoring.DefaultMoveSpeed,
                    SpeedJitterAmplitude = 0.08f,
                    SpeedJitterFrequency = 0.6f,
                    BoneAnimated = false,
                });
                AddComponent(entity, new BoidUnique
                {
                    Disabled = false,
                    MoveSpeedModifier = 1.0f,
                    TargetSpeedModifier = 1.0f,
                    MaxVerticalAngleOffset = 0.0f,
                    TargetVector = new float3(0,0,0),
                    PreviousHeading = new float3(0,0,0),
                });
                AddComponent(entity, new BoidSchoolMember
                {
                    SchoolEntity = Entity.Null,
                    SchoolIndex = -1,
                    DynamicEntityId = -1,
                    BoidSchoolId = -1,
                });
                AddComponent(entity, new BoidSpawnPending());
                AddComponent(entity, new LODState
                {
                    CurrentLOD = 0
                });
                SetComponentEnabled<BoidSpawnPending>(entity, false);

                // Material overrides
                AddComponent(entity, new ScreenDisplayStartOverride { Value = new float4(0, 0, 0, 0) });
                AddComponent(entity, new ScreenDisplayEndOverride { Value = new float4(0, 0, 0, 0) });
                AddComponent(entity, new MetalnessOverride { Value = 0.0f });
                AddComponent(entity, new AnimationRandomOffsetOverride { Value = 0.0f });
                AddComponent(entity, new AnimationSpeedOverride { Value = 1.0f });
                AddComponent(entity, new SineWavelengthOverride { Value = 1.0f });
                AddComponent(entity, new SineDeformationAmplitudeOverride { Value = new float3(0, 0, 0) });
                AddComponent(entity, new Secondary1AnimationAmplitudeOverride { Value = 0.0f });
                AddComponent(entity, new InvertSecondary1AnimationOverride { Value = 0.0f });
                AddComponent(entity, new Secondary2AnimationAmplitudeOverride { Value = new float3(0, 0, 0) });
                AddComponent(entity, new InvertSecondary2AnimationOverride { Value = 0.0f });
                AddComponent(entity, new SideToSideAmplitudeOverride { Value = new float3(0, 0, 0) });
                AddComponent(entity, new YawAmplitudeOverride { Value = new float3(0, 0, 0) });
                AddComponent(entity, new RollingSpineAmplitudeOverride { Value = new float3(0, 0, 0) });
                AddComponent(entity, new CurrentVectorOverride { Value = new float3(0, 0, 0) });
                AddComponent(entity, new AccumulatedTimeOverride { Value = 0f });
                AddComponent(entity, new MeshZMinOverride { Value = 0f });
                AddComponent(entity, new MeshZMaxOverride { Value = 0f });
                AddComponent(entity, new PositiveYClipOverride { Value = 0f });
                AddComponent(entity, new NegativeYClipOverride { Value = 0f });

                // Add CullingComponent (overwritten per school at spawn time)
                AddComponent(entity, new CullingComponent { MaxDistance = 0.0f });
            }
        }
    }

    /// <summary>
    /// Shared component containing settings that apply to an entire group of boids.
    /// </summary>
    [Serializable]
    [WriteGroup(typeof(LocalToWorld))]
    public struct BoidShared : ISharedComponentData
    {
        /// <summary>
        /// Unique identifier for the dynamic entity group this boid belongs to
        /// </summary>
        public int DynamicEntityId;
        
        /// <summary>
        /// Identifier for the school/group this boid belongs to
        /// </summary>
        public int BoidSchoolId;
        
        /// <summary>
        /// Radius used for spatial partitioning and neighbor detection
        /// </summary>
        public float CellRadius;
        
        /// <summary>
        /// Maximum bounds for boid movement
        /// </summary>
        public float3 BoundsMax;
        
        /// <summary>
        /// Minimum bounds for boid movement
        /// </summary>
        public float3 BoundsMin;

        /// <summary>
        /// Weight factor controlling how strongly boids separate from nearby neighbors
        /// </summary>
        public float SeparationWeight;

        /// <summary>
        /// Weight factor controlling how strongly boids align their movement with neighbors
        /// </summary>
        public float AlignmentWeight;

        /// <summary>
        /// Weight factor controlling how strongly boids move toward their target position
        /// </summary>
        public float TargetWeight;

        /// <summary>
        /// Distance at which boids start avoiding obstacles in their path
        /// </summary>
        public float ObstacleAversionDistance;

        /// <summary>
        /// Base movement speed when no modifiers are applied
        /// </summary>
        public float DefaultMoveSpeed;

        /// <summary>
        /// Base animation speed when no modifiers are applied
        /// </summary>
        public float DefaultAnimationSpeed;

        /// <summary>
        /// Maximum angle in degrees that boids can pitch up or down
        /// </summary>
        public float MaxVerticalAngle;

        /// <summary>
        /// Maximum rate at which boids can turn, with 1.0 being the default turn rate
        /// </summary>
        public float MaxTurnRate;

        /// <summary>
        /// If true, boid will maintain minimum distance from seabed
        /// </summary>
        public bool SeabedBound;

        /// <summary>
        /// Marks this boid as a predator, affecting its interaction with prey boids
        /// </summary>
        public bool Predator;

        /// <summary>
        /// Marks this boid as prey, affecting its interaction with predator boids
        /// </summary>
        public bool Prey;
        
        /// <summary>
        /// Speed at which boids transition between different behavioral states
        /// </summary>
        public float StateTransitionSpeed;

        /// <summary>
        /// Minimum time before a boid can change its behavioral state
        /// </summary>
        public float StateChangeTimerMin;

        /// <summary>
        /// Maximum time before a boid must change its behavioral state
        /// </summary>
        public float StateChangeTimerMax;

        /// <summary>
        /// If true, this boid uses bone-based animation instead of shader-based animation
        /// </summary>
        public bool BoneAnimated;

        /// <summary>
        /// Number of LOD levels available for this boid type
        /// </summary>
        public int NumberOfLODs;

        /// <summary>
        /// Minimum speed modifier for boid movement.
        /// </summary>
        public float SpeedModifierMin;

        /// <summary>
        /// Maximum speed modifier for boid movement.
        /// </summary>
        public float SpeedModifierMax;

        /// <summary>
        /// Amplitude for deterministic speed oscillation around the target speed.
        /// </summary>
        public float SpeedJitterAmplitude;

        /// <summary>
        /// Frequency of deterministic speed oscillation in radians per second.
        /// </summary>
        public float SpeedJitterFrequency;

        // Base mesh size info (from source mesh, no per-instance scaling)
        public float3 MeshSize;
        public float MeshLargestDimension;

        
    }
    
    /// <summary>
    /// Component containing per-boid instance data and behavior settings.
    /// </summary>
    [Serializable]
    public struct BoidUnique : IComponentData, IEnableableComponent
    {
        /// <summary>
        /// Whether this boid's behavior is currently disabled
        /// </summary>
        public bool Disabled;
        
        /// <summary>
        /// Current modifier affecting both movement and animation speed
        /// </summary>
        public float MoveSpeedModifier;
        
        /// <summary>
        /// Target speed modifier that MoveSpeedModifier will lerp towards
        /// </summary>
        public float TargetSpeedModifier;

        /// <summary>
        /// Static per-boid pitch limit variation in degrees.
        /// </summary>
        public float MaxVerticalAngleOffset;
        
        /// <summary>
        /// Target direction vector set by BoidSystem each frame
        /// </summary>
        public float3 TargetVector;
        
        /// <summary>
        /// Previous frame's heading vector, used for calculating the next TargetVector
        /// </summary>
        public float3 PreviousHeading;

            /// <summary>
            /// Smoothed reference heading used only for stable bend calculation.
            /// </summary>
            public float3 BendRefHeading;
    }    

    /// <summary>
    /// Lightweight per-boid ownership key used for unmanaged queries and school-level operations.
    /// SchoolEntity and SchoolIndex are the primary runtime keys. The legacy identifiers remain for compatibility paths.
    /// </summary>
    [Serializable]
    public struct BoidSchoolMember : IComponentData
    {
        public Entity SchoolEntity;
        public int SchoolIndex;
        public int DynamicEntityId;
        public int BoidSchoolId;
    }

    /// <summary>
    /// School-owned runtime data used by boid systems so school-wide settings are not copied onto every boid instance.
    /// </summary>
    [Serializable]
    public struct BoidSchoolRuntimeData : IComponentData
    {
        public int DynamicEntityId;
        public int BoidSchoolId;
        public int SchoolIndex;
        public Entity Target;
        public float3 BoundsCenter;
        public float3 BoundsMin;
        public float3 BoundsMax;
        public BoidBoundaryData Boundary;
        public float SeparationWeight;
        public float AlignmentWeight;
        public float TargetWeight;
        public float ObstacleAversionDistance;
        public float DefaultMoveSpeed;
        public float DefaultAnimationSpeed;
        public float MaxVerticalAngle;
        public float MaxTurnRate;
        public bool SeabedBound;
        public bool Predator;
        public bool Prey;
        public float WaterCurrentInfluence;
        public float CellRadius;
        public float StateTransitionSpeed;
        public float StateChangeTimerMin;
        public float StateChangeTimerMax;
        public bool BoneAnimated;
        public int NumberOfLODs;
        public float SpeedModifierMin;
        public float SpeedModifierMax;
        public float SpeedJitterAmplitude;
        public float SpeedJitterFrequency;
        public float SpawnClustering;
        public float ScaleMin;
        public float ScaleMax;
        public float MeshLargestDimension;
        public float CullingMaxDistance;
        public int ViewsCount;
        public float4 ViewVisibilityPercentages;
        public float AnimationSpeed;
        public float SineWavelength;
        public float3 SineDeformationAmplitude;
        public float Secondary1AnimationAmplitude;
        public float InvertSecondary1Animation;
        public float3 Secondary2AnimationAmplitude;
        public float InvertSecondary2Animation;
        public float3 SideToSideAmplitude;
        public float3 YawAmplitude;
        public float3 RollingSpineAmplitude;
        public float MeshZMin;
        public float MeshZMax;
        public float PositiveYClip;
        public float NegativeYClip;
    }

    /// <summary>
    /// Per-school cached spawn prototype containing all school-constant boid setup.
    /// </summary>
    [Serializable]
    public struct BoidSchoolSpawnPrototype : IComponentData
    {
        public Entity Value;
    }

    /// <summary>
    /// Buffer of boids currently owned by a school entity.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct BoidSchoolOwnedBoid : IBufferElementData
    {
        public Entity Value;
    }

    /// <summary>
    /// Tag for open-water boids so the steering query does not pay for seabed code paths.
    /// </summary>
    [Serializable]
    public struct OpenWaterBoidTag : IComponentData
    {
    }

    /// <summary>
    /// Tag for seabed-bound boids so the steering query can use the terrain-surface path directly.
    /// </summary>
    [Serializable]
    public struct SeabedBoidTag : IComponentData
    {
    }

    /// <summary>
    /// Temporary state for boids that have been instantiated but not yet initialized for simulation.
    /// </summary>
    [Serializable]
    public struct BoidSpawnPending : IComponentData, IEnableableComponent
    {
    }

    /// <summary>
    /// Shared terrain surface data used by seabed-bound boids for fast height and normal sampling.
    /// </summary>
    public struct SeabedSurfaceData : IComponentData
    {
        public float TerrainSizeX;
        public float TerrainSizeY;
        public float TerrainSizeZ;
        public float TerrainOffsetX;
        public float TerrainOffsetY;
        public float TerrainOffsetZ;
        public BlobAssetReference<FloatBlob> HeightmapDataBlobRef;
        public BlobAssetReference<Float3Blob> NormalDataBlobRef;
        public int HeightmapWidth;
        public int HeightmapHeight;
    }

    /// <summary>
    /// Blob asset for storing float3 arrays such as precomputed terrain normals.
    /// </summary>
    public struct Float3Blob
    {
        public BlobArray<float3> Values;
    }

    public static class SeabedSurfaceUtility
    {
        public static void SampleSurface(in SeabedSurfaceData surfaceData, float3 worldPosition, out float3 snappedPosition, out float3 surfaceNormal)
        {
            float normalizedX = (worldPosition.x - surfaceData.TerrainOffsetX) / surfaceData.TerrainSizeX;
            float normalizedZ = (worldPosition.z - surfaceData.TerrainOffsetZ) / surfaceData.TerrainSizeZ;
            normalizedX = math.clamp(normalizedX, 0.0f, 1.0f);
            normalizedZ = math.clamp(normalizedZ, 0.0f, 1.0f);

            float normalizedHeight = SampleHeight(surfaceData, normalizedX, normalizedZ);
            float3 normal = SampleNormal(surfaceData, normalizedX, normalizedZ);
            snappedPosition = new float3(
                worldPosition.x,
                surfaceData.TerrainOffsetY + (normalizedHeight * surfaceData.TerrainSizeY),
                worldPosition.z);
            surfaceNormal = math.normalizesafe(normal, math.up());
        }

        public static quaternion AlignForwardToSurface(float3 heading, float3 surfaceNormal, float3 fallbackHeading)
        {
            float3 normal = math.normalizesafe(surfaceNormal, math.up());
            float3 tangentHeading = ProjectOntoSurface(heading, normal);
            if (math.lengthsq(tangentHeading) < 0.0001f)
            {
                tangentHeading = ProjectOntoSurface(fallbackHeading, normal);
            }
            if (math.lengthsq(tangentHeading) < 0.0001f)
            {
                tangentHeading = math.cross(normal, new float3(1.0f, 0.0f, 0.0f));
            }
            if (math.lengthsq(tangentHeading) < 0.0001f)
            {
                tangentHeading = math.cross(normal, new float3(0.0f, 0.0f, 1.0f));
            }

            tangentHeading = math.normalizesafe(tangentHeading, new float3(0.0f, 0.0f, 1.0f));
            return quaternion.LookRotationSafe(tangentHeading, normal);
        }

        public static float3 ProjectOntoSurface(float3 heading, float3 surfaceNormal)
        {
            float3 normal = math.normalizesafe(surfaceNormal, math.up());
            float3 projectedHeading = heading - (normal * math.dot(heading, normal));
            return math.normalizesafe(projectedHeading, new float3(0.0f, 0.0f, 1.0f));
        }

        public static float SampleHeight(in SeabedSurfaceData surfaceData, float normalizedX, float normalizedZ)
        {
            normalizedX = math.clamp(normalizedX, 0.0f, 1.0f);
            normalizedZ = math.clamp(normalizedZ, 0.0f, 1.0f);

            ref FloatBlob heightmapBlob = ref surfaceData.HeightmapDataBlobRef.Value;
            ref BlobArray<float> heightmapValues = ref heightmapBlob.Values;

            float heightmapX = normalizedX * (surfaceData.HeightmapWidth - 1);
            float heightmapZ = normalizedZ * (surfaceData.HeightmapHeight - 1);

            int x0 = (int)math.floor(heightmapX);
            int z0 = (int)math.floor(heightmapZ);
            int x1 = math.min(x0 + 1, surfaceData.HeightmapWidth - 1);
            int z1 = math.min(z0 + 1, surfaceData.HeightmapHeight - 1);

            float tx = heightmapX - x0;
            float tz = heightmapZ - z0;

            float h00 = SampleHeightPoint(ref heightmapValues, surfaceData.HeightmapWidth, surfaceData.HeightmapHeight, x0, z0);
            float h01 = SampleHeightPoint(ref heightmapValues, surfaceData.HeightmapWidth, surfaceData.HeightmapHeight, x0, z1);
            float h10 = SampleHeightPoint(ref heightmapValues, surfaceData.HeightmapWidth, surfaceData.HeightmapHeight, x1, z0);
            float h11 = SampleHeightPoint(ref heightmapValues, surfaceData.HeightmapWidth, surfaceData.HeightmapHeight, x1, z1);

            float h0 = math.lerp(h00, h10, tx);
            float h1 = math.lerp(h01, h11, tx);
            return math.lerp(h0, h1, tz);
        }

        public static float3 SampleNormal(in SeabedSurfaceData surfaceData, float normalizedX, float normalizedZ)
        {
            normalizedX = math.clamp(normalizedX, 0.0f, 1.0f);
            normalizedZ = math.clamp(normalizedZ, 0.0f, 1.0f);

            ref Float3Blob normalBlob = ref surfaceData.NormalDataBlobRef.Value;
            ref BlobArray<float3> normalValues = ref normalBlob.Values;

            float normalX = normalizedX * (surfaceData.HeightmapWidth - 1);
            float normalZ = normalizedZ * (surfaceData.HeightmapHeight - 1);

            int x0 = (int)math.floor(normalX);
            int z0 = (int)math.floor(normalZ);
            int x1 = math.min(x0 + 1, surfaceData.HeightmapWidth - 1);
            int z1 = math.min(z0 + 1, surfaceData.HeightmapHeight - 1);

            float tx = normalX - x0;
            float tz = normalZ - z0;

            float3 n00 = SampleNormalPoint(ref normalValues, surfaceData.HeightmapWidth, surfaceData.HeightmapHeight, x0, z0);
            float3 n01 = SampleNormalPoint(ref normalValues, surfaceData.HeightmapWidth, surfaceData.HeightmapHeight, x0, z1);
            float3 n10 = SampleNormalPoint(ref normalValues, surfaceData.HeightmapWidth, surfaceData.HeightmapHeight, x1, z0);
            float3 n11 = SampleNormalPoint(ref normalValues, surfaceData.HeightmapWidth, surfaceData.HeightmapHeight, x1, z1);

            float3 n0 = math.lerp(n00, n10, tx);
            float3 n1 = math.lerp(n01, n11, tx);
            return math.normalizesafe(math.lerp(n0, n1, tz), math.up());
        }

        private static float SampleHeightPoint(ref BlobArray<float> values, int width, int height, int x, int z)
        {
            int sampleX = math.clamp(x, 0, width - 1);
            int sampleZ = math.clamp(z, 0, height - 1);
            int index = sampleZ * width + sampleX;
            return values[index];
        }

        private static float3 SampleNormalPoint(ref BlobArray<float3> values, int width, int height, int x, int z)
        {
            int sampleX = math.clamp(x, 0, width - 1);
            int sampleZ = math.clamp(z, 0, height - 1);
            int index = sampleZ * width + sampleX;
            return values[index];
        }
    }
    
    /// <summary>
    /// Component used for querying boids by their predator status
    /// </summary>
    [Serializable]
    public struct BoidPredator : IComponentData
    {
    }
    
    /// <summary>
    /// Component used for querying boids by their prey status
    /// </summary>
    [Serializable]
    public struct BoidPrey : IComponentData
    {
    }

    /// <summary>
    /// Tag component indicating a prey boid is currently escaping from a predator
    /// </summary>
    [Serializable]
    public struct EscapingPredator : IComponentData, IEnableableComponent
    {
    }

    /// <summary>
    /// Scene-wide runtime controls for inspecting and overriding ECS LOD selection.
    /// When debug overrides are disabled, LOD systems use their production defaults.
    /// </summary>
    [Serializable]
    public struct LODDebugSettings : IComponentData
    {
        public const int AutoLOD = -1;
        public const float DefaultLOD1Distance = 30.0f;
        public const float DefaultLOD2Distance = 60.0f;

        /// <summary>
        /// If true, LOD systems use these debug settings instead of their hardcoded defaults.
        /// </summary>
        public bool DebugOverridesEnabled;

        /// <summary>
        /// -1 means automatic distance-based LOD. 0, 1, and 2 force that LOD where available.
        /// </summary>
        public int ForcedLOD;

        /// <summary>
        /// Distance at which automatic dynamic boid LOD switches from LOD0 to LOD1.
        /// </summary>
        public float LOD1Distance;

        /// <summary>
        /// Distance at which automatic dynamic boid LOD switches from LOD1 to LOD2.
        /// </summary>
        public float LOD2Distance;

        public static LODDebugSettings CreateDefault()
        {
            return new LODDebugSettings
            {
                DebugOverridesEnabled = false,
                ForcedLOD = AutoLOD,
                LOD1Distance = DefaultLOD1Distance,
                LOD2Distance = DefaultLOD2Distance
            };
        }
    }

    /// <summary>
    /// Per-rendered-entity LOD state. This keeps LOD changes inspectable and prevents
    /// systems from rewriting MaterialMeshInfo when the selected LOD did not change.
    /// </summary>
    [Serializable]
    public struct LODState : IComponentData
    {
        public int CurrentLOD;
    }

    #region Material Property Overrides

    /// <summary>
    /// Controls the screen-space display start position for the boid.
    /// Used for per-view slice visibility and view-driven shader effects.
    /// Material Property: _ScreenDisplayStart
    /// </summary>
    [MaterialProperty("_ScreenDisplayStart")]
    public struct ScreenDisplayStartOverride : IComponentData
    {
        public float4 Value;
    }

    /// <summary>
    /// Controls the screen-space display end position for the boid.
    /// Used for per-view slice visibility and view-driven shader effects.
    /// Material Property: _ScreenDisplayEnd
    /// </summary>
    [MaterialProperty("_ScreenDisplayEnd")]
    public struct ScreenDisplayEndOverride : IComponentData
    {
        public float4 Value;
    }

    /// <summary>
    /// Controls the metallic material property of the boid's shader.
    /// Material Property: _Metalness
    /// Range: 0-1
    /// </summary>
    [MaterialProperty("_Metalness")]
    public struct MetalnessOverride : IComponentData
    {
        public float Value;
    }

    /// <summary>
    /// Random offset applied to animation timing to prevent synchronized animations.
    /// Material Property: _AnimationRandomOffset
    /// </summary>
    [MaterialProperty("_AnimationRandomOffset")]
    public struct AnimationRandomOffsetOverride : IComponentData
    {
        public float Value;
    }

    /// <summary>
    /// Controls the animation speed of the boid's shader.
    /// Material Property: _AnimationSpeed
    /// </summary>
    [MaterialProperty("_AnimationSpeed")]
    public struct AnimationSpeedOverride : IComponentData
    {
        public float Value;
    }

    /// <summary>
    /// Controls the wavelength of the sine wave used in the boid's shader.
    /// Material Property: _SineWavelength
    /// </summary>
    [MaterialProperty("_SineWavelength")]
    public struct SineWavelengthOverride : IComponentData
    {
        public float Value;
    }

    /// <summary>
    /// Controls the amplitude of the sine wave used in the boid's shader.
    /// Material Property: _SineDeformationAmplitude
    /// </summary>
    [MaterialProperty("_SineDeformationAmplitude")]
    public struct SineDeformationAmplitudeOverride : IComponentData
    {
        public float3 Value;
    }

    /// <summary>
    /// Controls the amplitude of the secondary animation used in the boid's shader.
    /// Material Property: _Secondary1AnimationAmplitude
    /// </summary>
    [MaterialProperty("_Secondary1AnimationAmplitude")]
    public struct Secondary1AnimationAmplitudeOverride : IComponentData
    {
        public float Value;
    }

    /// <summary>
    /// Controls the inversion of the secondary animation used in the boid's shader.
    /// Material Property: _InvertSecondary1Animation
    /// </summary>
    [MaterialProperty("_InvertSecondary1Animation")]
    public struct InvertSecondary1AnimationOverride : IComponentData
    {
        public float Value;
    }

    /// <summary>
    /// Controls the amplitude of the secondary animation used in the boid's shader.
    /// Material Property: _Secondary2AnimationAmplitude
    /// </summary>
    [MaterialProperty("_Secondary2AnimationAmplitude")]
    public struct Secondary2AnimationAmplitudeOverride : IComponentData
    {
        public float3 Value;
    }

    /// <summary>
    /// Controls the inversion of the secondary animation used in the boid's shader.
    /// Material Property: _InvertSecondary2Animation
    /// </summary>
    [MaterialProperty("_InvertSecondary2Animation")]
    public struct InvertSecondary2AnimationOverride : IComponentData
    {
        public float Value;
    }

    /// <summary>
    /// Controls the amplitude of the side-to-side movement used in the boid's shader.
    /// Material Property: _SideToSideAmplitude
    /// </summary>
    [MaterialProperty("_SideToSideAmplitude")]
    public struct SideToSideAmplitudeOverride : IComponentData
    {
        public float3 Value;
    }

    /// <summary>
    /// Controls the amplitude of the yaw movement used in the boid's shader.
    /// Material Property: _YawAmplitude
    /// </summary>
    [MaterialProperty("_YawAmplitude")]
    public struct YawAmplitudeOverride : IComponentData
    {
        public float3 Value;
    }

    /// <summary>
    /// Controls the amplitude of the rolling spine used in the boid's shader.
    /// Material Property: _RollingSpineAmplitude
    /// </summary>
    [MaterialProperty("_RollingSpineAmplitude")]
    public struct RollingSpineAmplitudeOverride : IComponentData
    {
        public float3 Value;
    }    
    
    /// <summary>
    /// Controls the current vector used in the boid's shader.
    /// Material Property: _CurrentVector
    /// </summary>
    [MaterialProperty("_CurrentVector")]
    public struct CurrentVectorOverride : IComponentData
    {
        public float3 Value;
    }

    /// <summary>
    /// Controls the accumulated time used in the boid's shader. AccumulatedTime is used to animate the boid's shader.
    /// Material Property: _AccumulatedTime
    /// </summary>
    [MaterialProperty("_AccumulatedTime")]
    public struct AccumulatedTimeOverride : IComponentData
    {
        public float Value;
    }
    
    /// <summary>
    /// Controls the minimum Z value of the mesh used in the boid's shader.
    /// Material Property: _MeshZMin
    /// </summary>
    [MaterialProperty("_MeshZMin")]
    public struct MeshZMinOverride : IComponentData
    {
        public float Value;
    }
    
    /// <summary>
    /// Controls the maximum Z value of the mesh used in the boid's shader.
    /// Material Property: _MeshZMax
    /// </summary>
    [MaterialProperty("_MeshZMax")]
    public struct MeshZMaxOverride : IComponentData
    {
        public float Value;
    }
    
    /// <summary>
    /// Controls the positive Y clip value used in the boid's shader.
    /// Material Property: _PositiveYClip
    /// </summary>
    [MaterialProperty("_PositiveYClip")]
    public struct PositiveYClipOverride : IComponentData
    {
        public float Value;
    }
    
    /// <summary>
    /// Controls the negative Y clip value used in the boid's shader.
    /// Material Property: _NegativeYClip
    /// </summary>
    [MaterialProperty("_NegativeYClip")]
    public struct NegativeYClipOverride : IComponentData
    {
        public float Value;
    }

    #endregion

}
