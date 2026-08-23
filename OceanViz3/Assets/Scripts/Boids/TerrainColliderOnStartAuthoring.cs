using OceanViz3;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using Collider = UnityEngine.Collider;
using Material = Unity.Physics.Material;
using TerrainCollider = Unity.Physics.TerrainCollider;

/// <summary>
/// Creates and manages a terrain collider entity in the ECS system.
/// This component should be attached to a GameObject with a Terrain component.
/// </summary>
public class TerrainColliderOnStartAuthoring : MonoBehaviour
{
    // The entity that will hold the terrain collider
    public Entity entity;
    
    public EntityManager entityManager;

    /// <summary>
    /// Initializes the terrain collider on start.
    /// Creates a new entity with a terrain collider if one doesn't exist,
    /// or updates the existing terrain collider entity.
    /// </summary>
    public void Start()
    {
        Debug.Log("TerrainColliderAuthoring Start");
        
        // Get the EntityManager from the World
        entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;

        if (!TryGetComponent<Terrain>(out var terrain))
        {
            Debug.LogError("No terrain found!");
            return;
        }

        CollisionFilter collisionFilter = new CollisionFilter
        {
            BelongsTo = ~0u,
            CollidesWith = ~0u, // All 1s, so all layers - collide with everything
            GroupIndex = 0
        };

        TerrainData terrainData = terrain.terrainData;
        int resolution = terrainData.heightmapResolution;
        float[,] terrainHeights = terrainData.GetHeights(0, 0, resolution, resolution);

        PhysicsCollider collider = CreateTerrainCollider(terrainData, terrainHeights, collisionFilter);
        SeabedSurfaceData seabedSurfaceData = CreateSeabedSurfaceData(terrain, terrainHeights);
        
        World world = World.DefaultGameObjectInjectionWorld;
        entityManager = world.EntityManager;
        
        // Target entity
        Entity entity;
        
        // Check if the TerrainCollider component already exists
        EntityQuery query = entityManager.CreateEntityQuery(typeof(TerrainCollider));
        
        // If a singleton entity with the TerrainCollider component already exists, we use that entity
        if (query.CalculateEntityCount() > 0)
        {
            Debug.Log("TerrainCollider entity already exists, updating the collider.");

            entity = entityManager.CreateEntityQuery(typeof(TerrainCollider)).GetSingletonEntity();
            
            entityManager.SetComponentData(entity, new LocalToWorld
            {
                Value = float4x4.TRS(terrain.transform.position, terrain.transform.rotation, terrain.transform.lossyScale)
            });
            entityManager.SetComponentData(entity, collider);
            SetSeabedSurfaceData(entity, seabedSurfaceData);
        }
        // If an entity with the TerrainCollider component does not exist, we set the terrain collider on new entity
        else
        {
            Debug.Log("TerrainCollider entity does not exist yet, creating a new entity.");
            
            entity = entityManager.CreateEntity();
            entityManager.SetName(entity, "TerrainCollider");
            entityManager.AddComponent<PhysicsCollider>(entity);
            entityManager.SetComponentData(entity, collider);
            entityManager.AddComponent<PhysicsWorldIndex>(entity);
            
            entityManager.AddComponent<LocalToWorld>(entity);
            entityManager.SetComponentData(entity, new LocalToWorld
            {
                Value = float4x4.TRS(terrain.transform.position, terrain.transform.rotation, terrain.transform.lossyScale)
            });
            
            entityManager.AddComponent<TerrainCollider>(entity); // Tag component
            SetSeabedSurfaceData(entity, seabedSurfaceData);
        }
    }

    /// <summary>
    /// Creates a PhysicsCollider component for the terrain using the provided TerrainData.
    /// </summary>
    /// <param name="terrainData">The TerrainData asset containing height information</param>
    /// <param name="filter">Collision filter determining collision layers and masks</param>
    /// <returns>A PhysicsCollider component configured for the terrain</returns>
    private PhysicsCollider CreateTerrainCollider(TerrainData terrainData, float[,] terrainHeights, CollisionFilter filter)
    {
        int resolution = terrainData.heightmapResolution;
        int2 size = new int2(resolution, resolution);
        Vector3 scale = terrainData.heightmapScale;

        NativeArray<float> colliderHeights = new NativeArray<float>(resolution * resolution, Allocator.TempJob);

        for (int j = 0; j < size.y; j++)
        for (int i = 0; i < size.x; i++)
        {
            var h = terrainHeights[i, j];
            colliderHeights[j + i * size.x] = h;
        }

        PhysicsCollider physicsCollider = new PhysicsCollider
        {
            Value = Unity.Physics.TerrainCollider.Create(colliderHeights, size, scale, Unity.Physics.TerrainCollider.CollisionMethod.Triangles, filter)
        };

        colliderHeights.Dispose();

        return physicsCollider;
    }

