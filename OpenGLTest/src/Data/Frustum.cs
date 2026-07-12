using System.Numerics;

namespace silkgltriangle;

public readonly struct Frustum
{
    // Individual fields instead of a Plane[] so constructing a frustum every frame is allocation-free
    private readonly Plane _left, _right, _bottom, _top, _near, _far;

    public Frustum(Matrix4x4 m)
    {
        _left = MakePlane(
            m.M14 + m.M11,
            m.M24 + m.M21,
            m.M34 + m.M31,
            m.M44 + m.M41
        );

        _right = MakePlane(
            m.M14 - m.M11,
            m.M24 - m.M21,
            m.M34 - m.M31,
            m.M44 - m.M41
        );

        _bottom = MakePlane(
            m.M14 + m.M12,
            m.M24 + m.M22,
            m.M34 + m.M32,
            m.M44 + m.M42
        );

        _top = MakePlane(
            m.M14 - m.M12,
            m.M24 - m.M22,
            m.M34 - m.M32,
            m.M44 - m.M42
        );

        _near = MakePlane(
            m.M13,
            m.M23,
            m.M33,
            m.M43
        );

        _far = MakePlane(
            m.M14 - m.M13,
            m.M24 - m.M23,
            m.M34 - m.M33,
            m.M44 - m.M43
        );
    }

    private static Plane MakePlane(float a, float b, float c, float d)
    {
        var plane = new Plane(a, b, c, d);
        plane.Normalize();
        return plane;
    }

    public bool IntersectsAABB(Vector3 min, Vector3 max) =>
        InFront(in _left, min, max) &&
        InFront(in _right, min, max) &&
        InFront(in _bottom, min, max) &&
        InFront(in _top, min, max) &&
        InFront(in _near, min, max) &&
        InFront(in _far, min, max);

    private static bool InFront(in Plane plane, Vector3 min, Vector3 max)
    {
        // Test the AABB corner furthest along the plane normal
        var p = new Vector3(
            plane.Normal.X >= 0 ? max.X : min.X,
            plane.Normal.Y >= 0 ? max.Y : min.Y,
            plane.Normal.Z >= 0 ? max.Z : min.Z
        );

        return plane.DistanceToPoint(p) >= 0;
    }
}
