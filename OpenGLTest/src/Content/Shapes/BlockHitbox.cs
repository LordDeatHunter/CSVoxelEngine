using System.Numerics;

namespace VoxelEngine.Content.Shapes;

/// <summary>
/// One axis-aligned selection box in cell-local 0..1 space. A block lists one or more of
/// these in its JSON <c>"hitbox"</c> field (a single <c>{min, max}</c> object or an array
/// of them); omitted means the full cube. The hover outline draws every box, the face
/// highlight draws the hit one, and the raycast only targets a cell when the ray actually
/// enters one of its boxes.
/// </summary>
public readonly record struct BlockHitbox(Vector3 Min, Vector3 Max)
{
    /// <summary>The default: the whole cell.</summary>
    public static readonly BlockHitbox FullCube = new(Vector3.Zero, Vector3.One);

    /// <summary>Cell-local size (<see cref="Max"/> minus <see cref="Min"/>).</summary>
    public Vector3 Size => Max - Min;

    /// <summary>Cell-local center, for placing outline transforms.</summary>
    public Vector3 Center => (Min + Max) * 0.5f;
}