    /// <summary>
    /// Creates the terrain surface data used by seabed-bound boids for fast height and normal sampling.
    /// </summary>
    private SeabedSurfaceData CreateSeabedSurfaceData(Terrain terrain, float[,] terrainHeights)
    {
        TerrainData terrainData = terrain.terrainData;
        int resolution = terrainData.heightmapResolution;
        BlobAssetReference<FloatBlob> heightmapBlobRef;
        BlobAssetReference<Float3Blob> normalBlobRef;

        using (var blobBuilder = new BlobBuilder(Allocator.Temp))
        {
            ref FloatBlob heightmapBlobAsset = ref blobBuilder.ConstructRoot<FloatBlob>();
            int length = resolution * resolution;
            BlobBuilderArray<float> heightmapArrayBuilder = blobBuilder.Allocate(ref heightmapBlobAsset.Values, length);
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    heightmapArrayBuilder[z * resolution + x] = terrainHeights[z, x];
                }
            }

            heightmapBlobRef = blobBuilder.CreateBlobAssetReference<FloatBlob>(Allocator.Persistent);
        }

        using (var blobBuilder = new BlobBuilder(Allocator.Temp))
        {
            ref Float3Blob normalBlobAsset = ref blobBuilder.ConstructRoot<Float3Blob>();
            int length = resolution * resolution;
            BlobBuilderArray<float3> normalArrayBuilder = blobBuilder.Allocate(ref normalBlobAsset.Values, length);
            float cellSizeX = terrainData.size.x / (resolution - 1);
            float cellSizeZ = terrainData.size.z / (resolution - 1);

            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int leftX = x - 1;
                    if (leftX < 0)
                    {
                        leftX = 0;
                    }

                    int rightX = x + 1;
                    if (rightX >= resolution)
                    {
                        rightX = resolution - 1;
                    }

                    int backZ = z - 1;
                    if (backZ < 0)
                    {
                        backZ = 0;
                    }

                    int forwardZ = z + 1;
                    if (forwardZ >= resolution)
                    {
                        forwardZ = resolution - 1;
                    }

                    float leftHeight = terrainHeights[z, leftX] * terrainData.size.y;
                    float rightHeight = terrainHeights[z, rightX] * terrainData.size.y;
                    float backHeight = terrainHeights[backZ, x] * terrainData.size.y;
                    float forwardHeight = terrainHeights[forwardZ, x] * terrainData.size.y;

                    float slopeX = (rightHeight - leftHeight) / (cellSizeX * math.max(1, rightX - leftX));
                    float slopeZ = (forwardHeight - backHeight) / (cellSizeZ * math.max(1, forwardZ - backZ));

                    normalArrayBuilder[z * resolution + x] = math.normalizesafe(new float3(-slopeX, 1.0f, -slopeZ), math.up());
                }
            }

            normalBlobRef = blobBuilder.CreateBlobAssetReference<Float3Blob>(Allocator.Persistent);
        }

        Vector3 terrainSize = terrainData.size;
        Vector3 terrainPosition = terrain.transform.position;
        return new SeabedSurfaceData
        {
            TerrainSizeX = terrainSize.x,
            TerrainSizeY = terrainSize.y,
            TerrainSizeZ = terrainSize.z,
            TerrainOffsetX = terrainPosition.x,
            TerrainOffsetY = terrainPosition.y,
            TerrainOffsetZ = terrainPosition.z,
            HeightmapDataBlobRef = heightmapBlobRef,
            NormalDataBlobRef = normalBlobRef,
            HeightmapWidth = resolution,
            HeightmapHeight = resolution
        };
    }

    private void SetSeabedSurfaceData(Entity terrainEntity, SeabedSurfaceData seabedSurfaceData)
    {
        if (entityManager.HasComponent<SeabedSurfaceData>(terrainEntity))
        {
            SeabedSurfaceData previousSurfaceData = entityManager.GetComponentData<SeabedSurfaceData>(terrainEntity);
            if (previousSurfaceData.HeightmapDataBlobRef.IsCreated)
            {
                previousSurfaceData.HeightmapDataBlobRef.Dispose();
            }
            if (previousSurfaceData.NormalDataBlobRef.IsCreated)
            {
                previousSurfaceData.NormalDataBlobRef.Dispose();
            }

            entityManager.SetComponentData(terrainEntity, seabedSurfaceData);
        }
        else
        {
            entityManager.AddComponentData(terrainEntity, seabedSurfaceData);
        }
    }
    
    /// <summary>
    /// Tag component to identify entities with terrain colliders.
    /// Used for querying and identifying terrain collider entities in the ECS world.
    /// </summary>
    public struct TerrainCollider : IComponentData
    {
    }
}
