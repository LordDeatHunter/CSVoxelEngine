using System.Numerics;

namespace silkgltriangle;

public struct Frustum
{
    public Plane[] Planes;

    public Frustum(Matrix4x4 m)
    {
        Planes = new Plane[6];

        // Left
        Planes[0] = new Plane(
            m.M14 + m.M11,
            m.M24 + m.M21,
            m.M34 + m.M31,
            m.M44 + m.M41
        );

        // Right
        Planes[1] = new Plane(
            m.M14 - m.M11,
            m.M24 - m.M21,
            m.M34 - m.M31,
            m.M44 - m.M41
        );

        // Bottom
        Planes[2] = new Plane(
            m.M14 + m.M12,
            m.M24 + m.M22,
            m.M34 + m.M32,
            m.M44 + m.M42
        );

        // Top
        Planes[3] = new Plane(
            m.M14 - m.M12,
            m.M24 - m.M22,
            m.M34 - m.M32,
            m.M44 - m.M42
        );

        // Near
        Planes[4] = new Plane(
            m.M13,
            m.M23,
            m.M33,
            m.M43
        );

        // Far
        Planes[5] = new Plane(
            m.M14 - m.M13,
            m.M24 - m.M23,
            m.M34 - m.M33,
            m.M44 - m.M43
        );

        // Normalize all planes
        for (int i = 0; i < 6; i++)
            Planes[i].Normalize();
    }

    public bool IntersectsAABB(Vector3 min, Vector3 max)
    {
        foreach (var plane in Planes)
        {
            Vector3 p = new(
                plane.Normal.X >= 0 ? max.X : min.X,
                plane.Normal.Y >= 0 ? max.Y : min.Y,
                plane.Normal.Z >= 0 ? max.Z : min.Z
            );

            if (plane.DistanceToPoint(p) < 0)
                return false;
        }

        return true;
    }
}