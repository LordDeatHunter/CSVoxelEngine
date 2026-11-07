using System;
using System.Linq;
using VoxelEngine.WorldGen.Features.Underground;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features.Underground;

/// <summary>
/// Tin veins: inside their Y band only (point query and stamp alike), stone-hosted, and commoner
/// than malachite where the bands overlap.
/// </summary>
[Collection("Worldgen seed")]
public sealed class TinTests
{
    public TinTests() => TestContent.EnsureLoaded();

    [Fact]
    public void StampOnlyReplacesStoneAndEqualsThePointQuery()
    {
        var ore = TinGenerator.OreBlock;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        var oreCells = 0;
        for (var x0 = 0; x0 < 128; x0 += Chunk.Size)
        for (var z0 = 0; z0 < 128; z0 += Chunk.Size)
        {
            Array.Fill(blocks, stone);
            blocks[Chunk.Idx(1, 1, 1)] = dirt;
            TinGenerator.Stamp(blocks, x0, -32, z0);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var got = blocks[Chunk.Idx(x, y, z)];
                if (x == 1 && y == 1 && z == 1) { Assert.Equal(dirt, got); continue; }
                Assert.Equal(TinGenerator.HasOre(x0 + x, -32 + y, z0 + z) ? ore : stone, got);
                if (got == ore) oreCells++;
            }
        }
        Assert.True(oreCells > 0);
    }

    [Fact]
    public void NoTinOutsideItsBand()
    {
        for (var x = 0; x < 64; x++)
        for (var z = 0; z < 64; z++)
        {
            Assert.False(TinGenerator.HasOre(x, WorldGenerationConfig.TinMaxY + 1, z));
            Assert.False(TinGenerator.HasOre(x, WorldGenerationConfig.TinMinY - 1, z));
        }
    }

    [Fact]
    public void TinIsCommonerThanMalachite()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var tin = OreScan.StampedCells(TinGenerator.Stamp, stone, TinGenerator.OreBlock);
        var malachite = OreScan.StampedCells(MalachiteGenerator.Stamp, stone, MalachiteGenerator.OreBlock);
        Assert.True(tin.Count > malachite.Count, $"tin {tin.Count} vs malachite {malachite.Count}");
    }

    [Fact]
    public void TheVeinsFollowTheWorldSeed()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
            var first = Cells();
            WorldSeed.Apply(WorldSeed.Default + 7);
            Assert.False(first.SetEquals(Cells()));
            WorldSeed.Apply(WorldSeed.Default);
            Assert.True(first.SetEquals(Cells()));
            Assert.NotEmpty(first);
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    private static System.Collections.Generic.HashSet<(int, int, int)> Cells()
    {
        var set = new System.Collections.Generic.HashSet<(int, int, int)>();
        for (var x = 0; x < 64; x++)
        for (var z = 0; z < 64; z++)
        for (var y = -32; y < 0; y++)
            if (TinGenerator.HasOre(x, y, z)) set.Add((x, y, z));
        return set;
    }
}
