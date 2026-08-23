using OceanViz3;
using Unity.Assertions;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Defines a habitat boundary for boid movement. The attached collider provides the shape, and hardness controls how strongly boids are kept inside it.
/// </summary>
public class BoidBounds : MonoBehaviour
{
    public string HabitatName;
    [Range(0.0f, 1.0f)]
    public float Hardness = 0.1f;
    public Collider SourceCollider;

    public BoidBoundaryData BuildBoundaryData()
    {
        Collider sourceCollider = ResolveSourceCollider();
        Assert.IsTrue(Hardness >= 0.0f && Hardness <= 1.0f, "BoidBounds hardness must be in [0, 1].");

        BoidBoundaryData boundaryData = new BoidBoundaryData
        {
            Hardness = Mathf.Clamp01(Hardness),
            BoundsCenter = sourceCollider.bounds.center,
            BoundsMin = sourceCollider.bounds.min,
            BoundsMax = sourceCollider.bounds.max,
            LocalToWorld = ToFloat4x4(sourceCollider.transform.localToWorldMatrix),
            WorldToLocal = ToFloat4x4(sourceCollider.transform.worldToLocalMatrix),
            CapsuleAxis = 1
        };

        if (sourceCollider is BoxCollider boxCollider)
        {
            boundaryData.ShapeType = BoidBoundaryShapeType.Box;
            boundaryData.LocalCenter = boxCollider.center;
            boundaryData.LocalExtents = boxCollider.size * 0.5f;
        }
        else if (sourceCollider is SphereCollider sphereCollider)
        {
            boundaryData.ShapeType = BoidBoundaryShapeType.Sphere;
            boundaryData.LocalCenter = sphereCollider.center;
            boundaryData.LocalExtents = new float3(sphereCollider.radius, sphereCollider.radius, sphereCollider.radius);
        }
        else if (sourceCollider is CapsuleCollider capsuleCollider)
        {
            boundaryData.ShapeType = BoidBoundaryShapeType.Capsule;
            boundaryData.LocalCenter = capsuleCollider.center;
            boundaryData.CapsuleRadius = capsuleCollider.radius;
            boundaryData.CapsuleHeight = capsuleCollider.height;
            boundaryData.CapsuleAxis = capsuleCollider.direction;
        }
        else if (sourceCollider is MeshCollider meshCollider)
        {
            Assert.IsTrue(meshCollider.convex, "BoidBounds MeshCollider must be convex.");
            Assert.IsTrue(meshCollider.sharedMesh != null, "BoidBounds MeshCollider requires a mesh.");
            boundaryData.ShapeType = BoidBoundaryShapeType.ConvexMesh;
            boundaryData.LocalCenter = meshCollider.sharedMesh.bounds.center;
            boundaryData.LocalExtents = meshCollider.sharedMesh.bounds.extents;
            FillConvexPlanes(meshCollider.sharedMesh, ref boundaryData);
        }
        else
        {
            Assert.IsTrue(false, "BoidBounds requires BoxCollider, SphereCollider, CapsuleCollider, or convex MeshCollider.");
        }

        return boundaryData;
    }

    private Collider ResolveSourceCollider()
    {
        if (SourceCollider != null)
        {
            return SourceCollider;
        }

        Collider sourceCollider = GetComponent<Collider>();
        Assert.IsTrue(sourceCollider != null, "BoidBounds requires a collider on the same GameObject or SourceCollider.");
        SourceCollider = sourceCollider;
        return sourceCollider;
    }

    private static float4x4 ToFloat4x4(Matrix4x4 matrix)
    {
        return new float4x4(
            new float4(matrix.m00, matrix.m10, matrix.m20, matrix.m30),
            new float4(matrix.m01, matrix.m11, matrix.m21, matrix.m31),
            new float4(matrix.m02, matrix.m12, matrix.m22, matrix.m32),
            new float4(matrix.m03, matrix.m13, matrix.m23, matrix.m33));
    }

    private static void FillConvexPlanes(Mesh mesh, ref BoidBoundaryData boundaryData)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        float3 center = mesh.bounds.center;

        for (int i = 0; i < triangles.Length; i += 3)
        {
            float3 a = vertices[triangles[i]];
            float3 b = vertices[triangles[i + 1]];
            float3 c = vertices[triangles[i + 2]];
            float3 normal = math.normalizesafe(math.cross(b - a, c - a));
            if (math.lengthsq(normal) < 0.0001f)
            {
                continue;
            }

            if (math.dot(normal, center - a) > 0.0f)
            {
                normal = -normal;
            }

            float d = -math.dot(normal, a);
            float4 plane = new float4(normal, d);
            if (ContainsSimilarPlane(boundaryData, plane))
            {
                continue;
            }

            Assert.IsTrue(boundaryData.ConvexPlaneCount < BoidBoundaryData.MaxConvexPlanes, "Convex BoidBounds mesh has too many planes. Simplify the collider mesh.");
            if (boundaryData.ConvexPlaneCount < BoidBoundaryData.MaxConvexPlanes)
            {
                SetConvexPlane(ref boundaryData, boundaryData.ConvexPlaneCount, plane);
                boundaryData.ConvexPlaneCount++;
            }
        }

        Assert.IsTrue(boundaryData.ConvexPlaneCount > 0, "Convex BoidBounds mesh produced no boundary planes.");
    }

    private static bool ContainsSimilarPlane(BoidBoundaryData boundaryData, float4 plane)
    {
        for (int i = 0; i < boundaryData.ConvexPlaneCount; i++)
        {
            float4 existing = BoidBoundaryUtility.GetConvexPlane(boundaryData, i);
            if (math.abs(math.dot(existing.xyz, plane.xyz)) > 0.999f && math.abs(existing.w - plane.w) < 0.01f)
            {
                return true;
            }
        }

        return false;
    }

    private static void SetConvexPlane(ref BoidBoundaryData boundaryData, int index, float4 plane)
    {
        if (index == 0)
        {
            boundaryData.ConvexPlane0 = plane;
        }
        else if (index == 1)
        {
            boundaryData.ConvexPlane1 = plane;
        }
        else if (index == 2)
        {
            boundaryData.ConvexPlane2 = plane;
        }
        else if (index == 3)
        {
            boundaryData.ConvexPlane3 = plane;
        }
        else if (index == 4)
        {
            boundaryData.ConvexPlane4 = plane;
        }
        else if (index == 5)
        {
            boundaryData.ConvexPlane5 = plane;
        }
        else if (index == 6)
        {
            boundaryData.ConvexPlane6 = plane;
        }
        else if (index == 7)
        {
            boundaryData.ConvexPlane7 = plane;
        }
        else if (index == 8)
        {
            boundaryData.ConvexPlane8 = plane;
        }
        else if (index == 9)
        {
            boundaryData.ConvexPlane9 = plane;
        }
        else if (index == 10)
        {
            boundaryData.ConvexPlane10 = plane;
        }
        else if (index == 11)
        {
            boundaryData.ConvexPlane11 = plane;
        }
        else if (index == 12)
        {
            boundaryData.ConvexPlane12 = plane;
        }
        else if (index == 13)
        {
            boundaryData.ConvexPlane13 = plane;
        }
        else if (index == 14)
        {
            boundaryData.ConvexPlane14 = plane;
        }
        else if (index == 15)
        {
            boundaryData.ConvexPlane15 = plane;
        }
    }
}
