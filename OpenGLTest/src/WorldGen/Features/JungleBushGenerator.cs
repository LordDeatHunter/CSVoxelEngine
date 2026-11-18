using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>Small horizontal bushes; every member stands on its own flat ground in one chunk column.</summary>
internal static class JungleBushGenerator
{
    private const int SiteSalt = 0x4A425348; // JBSH
    private static readonly ushort Bush = ContentRegistry.RequireBlock("bush").Id;
    private static readonly ushort Grass = ContentRegistry.RequireBlock("grass").Id;
    private static readonly ushort JungleMud = ContentRegistry.RequireBlock("jungle_mud").Id;

    /// <summary>One fixed site in each eight-block cell, with no ColumnCache dependency.</summary>
    internal static bool CandidateAt(int x, int z, out bool third)
    {
        third = false;
        if (WorldSeed.Profile != WorldGenProfile.Dynamic) return false;
        var cellX = MathUtils.FloorDiv(x, 8);
        var cellZ = MathUtils.FloorDiv(z, 8);
        var h = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ SiteSalt);
        if (x - cellX * 8 != (int)(SplitMix64.Unit(h, 0) * 8)
            || z - cellZ * 8 != (int)(SplitMix64.Unit(h, 1) * 8)) return false;
        var biome = TerrainGenerator.FloraBiomeAt(x, z);
        if (biome is not (Biome.Jungle or Biome.Forest)) return false;
        if (SplitMix64.Unit(h, 2) >= (biome == Biome.Jungle ? 0.42f : 0.06f)
            * TerrainErg.Retention(x, z)) return false;
        third = SplitMix64.Unit(h, 3) < 0.5f;
        return true;
    }

    internal static void Stamp(ushort[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic) return;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var wx = worldX0 + x;
            var wz = worldZ0 + z;
            if (!CandidateAt(wx, wz, out var third)) continue;
            // A footprint crossing the chunk edge is suppressed, never clipped.
            if (x + 1 >= Chunk.Size || (third && z + 1 >= Chunk.Size)) continue;
            if (TerrainErg.Intersects(wx, wz, wx + 1, wz + (third ? 1 : 0))) continue;
            var ci = (x << Chunk.SizeShift) | z;
            var height = column.Heights[ci];
            var ly = height + 1 - worldY0;
            if (ly < 1 || ly >= Chunk.Size || TerrainGenerator.IsBeachHeight(height)
                || !SurfaceClearance.Allows(height, 1)) continue;
            var biome = column.Biomes[ci];
            if (biome is not (Biome.Jungle or Biome.Forest)) continue;
            if (!Eligible(x, z, height, ly, biome, blocks, column, wx, wz)
                || !Eligible(x + 1, z, height, ly, biome, blocks, column, wx + 1, wz)
                || (third && !Eligible(x, z + 1, height, ly, biome, blocks, column, wx, wz + 1)))
                continue;
            blocks[Chunk.Idx(x, ly, z)] = Bush;
            blocks[Chunk.Idx(x + 1, ly, z)] = Bush;
            if (third) blocks[Chunk.Idx(x, ly, z + 1)] = Bush;
        }
    }

    private static bool Eligible(int x, int z, int height, int ly, Biome biome,
        ushort[] blocks, ColumnData column, int wx, int wz)
    {
        var ci = (x << Chunk.SizeShift) | z;
        if (column.Heights[ci] != height || column.Biomes[ci] != biome) return false;
        var ground = blocks[Chunk.Idx(x, ly - 1, z)];
        if (ground != Grass && (biome != Biome.Jungle || ground != JungleMud)) return false;
        return blocks[Chunk.Idx(x, ly, z)] == BlockType.Air
            && !TerrainGenerator.IsWaterColumn(wx, wz, height);
    }

    internal static StampBand WriteBand(ColumnData column) => StampBand.AboveSurface(column);
}
