using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free world light query for entity draws (drops, pops, debris, the player, ...), read
/// from the light snapshot each chunk publishes with its mesh (<see cref="Chunk.TryGetLocalLight"/>).
/// </summary>
public static class EntityLight
{
    /// <summary>
    /// Light at the cell holding <paramref name="world"/>. A cell that passes light answers with
    /// its own levels. An entity's anchor may sit inside a solid cell, though (a pop's own block,
    /// a part clipped into a wall), whose flood level is 0, so an opaque cell instead takes the
    /// per-channel maximum over its six face neighbours that are loaded and pass light, and the
    /// colour slot of the neighbour with the most block light (the lowest slot on a tie, so the
    /// answer does not depend on the order the neighbours are visited in). Anything
    /// unknown (no manager, unloaded chunk, no published snapshot) reads as
    /// <see cref="EntityLightSample.Default"/>, the light every entity drew with before.
    /// </summary>
    public static EntityLightSample Sample(ChunkManager? manager, Vector3 world)
    {
        if (manager == null) return EntityLightSample.Default;
        var (x, y, z) = world.BlockCell();
        var chunk = manager.ChunkAt(x, y, z);
        if (chunk == null) return EntityLightSample.Default;

        var id = chunk.GetLocalBlock(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask);
        if (BlockType.PassesLightById[id])
            return Read(chunk, x, y, z);

        var found = false;
        float sun = 0f, block = 0f;
        byte slot = 0;
        foreach (var (dx, dy, dz) in Vector3Directions.CardinalOffsets)
        {
            int nx = x + dx, ny = y + dy, nz = z + dz;
            // Most neighbours share the owning chunk; only a border step pays a lookup.
            var owner = (nx >> Chunk.SizeShift) == (x >> Chunk.SizeShift)
                        && (ny >> Chunk.SizeShift) == (y >> Chunk.SizeShift)
                        && (nz >> Chunk.SizeShift) == (z >> Chunk.SizeShift)
                ? chunk
                : manager.ChunkAt(nx, ny, nz);
            if (owner == null) continue;
            var nid = owner.GetLocalBlock(nx & Chunk.SizeMask, ny & Chunk.SizeMask, nz & Chunk.SizeMask);
            if (!BlockType.PassesLightById[nid]) continue;

            var s = Read(owner, nx, ny, nz);
            sun = MathF.Max(sun, s.Sun);
            if (s.Block > block || (s.Block == block && s.Tint < slot))
                slot = s.Tint;
            block = MathF.Max(block, s.Block);
            found = true;
        }

        return found ? new EntityLightSample(sun, block, slot) : EntityLightSample.Default;
    }

    private static EntityLightSample Read(Chunk chunk, int x, int y, int z) =>
        chunk.TryGetLocalLight(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask,
            out var sun, out var block, out var slot)
            ? new EntityLightSample(sun / 15f, block / 15f, slot)
            : EntityLightSample.Default;
}
