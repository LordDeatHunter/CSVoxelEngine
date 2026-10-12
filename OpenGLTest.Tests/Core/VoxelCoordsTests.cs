using System.Numerics;
using Xunit;

namespace VoxelEngine.Tests.Core;

public sealed class VoxelCoordsTests
{
    public VoxelCoordsTests() => TestContent.EnsureLoaded();

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(15, 0, 15)]
    [InlineData(16, 1, 0)]
    [InlineData(-1, -1, 15)]
    [InlineData(-16, -1, 0)]
    [InlineData(-17, -2, 15)]
    public void WorldToChunkAndLocalAgree(int world, int chunk, int local)
    {
        Assert.Equal(chunk, VoxelCoords.WorldToChunk(world));
        Assert.Equal(local, VoxelCoords.WorldToLocal(world));
    }

    [Fact]
    public void LocalIndexMatchesChunkPacking()
    {
        Assert.Equal((15 << 8) | (0 << 4) | 15, VoxelCoords.LocalIndex(15, 0, 15));
        Assert.Equal(0, VoxelCoords.LocalIndex(0, 0, 0));
    }

    [Fact]
    public void ChunkPosOfHandlesNegativePositions()
    {
        Assert.Equal(new Vector3(-1, 4, -2), VoxelCoords.ChunkPosOf(new Vector3(-0.1f, 64f, -16.2f)));
        var (bx, by, bz) = VoxelCoords.BlockCellOf(new Vector3(-0.1f, 64f, -16.2f));
        Assert.Equal((-1, 64, -17), (bx, by, bz));
        Assert.Equal((15, 0, 15), VoxelCoords.LocalOf(bx, by, bz));
    }

    [Fact]
    public void CellRangeCoversBodyPlusExtent()
    {
        VoxelCoords.CellRange(new Vector3(0.5f, 0.5f, 0.5f), 0.3f, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);
        Assert.Equal((0, 0, 0), (minX, minY, minZ));
        Assert.Equal((0, 0, 0), (maxX, maxY, maxZ));
        VoxelCoords.CellRange(new Vector3(-0.1f, 0f, 0f), 0.3f, out minX, out _, out _, out maxX, out _, out _);
        Assert.Equal(-1, minX);
        Assert.Equal(0, maxX);
    }
}
