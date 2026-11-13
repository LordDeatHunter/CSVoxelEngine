using VoxelEngine.Chunks;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>The jungle's mud: patches on jungle ground only, and a patch is a minority of it.</summary>
[Collection("Worldgen seed")]
public sealed class JungleMudTests
{
    private const int Height = 12;

    public JungleMudTests() => TestContent.EnsureLoaded();

    [Fact]
    public void MudPatchesExistButNeverCoverTheWholeFloor()
    {
        var mud = 0;
        var total = 0;
        for (var x = -2048; x < 2048; x += 8)
        for (var z = -2048; z < 2048; z += 8)
        {
            total++;
            if (JungleMud.IsMud(x, z)) mud++;
        }

        Assert.InRange(mud, 1, total / 2);
    }

    [Fact]
    public void OnlyAJungleColumnOnAMudPatchTopsWithMud()
    {
        int mudX = 0, mudZ = 0, dryX = 0, dryZ = 0;
        var foundMud = false;
        var foundDry = false;
        for (var x = 0; x < 2048 && !(foundMud && foundDry); x++)
        {
            if (!foundMud && JungleMud.IsMud(x, 5)) (foundMud, mudX, mudZ) = (true, x, 5);
            if (!foundDry && !JungleMud.IsMud(x, 5)) (foundDry, dryX, dryZ) = (true, x, 5);
        }

        Assert.True(foundMud && foundDry);
        Assert.Equal(JungleMud.MudBlock, TopOf(Biome.Jungle, mudX, mudZ));
        Assert.Equal(TerrainGenerator.SurfaceBlock, TopOf(Biome.Jungle, dryX, dryZ));
        foreach (var biome in new[] { Biome.Forest, Biome.Savanna, Biome.Steppe, Biome.Taiga })
            Assert.NotEqual(JungleMud.MudBlock, TopOf(biome, mudX, mudZ));
    }

    // The top block of one filled column at world (x, z): the column's own chunk, lowest block row
    private static ushort TopOf(Biome biome, int worldX, int worldZ)
    {
        var chunkX = MathUtils.FloorDiv(worldX, Chunk.Size);
        var chunkZ = MathUtils.FloorDiv(worldZ, Chunk.Size);
        var x = worldX - chunkX * Chunk.Size;
        var z = worldZ - chunkZ * Chunk.Size;
        var blocks = new ushort[Chunk.Size * Chunk.Size * Chunk.Size];
        ColumnCache.GenerateColumn(blocks, chunkX, chunkZ, x, z, Height, Height, 3, 0, biome);
        return blocks[Chunk.Idx(x, Height, z)];
    }
}