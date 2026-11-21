using System;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>Selected Dynamic plants must pass the real soil, air and whole-footprint stamp gates.</summary>
[Collection("Worldgen seed")]
public sealed class GroundCoverStampTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private static ushort Grass => ContentRegistry.RequireBlock("grass").Id;
    private static ushort Stone => ContentRegistry.RequireBlock("stone").Id;
    private static ushort Fern => ContentRegistry.RequireBlock("fern").Id;
    private static ushort TallFern => ContentRegistry.RequireBlock("tall_fern").Id;
    private static ushort TallFernTop => ContentRegistry.RequireBlock("tall_fern_top").Id;
    private static ushort Bush => ContentRegistry.RequireBlock("bush").Id;

    public GroundCoverStampTests(ITestOutputHelper output)
    {
        _output = output;
        TestContent.EnsureLoaded();
        WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
    }

    public void Dispose() => WorldSeed.Apply(WorldSeed.Default);

    private static (ushort[] Blocks, ColumnData Column, int LocalY) Flat(int height, Biome biome)
    {
        const int count = Chunk.Size * Chunk.Size;
        var heights = new int[count];
        var biomes = new Biome[count];
        Array.Fill(heights, height);
        Array.Fill(biomes, biome);
        var blocks = new ushort[Chunk.BlockCount];
        var groundY = height - (height & ~Chunk.SizeMask);
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            blocks[Chunk.Idx(x, groundY, z)] = Grass;
        return (blocks, new ColumnData(heights, new byte[count], new float[count], new float[count], biomes), groundY + 1);
    }

    [Fact]
    public void SelectedTallFernPairsInsideTheChunkAndFallsBackAtTheTopOrOccupiedUpperCell()
    {
        var probes = 0;
        (int X, int Z, Biome Biome)? chosen = null;
        try
        {
            // One declared 16x16 chunk column; a miss is a failed precondition, not a pass.
            for (var x = 0; x < Chunk.Size && !chosen.HasValue; x++)
            for (var z = 0; z < Chunk.Size && !chosen.HasValue; z++)
            {
                probes++;
                var biome = TerrainGenerator.FloraBiomeAt(x, z);
                if (!FoliageGenerator.TryGetGrassTuft(x, z)
                    || TerrainGenerator.IsWaterColumn(x, z, 157)
                    || DynamicGroundCover.CandidateAt(x, z, biome) != TallFern) continue;
                chosen = (x, z, biome);
            }
        }
        finally { _output.WriteLine($"tall-fern direct probes={probes}/256"); }
        Assert.True(chosen.HasValue, "fixed 16x16 Dynamic candidate window has no selected tall fern");
        var (sx, sz, selectedBiome) = chosen.Value;

        var (blocks, column, ly) = Flat(157, selectedBiome);
        DynamicGroundCover.Stamp(blocks, column, 0, 144, 0);
        Assert.Equal(TallFern, blocks[Chunk.Idx(sx, ly, sz)]);
        Assert.Equal(TallFernTop, blocks[Chunk.Idx(sx, ly + 1, sz)]);

        (blocks, column, ly) = Flat(158, selectedBiome);
        DynamicGroundCover.Stamp(blocks, column, 0, 144, 0);
        Assert.Equal(Fern, blocks[Chunk.Idx(sx, ly, sz)]);

        (blocks, column, ly) = Flat(157, selectedBiome);
        blocks[Chunk.Idx(sx, ly + 1, sz)] = Stone;
        DynamicGroundCover.Stamp(blocks, column, 0, 144, 0);
        Assert.Equal(Fern, blocks[Chunk.Idx(sx, ly, sz)]);
        Assert.Equal(Stone, blocks[Chunk.Idx(sx, ly + 1, sz)]);
    }

    [Fact]
    public void ThreeMemberBushAdmissionRejectsEverySingleInvalidMemberAndTheColumnEdge()
    {
        // Synthetic postselection input: this proves the shared production admission, not
        // the sparse natural CandidateAt selector. Local (2, 0) is core-free and dry.
        const int lx = 2, lz = 0, x0 = 0, z0 = 0;
        const Biome biome = Biome.Forest;

        for (var variant = 0; variant < 5; variant++)
        {
            var (blocks, column, ly) = Flat(157, biome);
            var member = (lx + 1, lz);
            var ci = (member.Item1 << Chunk.SizeShift) | member.Item2;
            if (variant == 1) column.Heights[ci]++;
            if (variant == 2) blocks[Chunk.Idx(member.Item1, ly - 1, member.Item2)] = Stone;
            if (variant == 3) blocks[Chunk.Idx(member.Item1, ly, member.Item2)] = Stone;
            if (variant == 4) column.Biomes[ci] = biome == Biome.Jungle ? Biome.Forest : Biome.Jungle;
            var accepted = JungleBushGenerator.TryStampFootprint(blocks, column, x0, 144, z0, lx, lz, true);
            Assert.Equal(variant == 0, accepted);
            Assert.Equal(variant == 0 ? Bush : BlockType.Air, blocks[Chunk.Idx(lx, ly, lz)]);
            Assert.Equal(variant == 0 ? Bush : (variant == 3 ? Stone : BlockType.Air), blocks[Chunk.Idx(lx + 1, ly, lz)]);
            Assert.Equal(variant == 0 ? Bush : BlockType.Air, blocks[Chunk.Idx(lx, ly, lz + 1)]);
        }

        var (edgeBlocks, edgeColumn, edgeY) = Flat(157, biome);
        Assert.False(JungleBushGenerator.TryStampFootprint(edgeBlocks, edgeColumn, x0, 144, z0,
            Chunk.SizeMask, lz, true));
        Assert.Equal(BlockType.Air, edgeBlocks[Chunk.Idx(Chunk.SizeMask, edgeY, lz)]);
    }
}
