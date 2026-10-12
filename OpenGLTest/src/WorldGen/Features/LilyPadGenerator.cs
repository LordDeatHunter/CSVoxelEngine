using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Lily pads: a flat pad stamped on the cell above a still lake's top water cell. A lake is a
/// column whose own <see cref="ColumnData.WaterLevel"/> sits above the ground and is not the sea
/// level (<see cref="TerrainGenerator.WaterLevelAt"/> gives the ocean and rivers sea level, a lake
/// its own surface), and generated water is all sources, so the surface is still by construction.
/// A pure per-column hash of (x, z) and the seed; the far LOD shows none.
/// </summary>
public static class LilyPadGenerator
{
    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte PadBlock;

    static LilyPadGenerator() => PadBlock = ContentRegistry.RequireBlock("lily_pad").Id;

    /// <summary>Whether the lake column (x, z) carries a pad, before the terrain checks.</summary>
    public static bool TryGetPad(int x, int z)
    {
        var h = SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ 0x71D5);
        return (SplitMix64.Mix(h) >> 40) * (1f / (1 << 24)) < WorldGenerationConfig.LilyPadChance;
    }

    /// <summary>
    /// Stamps pads onto lake surfaces: the cell above the top water cell of a wet, non-sea column,
    /// into air only. Runs on a generation worker thread after the water.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var i2 = (x << Chunk.SizeShift) | z;
            var waterLevel = column.WaterLevel[i2];
            // The ocean and rivers are sea level; a lake carries its own surface, above its ground
            if (waterLevel == WorldGenerationConfig.SeaLevel || waterLevel <= column.Heights[i2]) continue;
            if (!TryGetPad(worldX0 + x, worldZ0 + z)) continue;

            var ly = waterLevel - worldY0;
            if ((uint)ly >= Chunk.Size) continue;
            var i = Chunk.Idx(x, ly, z);
            if (blocks[i] != BlockType.Air) continue;
            blocks[i] = PadBlock;
        }
    }
}
