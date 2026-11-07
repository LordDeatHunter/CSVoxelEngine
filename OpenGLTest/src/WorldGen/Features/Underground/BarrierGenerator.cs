using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>
/// The barrier band between the overworld and hell (plans/LAYERS.md): a layer of the barrier block,
/// which only the iron pickaxe mines, over world y <see cref="WorldGenerationConfig.BarrierBottomY"/>..
/// <see cref="WorldGenerationConfig.BarrierTopY"/>. Its core
/// (<see cref="WorldGenerationConfig.BarrierFullBottomY"/>..<see cref="WorldGenerationConfig.BarrierFullTopY"/>)
/// is gapless: every cell there becomes barrier, carved air, water and lava included, so no cave lets
/// a player through. Over the two ramps the barrier's share (<see cref="ShareAt"/>) rises linearly
/// from 0 at the band's edge to 1 at the core, and a cell is barrier where a coherent 3D noise,
/// mapped to 0..1, is under that share, so the edges break into patches instead of flat planes; a
/// ramp cell only replaces solid, occluding ground (<see cref="IsHost"/>) and leaves caves open.
/// A pure function of world position and seed: <see cref="IsBarrier"/> is the field the stamp
/// writes, bit for bit across chunk seams. Stamped last of all the features, so nothing writes over it.
/// </summary>
public static class BarrierGenerator
{
    private static FastNoiseLite PatchNoise;

    /// <summary>The barrier block; resolved once in the static constructor.</summary>
    public static readonly byte BarrierBlock;

    static BarrierGenerator()
    {
        Reseed();
        BarrierBlock = ContentRegistry.RequireBlock("barrier").Id;
    }

    /// <summary>Builds the noise from the current seed (static constructor and <see cref="WorldSeed.Apply"/>).</summary>
    internal static void Reseed() =>
        PatchNoise = TerrainGenerator.MakeNoise(162, WorldGenerationConfig.BarrierPatchFrequency);

    /// <summary>Whether world y lies in the band's gapless core.</summary>
    public static bool InCore(int y) =>
        y <= WorldGenerationConfig.BarrierFullTopY && y >= WorldGenerationConfig.BarrierFullBottomY;

    /// <summary>
    /// The barrier's share of the ground at world y: 0 outside the band and at its two edges, rising
    /// linearly over each ramp to 1 in the core.
    /// </summary>
    public static float ShareAt(int y)
    {
        if (y > WorldGenerationConfig.BarrierTopY || y < WorldGenerationConfig.BarrierBottomY)
            return 0f;
        if (y > WorldGenerationConfig.BarrierFullTopY)
            return (float)(WorldGenerationConfig.BarrierTopY - y)
                   / (WorldGenerationConfig.BarrierTopY - WorldGenerationConfig.BarrierFullTopY);
        if (y >= WorldGenerationConfig.BarrierFullBottomY)
            return 1f;
        return (float)(y - WorldGenerationConfig.BarrierBottomY)
               / (WorldGenerationConfig.BarrierFullBottomY - WorldGenerationConfig.BarrierBottomY);
    }

    /// <summary>Whether the cell belongs to the barrier field: always in the core, in a ramp where
    /// the patch noise is under the share. A ramp cell is written only over a <see cref="IsHost"/>
    /// block, so on all-host ground this equals what <see cref="Stamp"/> writes.</summary>
    public static bool IsBarrier(int x, int y, int z)
    {
        if (InCore(y)) return true;
        var share = ShareAt(y);
        return share > 0f && PatchValue(x, y, z) < share;
    }

    /// <summary>Whether a ramp cell holding this block may become barrier: solid, occluding ground
    /// (stone, ore, gravel), never air, a fluid or a partial block.</summary>
    public static bool IsHost(byte block) => BlockType.IsSolid(block) && BlockType.Occludes(block);

    /// <summary>The patch noise mapped to 0..1.</summary>
    private static float PatchValue(int x, int y, int z) => 0.5f * (PatchNoise.GetNoise(x, y, z) + 1f);

    /// <summary>Writes the barrier into the chunk whose minimum corner is (worldX0, worldY0, worldZ0):
    /// every core cell, and every host cell of a ramp that <see cref="IsBarrier"/> names.</summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        var yMin = Math.Max(0, WorldGenerationConfig.BarrierBottomY - worldY0);
        var yMax = Math.Min(Chunk.Size - 1, WorldGenerationConfig.BarrierTopY - worldY0);

        for (var x = 0; x < Chunk.Size; x++)
        for (var y = yMin; y <= yMax; y++)
        {
            var wy = worldY0 + y;
            var core = InCore(wy);
            var share = ShareAt(wy);
            if (!core && share <= 0f) continue;

            for (var z = 0; z < Chunk.Size; z++)
            {
                var i = Chunk.Idx(x, y, z);
                if (core)
                {
                    blocks[i] = BarrierBlock;
                    continue;
                }

                if (IsHost(blocks[i]) && PatchValue(worldX0 + x, wy, worldZ0 + z) < share)
                    blocks[i] = BarrierBlock;
            }
        }
    }

    /// <summary>Every world y <see cref="Stamp"/> can write: the band itself.</summary>
    internal static StampBand WriteBand() =>
        new(WorldGenerationConfig.BarrierBottomY, WorldGenerationConfig.BarrierTopY);
}
