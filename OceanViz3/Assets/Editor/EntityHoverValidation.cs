using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace OceanViz3.Editor
{
    /// <summary>
    /// Fast deterministic validation for entity-hover bounds, view routing, and spatial buckets.
    /// </summary>
    public static class EntityHoverValidation
    {
        [MenuItem("Tools/OceanViz/Validate Entity Hover")]
        public static void Run()
        {
            ValidateBoundsIntersection();
            ValidateViewRouting();
            ValidateSpatialBuckets();
            Debug.Log("[EntityHoverValidation] Bounds, split-view routing, and spatial bucket checks passed.");
        }

        private static void ValidateBoundsIntersection()
        {
            bool hit = EntityHoverPickingMath.TryIntersectAabb(
                new float3(0.0f, 0.0f, -10.0f),
                new float3(0.0f, 0.0f, 1.0f),
                new float3(-1.0f),
                new float3(1.0f),
                out float hitDistance);
            Debug.Assert(hit, "Entity hover ray should hit the centered bounds.");
            Debug.Assert(
                math.abs(hitDistance - 9.0f) < 0.0001f,
                "Entity hover ray returned the wrong nearest distance.");

            bool rangeHit = EntityHoverPickingMath.TryIntersectAabbRange(
                new float3(0.0f, 0.0f, -10.0f),
                new float3(0.0f, 0.0f, 1.0f),
                new float3(-1.0f),
                new float3(1.0f),
                out float nearDistance,
                out float farDistance);
            Debug.Assert(rangeHit, "Entity hover traversal bounds should intersect.");
            Debug.Assert(
                math.abs(nearDistance - 9.0f) < 0.0001f &&
                math.abs(farDistance - 11.0f) < 0.0001f,
                "Entity hover traversal range is incorrect.");

            bool missed = EntityHoverPickingMath.TryIntersectAabb(
                new float3(3.0f, 0.0f, -10.0f),
                new float3(0.0f, 0.0f, 1.0f),
                new float3(-1.0f),
                new float3(1.0f),
                out _);
            Debug.Assert(!missed, "Entity hover ray should miss bounds outside its path.");
        }

        private static void ValidateViewRouting()
        {
            float4 values = new float4(0.5f, 1.0f, 1.5f, 2.0f);
            Debug.Assert(
                math.abs(EntityHoverPickingMath.SelectViewValue(values, 0) - 0.5f) < 0.0001f,
                "View 0 routing failed.");
            Debug.Assert(
                math.abs(EntityHoverPickingMath.SelectViewValue(values, 3) - 2.0f) < 0.0001f,
                "View 3 routing failed.");
        }

        private static void ValidateSpatialBuckets()
        {
            Debug.Assert(HoverSpatialIndexUtility.CalculateBucket(0.5f) == 0, "Small hover bucket routing failed.");
            Debug.Assert(HoverSpatialIndexUtility.CalculateBucket(2.0f) == 1, "Medium hover bucket routing failed.");
            Debug.Assert(HoverSpatialIndexUtility.CalculateBucket(10.0f) == 2, "Large hover bucket routing failed.");
            Debug.Assert(HoverSpatialIndexUtility.CalculateBucket(40.0f) == 3, "Very large hover bucket routing failed.");
            Debug.Assert(HoverSpatialIndexUtility.CalculateBucket(100.0f) == 4, "Oversized hover bucket routing failed.");

            float3 longThinBounds = new float3(0.5f, 20.0f, 0.5f);
            Debug.Assert(
                HoverSpatialIndexUtility.CalculateHorizontalFootprintBucket(longThinBounds) == 0,
                "Long thin static entities must use their horizontal footprint for far hover cells.");
        }
    }
}
