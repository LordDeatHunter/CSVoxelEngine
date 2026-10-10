using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The one shape rule of a <see cref="BlockRenderer.Pane"/> block (the glass pane), derived from
/// its four horizontal neighbours at mesh time and never stored: a 2 px centre post, plus an arm
/// reaching the cell edge toward every neighbour it connects to (<see cref="ConnectsTo"/>: another
/// pane, or a solid full cube). A lone pane is just the post, as in Minecraft. Directions are the
/// horizontal <see cref="Chunk.CubeFaces"/> indices (north -Z = 0, south +Z = 1, west -X = 2,
/// east +X = 3), and bit <c>d</c> of a mask is an arm toward direction <c>d</c>. The mesher
/// (<see cref="PaneEmitter"/>), the raycast, the outline and body collision
/// (<see cref="BlockShape"/>) all read this, so what is drawn is exactly what is hit.
/// GL-free and allocation-free.
/// </summary>
public static class PaneShape
{
    /// <summary>The post's and every arm's near and far edge across the pane, cell-local: the
    /// middle 2 of 16 pixels.</summary>
    public const float Near = 7f / 16f;

    /// <inheritdoc cref="Near"/>
    public const float Far = 9f / 16f;

    /// <summary>Number of horizontal directions, and so of arms.</summary>
    public const int Directions = 4;

    // The boxes of every mask, built once: Boxes hands out these shared arrays
    private static readonly BlockHitbox[][] BoxesByMask = BuildAllBoxes();

    /// <summary>
    /// Whether a pane reaches toward a neighbour holding <paramref name="id"/> with state byte
    /// <paramref name="state"/>: another pane, or a solid full cube - one that occludes, a plain
    /// full-cube model such as glass or leaves, or full-thickness tree wood. Air, fluids and
    /// sub-cell blocks (a torch, a flower, thin wood) get no arm.
    /// </summary>
    public static bool ConnectsTo(byte id, byte state) =>
        BlockType.IsPane(id)
        || (BlockType.IsSolid(id)
            && (BlockType.Occludes(id) || BlockType.IsGreedy(id)
                || (BlockType.IsTreeWood(id) && WoodState.IsFullCube(state))));

    /// <summary>The arm mask of a pane whose horizontal neighbours, in direction order, hold
    /// <paramref name="ids"/> with <paramref name="states"/> (at least four of each).</summary>
    public static int ConnectionMask(ReadOnlySpan<byte> ids, ReadOnlySpan<byte> states)
    {
        var mask = 0;
        for (var dir = 0; dir < Directions; dir++)
            if (ConnectsTo(ids[dir], states[dir]))
                mask |= 1 << dir;
        return mask;
    }

    /// <summary>The arm mask of the pane at world cell (x, y, z), its neighbours read through
    /// <paramref name="chunks"/> (an unloaded neighbour reads as air: no arm).</summary>
    public static int ConnectionMaskAt(ChunkManager chunks, int x, int y, int z)
    {
        var mask = 0;
        for (var dir = 0; dir < Directions; dir++)
        {
            var normal = Chunk.CubeFaces[dir].normal;
            int nx = x + (int)normal.X, nz = z + (int)normal.Z;
            if (ConnectsTo(chunks.GetBlockGlobal(nx, y, nz), chunks.GetStateGlobal(nx, y, nz)))
                mask |= 1 << dir;
        }
        return mask;
    }

    /// <summary>The full-height centre post, cell-local.</summary>
    public static void PostBox(out Vector3 min, out Vector3 max)
    {
        min = new Vector3(Near, 0f, Near);
        max = new Vector3(Far, 1f, Far);
    }

    /// <summary>The full-height arm from the post to the cell edge toward
    /// <paramref name="dir"/>, cell-local.</summary>
    public static void ArmBox(int dir, out Vector3 min, out Vector3 max)
    {
        (min, max) = dir switch
        {
            0 => (new Vector3(Near, 0f, 0f), new Vector3(Far, 1f, Near)),
            1 => (new Vector3(Near, 0f, Far), new Vector3(Far, 1f, 1f)),
            2 => (new Vector3(0f, 0f, Near), new Vector3(Near, 1f, Far)),
            _ => (new Vector3(Far, 0f, Near), new Vector3(1f, 1f, Far)),
        };
    }

    /// <summary>
    /// The selection and collision boxes of a pane with arm mask <paramref name="mask"/>: one
    /// bar along X through the post (out to the cell edge on each side with an arm), plus a
    /// north and a south stub for those arms, so the boxes never overlap and their outline traces
    /// the drawn shape. The arrays are shared; never write into them.
    /// </summary>
    public static BlockHitbox[] Boxes(int mask) => BoxesByMask[mask & 15];

    /// <summary><see cref="Boxes"/> of the pane at world cell (x, y, z).</summary>
    public static BlockHitbox[] GetBoxesAt(ChunkManager chunks, int x, int y, int z) =>
        Boxes(ConnectionMaskAt(chunks, x, y, z));

    private static BlockHitbox[][] BuildAllBoxes()
    {
        var all = new BlockHitbox[16][];
        for (var mask = 0; mask < 16; mask++)
        {
            var bar = new BlockHitbox(
                new Vector3((mask & 1 << 2) != 0 ? 0f : Near, 0f, Near),
                new Vector3((mask & 1 << 3) != 0 ? 1f : Far, 1f, Far));
            var boxes = new BlockHitbox[1 + ((mask & 1) != 0 ? 1 : 0) + ((mask & 2) != 0 ? 1 : 0)];
            var next = 0;
            boxes[next++] = bar;
            for (var dir = 0; dir < 2; dir++)
            {
                if ((mask & 1 << dir) == 0) continue;
                ArmBox(dir, out var min, out var max);
                boxes[next++] = new BlockHitbox(min, max);
            }
            all[mask] = boxes;
        }
        return all;
    }
}
