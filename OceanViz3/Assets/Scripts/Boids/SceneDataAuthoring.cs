using System;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;

namespace OceanViz3
{
    /// <summary>
    /// Authoring component for storing and managing scene-wide configuration data.
    /// This component allows setting up global scene parameters that need to be accessible
    /// across multiple systems in the ECS architecture.
    /// </summary>
    public class SceneDataAuthoring : MonoBehaviour
    {
        /// <summary>
        /// The position for the main camera in the scene.
        /// This value will be converted to a SceneData component during entity conversion.
        /// </summary>
        public float3 CameraPosition;

        [Header("Culling (Size-Aware)")]
        public float CullingStartMeshSize = 1.0f;
        public float CullingStartDistance = 70.0f;
        public float CullingEndMeshSize = 10.0f;
        public float CullingEndDistance = 200.0f;

        class Baker : Baker<SceneDataAuthoring>
        {
            public override void Bake(SceneDataAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new SceneData
                {
                    CameraPosition = authoring.CameraPosition,
                    CullingStartMeshSize = authoring.CullingStartMeshSize,
                    CullingStartDistance = authoring.CullingStartDistance,
                    CullingEndMeshSize = authoring.CullingEndMeshSize,
                    CullingEndDistance = authoring.CullingEndDistance
                });
            }
        }
    }

    /// <summary>
    /// Component that stores scene-wide configuration data in the ECS world.
    /// Systems can query for this component to access global scene parameters.
    /// </summary>
    public struct SceneData : IComponentData
    {
        /// <summary>
        /// The position for the main camera in the scene
        /// </summary>
        public float3 CameraPosition;

        /// <summary>
        /// Size-aware culling mapping parameters (sampled at spawn time).
        /// </summary>
        public float CullingStartMeshSize;
        public float CullingStartDistance;
        public float CullingEndMeshSize;
        public float CullingEndDistance;
    }
}
