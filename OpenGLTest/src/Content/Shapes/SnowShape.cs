using System;
using System.Numerics;
using VoxelEngine.WorldGen.Features;

namespace VoxelEngine.Content.Shapes;

/// <summary>
/// Shape and stack rules for a layered snow cell. The chunk state byte holds 1..<c>StackMax</c>
/// layers (0 is a bare worldgen cell), and each layer is 1/<c>StackMax</c> of a block tall, so a
/// full stack is a full cube. A bare cell derives its depth from its position like the pebble
/// piles do (<see cref="SnowGenerator.DepthAt"/>). The boxes here back the outline, break overlay,
/// mining pop and the raycast, and a full stack is solid while partial layers are walked through
/// (see <see cref="BlockType.IsSolidCell"/>).
/// </summary>
public static class SnowShape
{
    /// <summary>Layers in the cell (x, y, z): an explicit state 1..<paramref name="max"/>, else the
    /// column's natural depth at that height, clamped into range.</summary>
    public static int CountWithState(int x, int y, int z, byte state, int max)
    {
        var cap = max < 1 ? 1 : max;
        if (state >= 1 && state <= cap) return state;
        return Math.Clamp(SnowGenerator.DepthAt(x, z, y), 1, cap);
    }

    /// <summary>Height of a stack in blocks: 1/<paramref name="max"/> for one layer up to 1.</summary>
    public static float Height(int count, int max) =>
        max <= 0 ? 1f : Math.Clamp(count, 1, max) / (float)max;

    /// <summary>Boxes covering the drawn layers: a full cube when the stack fills the cell, a
    /// floor-sitting slab of its height otherwise.</summary>
    public static BlockHitbox[] GetBoxes(int x, int y, int z, byte state, int max)
    {
        if (max < 2) return [BlockHitbox.FullCube];
        var height = Height(CountWithState(x, y, z, state, max), max);
        return height >= 1f
            ? [BlockHitbox.FullCube]
            : [new BlockHitbox(Vector3.Zero, new Vector3(1f, height, 1f))];
    }
}
