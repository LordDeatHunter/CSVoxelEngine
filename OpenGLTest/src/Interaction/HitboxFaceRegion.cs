using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Shapes;

namespace VoxelEngine.Interaction;

/// <summary>
/// GL-free: which of a block's hitboxes share the surface the player is looking at. A shaped block (a mold with its
/// pocket, a basin) is many boxes, so highlighting only the hit box's face would light one strip of a flat top.
/// </summary>
public static class HitboxFaceRegion
{
    private const float Eps = 1e-4f;

    /// <summary>
    /// The indices of every box whose face on <paramref name="normal"/>'s side lies in the same plane as the hit box's
    /// and connects to it through boxes that do too (faces that overlap or touch edge to edge). The hit box comes
    /// first. Just the hit box for a normal that is not an axis.
    /// </summary>
    public static List<int> Connected(IReadOnlyList<BlockHitbox> boxes, int hitIndex, Vector3 normal)
    {
        var region = new List<int> { hitIndex };
        var axis = normal.X != 0 ? 0 : normal.Y != 0 ? 1 : normal.Z != 0 ? 2 : -1;
        if (axis < 0) return region;
        var plane = Plane(boxes[hitIndex], axis, normal);

        for (var next = 0; next < region.Count; next++)
        {
            var from = boxes[region[next]];
            for (var i = 0; i < boxes.Count; i++)
            {
                if (region.Contains(i) || MathF.Abs(Plane(boxes[i], axis, normal) - plane) > Eps) continue;
                if (Touches(from, boxes[i], axis)) region.Add(i);
            }
        }
        return region;
    }

    private static float Plane(BlockHitbox box, int axis, Vector3 normal) =>
        Component(normal, axis) > 0 ? Component(box.Max, axis) : Component(box.Min, axis);

    private static bool Touches(BlockHitbox a, BlockHitbox b, int axis)
    {
        for (var other = 0; other < 3; other++)
        {
            if (other == axis) continue;
            if (Component(a.Min, other) > Component(b.Max, other) + Eps
                || Component(b.Min, other) > Component(a.Max, other) + Eps)
                return false;
        }
        return true;
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}
