using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Tests.Support;
using VoxelEngine.WorldGen;
using Xunit;
using static VoxelEngine.Chunks.Chunk;

namespace VoxelEngine.Tests.Chunks;

[Collection("Worldgen seed")]
public sealed class ColumnCacheGenerationTests
{
    private static readonly Biome[] Biomes =
    [
        Biome.Forest, Biome.Desert, Biome.Ocean, Biome.Taiga,
        Biome.Frozen, Biome.Steppe, Biome.Savanna, (Biome)255
    ];

    [Fact]
    public void ColumnFillMatchesPinnedLegacyAcrossBiomeAndDepthBoundaries()
    {
        TestContent.EnsureLoaded();
        var soilDepths = new byte[] { 0, 1, 2, 5, 8, 9, 255 };
        var offsets = new[] { -2, -1, 0, 1, 2, 4, 5, 8, 9, 14, 15, 16, 17, 40 };
        var floors = new[] { -112, -16, 0, 16 };
        var caseIndex = 0;

        foreach (var biome in Biomes)
        foreach (var dirtDepth in soilDepths)
        foreach (var floor in floors)
        foreach (var offset in offsets)
        {
            var lowerBound = floor;
            var height = lowerBound + offset;
            var chunkX = -19 + caseIndex % 7;
            var chunkZ = -23 - caseIndex % 5;
            AssertEquivalent(chunkX, chunkZ, 13, 2, height, height - 11, dirtDepth, lowerBound, biome);
            caseIndex++;
        }
    }

    [Fact]
    public void ColumnFillMatchesLegacyForUncheckedIntegerEdgesAndDifferentBed()
    {
        TestContent.EnsureLoaded();
        var cases = new (int Height, int Bed, int Floor, byte Dirt, Biome Biome)[]
        {
            (int.MinValue, int.MaxValue, int.MaxValue, 1, Biome.Forest),
            (int.MinValue, int.MinValue, int.MaxValue, 255, Biome.Ocean),
            (int.MaxValue, int.MinValue, int.MaxValue, 255, Biome.Desert),
            (int.MaxValue, int.MinValue, int.MinValue, 0, Biome.Frozen),
            (WorldGenerationConfig.SeaLevel, int.MinValue, WorldGenerationConfig.SeaLevel, 5, Biome.Forest),
            (WorldGenerationConfig.SeaLevel, int.MaxValue, WorldGenerationConfig.SeaLevel - 16, 9, Biome.Forest),
            (WorldGenerationConfig.SeaLevel + 30, int.MinValue, WorldGenerationConfig.SeaLevel, 255, (Biome)255)
        };

        foreach (var item in cases)
            AssertEquivalent(-7, -11, 3, 14, item.Height, item.Bed, item.Dirt, item.Floor, item.Biome);
    }

    [Fact]
    public void BeachWaterAndGravelCasesMatchLegacyAndAreNotVacuous()
    {
        TestContent.EnsureLoaded();
        var originalSeed = WorldSeed.Default;
        var foundPositiveGravel = false;
        var foundPositiveSand = false;
        var foundZeroWeight = false;
        var foundWaterTop = false;
        var testedPositions = new List<(int X, int Z)>();

        try
        {
            foreach (var seed in new[] { 1337, 424242, -90210 })
            {
                WorldSeed.Apply(seed);
                for (var x = -1024; x <= 1024 && !(foundPositiveGravel && foundPositiveSand && foundZeroWeight && foundWaterTop); x += 8)
                for (var z = -1024; z <= 1024 && !(foundPositiveGravel && foundPositiveSand && foundZeroWeight && foundWaterTop); z += 8)
                {
                    var height = WorldGenerationConfig.SeaLevel;
                    var weight = TerrainGenerator.SurfaceWaterWeight(x, z);
                    var gravel = TerrainGenerator.IsGravelPatch(x, z);
                    if (weight > 0f && (gravel ? !foundPositiveGravel : !foundPositiveSand))
                    {
                        if (gravel) foundPositiveGravel = true;
                        else foundPositiveSand = true;
                        testedPositions.Add((x, z));
                        AssertEquivalent(x >> SizeShift, z >> SizeShift, x & SizeMask, z & SizeMask,
                            height, height - 7, 5, height - 8, Biome.Forest);
                    }
                    else if (weight == 0f && !foundZeroWeight)
                    {
                        foundZeroWeight = true;
                        testedPositions.Add((x, z));
                        AssertEquivalent(x >> SizeShift, z >> SizeShift, x & SizeMask, z & SizeMask,
                            height, height - 7, 5, height - 8, Biome.Forest);
                    }

                    var nonBeachWaterHeight = WorldGenerationConfig.SeaLevel
                                              - WorldGenerationConfig.BeachBelowSeaLevel - 1;
                    if (!foundWaterTop && !TerrainGenerator.IsBeachHeight(nonBeachWaterHeight)
                        && TerrainGenerator.IsWaterColumn(x, z, nonBeachWaterHeight - 1))
                    {
                        foundWaterTop = true;
                        AssertEquivalent(x >> SizeShift, z >> SizeShift, x & SizeMask, z & SizeMask,
                            nonBeachWaterHeight, nonBeachWaterHeight - 1, 5, nonBeachWaterHeight - 1, Biome.Forest);
                    }
                }
            }
        }
        finally
        {
            WorldSeed.Apply(originalSeed);
        }

        Assert.True(foundPositiveGravel, "The bounded scan did not find a positive-weight gravel beach.");
        Assert.True(foundPositiveSand, "The bounded scan did not find a positive-weight sand beach.");
        Assert.True(foundZeroWeight, "The bounded scan did not find a zero-weight beach.");
        Assert.True(foundWaterTop, "The bounded scan did not find a wet top outside the beach band.");
        Assert.NotEmpty(testedPositions);
    }

