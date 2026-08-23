using System;
using Unity.Entities;
using Unity.Mathematics;

namespace OceanViz3
{
    public enum EntityHoverKind : byte
    {
        Dynamic = 1,
        Static = 2
    }

    /// <summary>
    /// Compact group reference for static instances, which do not already have the boid school member component.
    /// </summary>
    public struct StaticEntityHoverMember : IComponentData
    {
        public Entity GroupEntity;
    }

    /// <summary>
    /// Group-owned hover data shared through an entity reference instead of copied to every instance.
    /// Bounds are stored in the source mesh's local space and tested against each current ECS transform.
    /// </summary>
    public struct EntityHoverGroup : IComponentData
    {
        public int GroupId;
        public EntityHoverKind Kind;
        public float3 LocalBoundsCenter;
        public float3 LocalBoundsExtents;
        public float4 ViewScaleMultipliers;
    }

    public struct EntityHoverRequest : IComponentData
    {
        public float3 RayOrigin;
        public float3 RayDirection;
        public float MaximumDistance;
        public float NormalizedScreenX;
        public int ViewIndex;
        public uint Sequence;
        public bool Active;
        public bool IncludeStaticEntities;
    }

    public struct EntityHoverResult : IComponentData
    {
        public Entity Entity;
        public int GroupId;
        public EntityHoverKind Kind;
        public uint RequestSequence;
    }

    public struct EntityHoverTraversalBounds
    {
        public Entity GroupEntity;
        public float3 Minimum;
        public float3 Maximum;
    }

    public struct StaticEntityHoverCellKey : IEquatable<StaticEntityHoverCellKey>
    {
        public int SpatialKey;
        public Entity GroupEntity;

        public bool Equals(StaticEntityHoverCellKey other)
        {
            return SpatialKey == other.SpatialKey && GroupEntity == other.GroupEntity;
        }

        public override bool Equals(object other)
        {
            return other is StaticEntityHoverCellKey key && Equals(key);
        }

        public override int GetHashCode()
        {
            return (int)math.hash(new int3(SpatialKey, GroupEntity.Index, GroupEntity.Version));
        }
    }

}
