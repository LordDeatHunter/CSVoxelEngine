using System;
using silkgltriangle.Content;

namespace silkgltriangle;

/// <summary>
/// Gravel and sand pockets: blobs of loose rock buried inside stone, so mining into one can bring
/// a small collapse down (both are <c>falls</c> blocks, which <see cref="FallingBlockManager"/>
/// topples). A low-frequency 3D noise decides the blobs and a per-cell hash picks gravel or sand,
/// so the pockets are a pure function of world position and neighbors agree without shared state.
/// Only stone within a depth band under the surface is replaced — soil, ore and rock caps are left
/// alone. Runs on a generation worker thread after the carvers, like <see cref="LavaGenerator"/>.
/// </summary>
public static class GravelPocketGenerator
{
    private const int MaterialSalt = 0x6B1E77;

    private static FastNoiseLite PocketNoise;

    /// <summary>Blocks this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte GravelBlock;
    public static readonly byte SandBlock;

    private static readonly byte StoneBlock;

    static GravelPocketGenerator()
    {
        Reseed();
        GravelBlock = ContentRegistry.RequireBlock("gravel").Id;
        SandBlock = ContentRegistry.RequireBlock("sand").Id;
        StoneBlock = ContentRegistry.RequireBlock("stone").Id;
    }

    /// <summary>
    /// Builds the noise from the current <see cref="WorldGenerationConfig.Seed"/>: once from the
    /// static constructor, and again by <see cref="WorldSeed.Apply"/> while no worker thread runs.
    /// </summary>
    internal static void Reseed() => PocketNoise = TerrainGenerator.MakeNoise(93, WorldGenerationConfig.GravelPocketFrequency, octaves: 2);

    /// <summary>
    /// Whether the stone cell (x, y, z) is inside a pocket, buried at least
    /// <see cref="WorldGenerationConfig.GravelPocketMinBurial"/> under the column's surface.
    /// </summary>
    public static bool HasPocket(int x, int y, int z, int surfaceHeight)
    {
        if (y > surfaceHeight - WorldGenerationConfig.GravelPocketMinBurial) return false;
        return PocketNoise.GetNoise(x, y, z) > WorldGenerationConfig.GravelPocketThreshold;
    }

    /// <summary>
    /// Replaces stone inside a pocket with gravel or sand: a pure function of the cell, so the
    /// same world always has the same pockets wherever a chunk is generated.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var surfaceHeight = column.Heights[(x << Chunk.SizeShift) | z];
            for (var ly = 0; ly < Chunk.Size; ly++)
            {
                var y = worldY0 + ly;
                if (y > surfaceHeight - WorldGenerationConfig.GravelPocketMinBurial) continue;
                var i = Chunk.Idx(x, ly, z);
                if (blocks[i] != StoneBlock) continue;
                if (!HasPocket(worldX0 + x, y, worldZ0 + z, surfaceHeight)) continue;
                blocks[i] = UnitHash(worldX0 + x, y, worldZ0 + z) < WorldGenerationConfig.GravelPocketSandShare
                    ? SandBlock
                    : GravelBlock;
            }
        }
    }

    private static float UnitHash(int x, int y, int z)
    {
        var h = SplitMix64.Hash(x ^ (y * 0x9E37), z, WorldGenerationConfig.Seed ^ MaterialSalt);
        return (SplitMix64.Mix(h + 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));
    }
}