    private static void AssertEquivalent(int chunkX, int chunkZ, int x, int z,
        int height, int bed, byte dirtDepth, int lowerBoundY, Biome biome)
    {
        var expected = Enumerable.Repeat((byte)0xA5, Size * Size * Size).ToArray();
        var actual = (byte[])expected.Clone();
        LegacyGenerateColumn(expected, chunkX, chunkZ, x, z, height, bed, dirtDepth, lowerBoundY, biome);
        ColumnCache.GenerateColumn(actual, chunkX, chunkZ, x, z, height, bed, dirtDepth, lowerBoundY, biome);
        Assert.Equal(expected, actual);
    }

    // Frozen copy of the pre-optimization implementation. This is the behavior oracle; the
    // candidate's predicates are deliberately not reproduced here.
    private static void LegacyGenerateColumn(byte[] blocks, int chunkX, int chunkZ, int x, int z,
        int terrainHeight, int bed, int dirtDepth, int lowerBoundY, Biome biome)
    {
        var worldX = chunkX * Size + x;
        var worldZ = chunkZ * Size + z;
        var topBlock = TerrainGenerator.IsWaterColumn(worldX, worldZ, bed)
            ? TerrainGenerator.SoilBlock
            : TerrainGenerator.SurfaceBlock;
        var soilBlock = TerrainGenerator.SoilBlock;
        var soilDepth = dirtDepth;

        switch (biome)
        {
            case Biome.Desert:
                topBlock = TerrainGenerator.SandBlock;
                soilBlock = TerrainGenerator.SandstoneBlock;
                soilDepth = dirtDepth == 0 ? 0 : WorldGenerationConfig.SandstoneDepth;
                break;
            case Biome.Ocean:
                (topBlock, soilBlock) = terrainHeight >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanSandDepth
                    ? (TerrainGenerator.SandBlock, TerrainGenerator.SandBlock)
                    : terrainHeight >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanGravelDepth
                        ? (TerrainGenerator.GravelBlock, TerrainGenerator.GravelBlock)
                        : (TerrainGenerator.RockBlock, TerrainGenerator.RockBlock);
                soilDepth = 2;
                break;
            case Biome.Frozen:
                topBlock = TerrainGenerator.PermafrostBlock;
                soilBlock = TerrainGenerator.PermafrostBlock;
                soilDepth = dirtDepth == 0 ? 0 : 2;
                break;
            default:
                if (dirtDepth != 0 && TerrainGenerator.IsBeachHeight(terrainHeight)
                    && TerrainGenerator.SurfaceWaterWeight(worldX, worldZ) > 0f)
                {
                    topBlock = TerrainGenerator.IsGravelPatch(worldX, worldZ)
                        ? TerrainGenerator.GravelBlock
                        : TerrainGenerator.SandBlock;
                    soilBlock = TerrainGenerator.SandBlock;
                }
                break;
        }

        for (var y = 0; y < Size; y++)
        {
            var depth = terrainHeight - (lowerBoundY + y);
            if (depth < 0) break;

            blocks[Idx(x, y, z)] = soilDepth == 0 ? TerrainGenerator.RockBlock
                : depth == 0 ? topBlock
                : depth <= soilDepth ? soilBlock
                : TerrainGenerator.RockBlock;
        }
    }
}
