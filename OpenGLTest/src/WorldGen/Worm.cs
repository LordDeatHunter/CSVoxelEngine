using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// A worm-carved tunnel: its node spheres plus the bounding box enclosing all of them, so a
/// chunk can reject a whole worm before testing individual nodes.
/// </summary>
public sealed record Worm(CaveNode[] Nodes, Vector3 Min, Vector3 Max)
{
    public bool Intersects(Vector3 boxMin, Vector3 boxMax) =>
        Min.X <= boxMax.X && Max.X >= boxMin.X &&
        Min.Y <= boxMax.Y && Max.Y >= boxMin.Y &&
        Min.Z <= boxMax.Z && Max.Z >= boxMin.Z;
}
