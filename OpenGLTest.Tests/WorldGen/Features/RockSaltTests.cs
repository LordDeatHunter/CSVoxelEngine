using System;
using System.Linq;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// Rock salt: only in bare surface stone (the deep sea floor, soil-stripped columns), only in the top few cells, only
/// over stone (a carved cave stays open), the stamp equal to the point query, and some of it really generated on the
/// sea floor near spawn, so the fleshing beam's salt can be had.
/// </summary>
[Collection("Worldgen seed")]
public sealed class RockSaltTests
{
    private const int Height = 6;

    public RockSaltTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    // A chunk column at a fixed height whose soil is stripped in every other column, forest throughout
    private static ColumnData Column(Func<int, byte> dirtDepth)
    {
        var count = Chunk.Size * Chunk.Size;
        return new ColumnData(Enumerable.Repeat(Height, count).ToArray(),
            Enumerable.Range(0, count).Select(dirtDepth).ToArray(), new float[count], new float[count]);
    }

    [Fact]
    public void TheStampEqualsThePointQueryAndReplacesOnlyStone()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var column = Column(i => (byte)(i % 2 == 0 ? 0 : 2));
        var blocks = new ushort[Chunk.Size * Chunk.Size * Chunk.Size];
        var salted = 0;
        for (var x0 = 0; x0 < 256; x0 += Chunk.Size)
        for (var z0 = 0; z0 < 256; z0 += Chunk.Size)
        {
            Array.Fill(blocks, stone);
            // A cell at the very top of a bare column that is not stone (a cave, a boulder) is left alone
            blocks[Chunk.Idx(0, Height, 0)] = dirt;
            RockSaltGenerator.Stamp(blocks, column, x0, 0, z0);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var got = blocks[Chunk.Idx(x, y, z)];
                if (x == 0 && y == Height && z == 0) { Assert.Equal(dirt, got); continue; }
                var i = (x << Chunk.SizeShift) | z;
                var salt = RockSaltGenerator.HasSalt(x0 + x, y, z0 + z, Biome.Forest, Height, column.DirtDepth[i]);
                Assert.Equal(salt ? RockSaltGenerator.SaltBlock : stone, got);
                if (salt) salted++;
            }
        }
        Assert.True(salted > 0);
    }

    [Fact]
    public void SaltLiesOnlyInTheTopCellsOfBareStone()
    {
        for (var x = 0; x < 256; x++)
        for (var z = 0; z < 256; z++)
        {
            // A column with soil on top never has salt, at any depth
            for (var y = Height - WorldGenerationConfig.RockSaltDepth - 2; y <= Height + 1; y++)
                Assert.False(RockSaltGenerator.HasSalt(x, y, z, Biome.Forest, Height, 2));
            // Nor above the surface, nor deeper than its depth
            Assert.False(RockSaltGenerator.HasSalt(x, Height + 1, z, Biome.Forest, Height, 0));
            Assert.False(RockSaltGenerator.HasSalt(x, Height - WorldGenerationConfig.RockSaltDepth, z, Biome.Forest,
                Height, 0));
        }
    }

    [Fact]
    public void TheDeepSeaFloorIsBareStoneButTheSandAndGravelShelvesAreNot()
    {
        var deep = WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanGravelDepth - 1;
        var gravel = WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanGravelDepth;
        var sand = WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanSandDepth;
        Assert.True(RockSaltGenerator.IsSaltColumn(Biome.Ocean, deep, 2));
        Assert.False(RockSaltGenerator.IsSaltColumn(Biome.Ocean, gravel, 2));
        Assert.False(RockSaltGenerator.IsSaltColumn(Biome.Ocean, sand, 2));
        // Land at the same depth with its soil is no salt column; stripped of it, any biome is
        Assert.False(RockSaltGenerator.IsSaltColumn(Biome.Forest, deep, 2));
        Assert.True(RockSaltGenerator.IsSaltColumn(Biome.Forest, deep, 0));
    }

    [Fact]
    public void SaltIsGeneratedOnTheSeaFloorWithinReachOfSpawn()
    {
        // The nearest generated salt column: a spiral of chunk columns out from the origin, stopping at the first
        for (var ring = 0; ring <= 48; ring++)
        for (var cx = -ring; cx <= ring; cx++)
        for (var cz = -ring; cz <= ring; cz++)
        {
            if (Math.Max(Math.Abs(cx), Math.Abs(cz)) != ring) continue;
            var column = ColumnCache.Get(cx, cz);
            for (var i = 0; i < column.Heights.Length; i++)
            {
                if (column.Biomes[i] != Biome.Ocean
                    || !RockSaltGenerator.IsSaltColumn(column.Biomes[i], column.Heights[i], column.DirtDepth[i]))
                    continue;
                var (x, z) = (cx * Chunk.Size + (i >> Chunk.SizeShift), cz * Chunk.Size + (i & (Chunk.Size - 1)));
                if (RockSaltGenerator.InPatch(x, z)) return;
            }
        }
        Assert.Fail("no rock salt on the sea floor within 48 chunks of spawn");
    }
}
