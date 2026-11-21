using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>Dynamic-only weighted ground cover on real dry grass or Jungle mud.</summary>
internal static class DynamicGroundCover
{
    private const int VariantSalt = 0x44474356; // DGCV, separate from the Legacy density roll.
    private static readonly float[] ForestWeights = [0.40f, 0.28f, 0.20f, 0.12f];
    private static readonly float[] TaigaWeights = [0.45f, 0.55f, 0f, 0f];
    private static readonly float[] JungleWeights = [0.20f, 0.35f, 0.30f, 0.15f];
    private static readonly float[] SavannaWeights = [0.85f, 0f, 0f, 0.15f];
    private static readonly float[] SteppeWeights = [0.95f, 0f, 0f, 0.05f];

    private static readonly ushort Fern = ContentRegistry.RequireBlock("fern").Id;
    private static readonly ushort TallFern = ContentRegistry.RequireBlock("tall_fern").Id;
    private static readonly ushort TallFernTop = ContentRegistry.RequireBlock("tall_fern_top").Id;
    private static readonly ushort Bush = ContentRegistry.RequireBlock("bush").Id;
    private static readonly ushort Grass = ContentRegistry.RequireBlock("grass").Id;
    private static readonly ushort JungleMud = ContentRegistry.RequireBlock("jungle_mud").Id;
    private static readonly ushort Sand = ContentRegistry.RequireBlock("sand").Id;
    private static readonly ushort Permafrost = ContentRegistry.RequireBlock("permafrost").Id;

    /// <summary>Normalize a positive biome row before drawing; zero weights cannot win.</summary>
    internal static int WeightedPick(float roll, ReadOnlySpan<float> weights)
    {
        var total = 0f;
        foreach (var weight in weights) total += weight;
        if (total <= 0f) return -1;
        var threshold = MathUtils.Clamp01(roll) * total;
        for (var i = 0; i < weights.Length; i++)
        {
            threshold -= weights[i];
            if (threshold < 0f && weights[i] > 0f) return i;
        }
        for (var i = weights.Length - 1; i >= 0; i--)
            if (weights[i] > 0f) return i;
        return -1;
    }

    private static ReadOnlySpan<float> Row(Biome biome) => biome switch
    {
        Biome.Forest => ForestWeights,
        Biome.Taiga => TaigaWeights,
        Biome.Jungle => JungleWeights,
        Biome.Savanna => SavannaWeights,
        Biome.Steppe => SteppeWeights,
        _ => ReadOnlySpan<float>.Empty,
    };

    /// <summary>Pure candidate before the real generated soil, snow, air and water gates.</summary>
    internal static ushort CandidateAt(int x, int z, Biome biome)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic) return BlockType.Air;
        var row = Row(biome);
        if (row.IsEmpty) return FoliageGenerator.GrassTuftBlock;
        var h = SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ VariantSalt);
        return WeightedPick(SplitMix64.Unit(h, 0), row) switch
        {
            1 => Fern,
            2 => TallFern,
            3 => Bush,
            _ => FoliageGenerator.GrassTuftBlock,
        };
    }

    internal static void Stamp(ushort[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var ci = (x << Chunk.SizeShift) | z;
            var height = column.Heights[ci];
            var ly = height + 1 - worldY0;
            if (ly < 1 || ly >= Chunk.Size || TerrainGenerator.IsBeachHeight(height)) continue;
            var biome = column.Biomes[ci];
            var ground = blocks[Chunk.Idx(x, ly - 1, z)];
            var i = Chunk.Idx(x, ly, z);
            var wx = worldX0 + x;
            var wz = worldZ0 + z;
            var dry = blocks[i] == BlockType.Air;
            if (ground == Sand || ground == Permafrost)
            {
                // Keep the existing Dynamic desert and Frozen shrub rule, including the
                // permafrost snow exception, while changing only grass and Jungle mud cover.
                if ((dry || ground == Permafrost && blocks[i] == TerrainGenerator.SnowBlock)
                    && FoliageGenerator.TryGetDeadShrubOn(ground, wx, wz)
                    && !TerrainGenerator.IsWaterColumn(wx, wz, height))
                    blocks[i] = FoliageGenerator.DeadShrubBlock;
                continue;
            }
            if (ground != Grass && (biome != Biome.Jungle || ground != JungleMud)) continue;
            if (!dry || !SurfaceClearance.Allows(height, 1)) continue;
            if (!FoliageGenerator.TryGetGrassTuft(wx, wz)
                || TerrainGenerator.IsWaterColumn(wx, wz, height)) continue;
            var plant = CandidateAt(wx, wz, biome);
            if (plant == TallFern)
            {
                // The top cannot be independently stamped in another y chunk.
                if (ly + 1 >= Chunk.Size || !SurfaceClearance.Allows(height, 2)
                    || blocks[Chunk.Idx(x, ly + 1, z)] != BlockType.Air)
                    plant = Fern;
                else
                    blocks[Chunk.Idx(x, ly + 1, z)] = TallFernTop;
            }
            blocks[i] = plant;
        }
    }

    internal static StampBand WriteBand(ColumnData column) =>
        new(column.MinHeight + 1, column.MaxHeight + 2);
}
