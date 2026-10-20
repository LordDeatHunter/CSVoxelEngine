using System;
using System.Numerics;

namespace VoxelEngine.Chunks;

/// <summary>
/// The GL-free, state-free axis sweep every loose body shares: item drops, primed TNT, falling
/// blocks, thrown projectiles, goblins, slimes and mining debris. A box, given by its center and
/// half extents, moves one axis at a time in <see cref="StepSize"/> steps and stops before the
/// first step that would overlap a solid cell. Solid, not merely non-air
/// (<c>BlockType.IsSolid</c>): a falling cube drops through fluids and fragile blocks, and a primed
/// cube falls through pebbles, like the player and the item drops do. Unloaded cells read as air.
/// The player's body is not a solid box (<c>BodyCollision.BodyCollides</c>), so it keeps its own
/// sweep and shares only <see cref="StepSize"/> and <see cref="OffsetAxis"/>. Main thread.
/// </summary>
internal static class BoxSweep
{
    /// <summary>The length of one sweep step, in blocks.</summary>
    internal const float StepSize = 0.05f;

    /// <summary>Moves a box of <paramref name="halfExtents"/> from <paramref name="start"/> by <paramref name="delta"/> along <paramref name="axis"/> (0 X, 1 Y, 2 Z), stopping before the first colliding step; <paramref name="collided"/> reports whether one was hit.</summary>
    internal static Vector3 MoveOnAxis(Vector3 start, float delta, int axis, Vector3 halfExtents, ChunkManager chunks, out bool collided)
    {
        collided = false;
        if (MathF.Abs(delta) < float.Epsilon)
            return start;

        var position = start;
        var remaining = delta;

        while (MathF.Abs(remaining) > float.Epsilon)
        {
            var step = MathUtils.ClampStep(remaining, StepSize);
            var candidate = OffsetAxis(position, axis, step);

            if (IsColliding(candidate, halfExtents, chunks))
            {
                collided = true;
                break;
            }

            position = candidate;
            remaining -= step;
        }

        return position;
    }

    /// <summary>The vector with <paramref name="delta"/> added to one axis (0 X, 1 Y, 2 Z).</summary>
    internal static Vector3 OffsetAxis(Vector3 value, int axis, float delta)
    {
        return axis switch
        {
            0 => new Vector3(value.X + delta, value.Y, value.Z),
            1 => new Vector3(value.X, value.Y + delta, value.Z),
            2 => new Vector3(value.X, value.Y, value.Z + delta),
            _ => value
        };
    }

    /// <summary>Whether a box of <paramref name="halfExtents"/> centered at <paramref name="center"/> overlaps a solid cell.</summary>
    internal static bool IsColliding(Vector3 center, Vector3 halfExtents, ChunkManager chunks)
    {
        VoxelCoords.CellRange(center - halfExtents, center + halfExtents, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);

        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            // Solid, not merely non-air: see the class summary
            if (BlockType.IsSolid(chunks.GetBlockGlobal(x, y, z)))
                return true;
        }

        return false;
    }
}
