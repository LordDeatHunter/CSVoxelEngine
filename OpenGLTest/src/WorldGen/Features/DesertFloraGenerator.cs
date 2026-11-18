using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>Pure seeded small cactus and desert flower scatter on generated Dynamic desert sand.</summary>
public static class DesertFloraGenerator
{
    private const int ClumpSalt = 0x44464C52; // DFLR
    private const int PlantSalt = 0x4446504C; // DFPL
    private const int BlossomSalt = 0x4446424C; // DFBL

    private static readonly PlantClumpSpec Clumps = new(
        WorldFeatureConfig.DesertFloraCellShift,
        WorldFeatureConfig.DesertFloraRadius,
        WorldFeatureConfig.DesertFloraJitter,
        WorldFeatureConfig.DesertFloraMinMembers,
        WorldFeatureConfig.DesertFloraClumpChance,
        WorldFeatureConfig.DesertFloraEdgeDensity,
        ClumpSalt);
    private static readonly Func<int, int, bool> DesertCenter = static (x, z) =>
        TerrainGenerator.FloraBiomeAt(x, z) == Biome.Desert;

    public static readonly ushort SmallCactusBlock = ContentRegistry.RequireBlock("small_cactus").Id;
    public static readonly ushort CactusFlowerBlock = ContentRegistry.RequireBlock("cactus_flower").Id;
    public static readonly ushort DesertFlowerBlock = ContentRegistry.RequireBlock("desert_flower").Id;
    private static readonly ushort SandBlock = ContentRegistry.RequireBlock("sand").Id;

    /// <summary>Candidate kind before the actual generated biome, ground, air and water checks.</summary>
    internal static ushort CandidateAt(int x, int z)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic || TerrainErg.Intersects(x, z, x, z))
            return BlockType.Air;
        if (!PlantClumps.TryGetMember(Clumps, WorldGenerationConfig.Seed, x, z, DesertCenter, out _, out _))
            return BlockType.Air;

        var retention = TerrainErg.Retention(x, z);
        var roll = SplitMix64.Unit(SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ PlantSalt), 0);
        if (roll < WorldFeatureConfig.SmallCactusChance * retention)
            return SmallCactusBlock;
        if (roll < (WorldFeatureConfig.SmallCactusChance + WorldFeatureConfig.DesertFlowerChance) * retention)
            return DesertFlowerBlock;
        return BlockType.Air;
    }

    /// <summary>Seeded blossom decision; the stamp still requires a real cactus beneath it.</summary>
    internal static bool BlossomsAt(int x, int z) =>
        SplitMix64.Unit(SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ BlossomSalt), 0)
        < WorldFeatureConfig.CactusFlowerChance * TerrainErg.Retention(x, z);

    /// <summary>Writes only into local air over actual dry Desert sand; a blossom needs its newly
    /// written cactus in this same y chunk, so no upper flower can float across a chunk boundary.</summary>
    public static void Stamp(ushort[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic) return;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var columnIndex = (x << Chunk.SizeShift) | z;
            if (column.Biomes[columnIndex] != Biome.Desert) continue;
            var height = column.Heights[columnIndex];
            var ly = height + 1 - worldY0;
            if (ly < 1 || ly >= Chunk.Size || TerrainGenerator.IsBeachHeight(height)) continue;
            if (blocks[Chunk.Idx(x, ly - 1, z)] != SandBlock || blocks[Chunk.Idx(x, ly, z)] != BlockType.Air)
                continue;
            if (!SurfaceClearance.Allows(height, 1)) continue;
            var worldX = worldX0 + x;
            var worldZ = worldZ0 + z;
            var plant = CandidateAt(worldX, worldZ);
            if (plant == BlockType.Air || TerrainGenerator.IsWaterColumn(worldX, worldZ, height)) continue;

            blocks[Chunk.Idx(x, ly, z)] = plant;
            if (plant != SmallCactusBlock || ly + 1 >= Chunk.Size
                || !SurfaceClearance.Allows(height, 2)
                || blocks[Chunk.Idx(x, ly + 1, z)] != BlockType.Air)
                continue;
            if (BlossomsAt(worldX, worldZ))
                blocks[Chunk.Idx(x, ly + 1, z)] = CactusFlowerBlock;
        }
    }

    /// <summary>Both the surface plant and the compact flower immediately above a cactus.</summary>
    internal static StampBand WriteBand(ColumnData column) =>
        new(column.MinHeight + 1, column.MaxHeight + 2);
}
