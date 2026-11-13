using System.Numerics;
using VoxelEngine.Chunks.Meshing;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

/// <summary>
/// The cave cull: a chunk's visibility set joins exactly the faces an open run of cells connects, and the walk from
/// the camera's chunk reaches a chunk only through such runs, so a cave sealed behind solid rock is never drawn while
/// anything the camera can see along open cells is.
/// </summary>
public sealed class ChunkVisibilityGraphTests
{
    public ChunkVisibilityGraphTests() => TestContent.EnsureLoaded();

    private static ushort Stone => ContentRegistry.RequireBlock("stone").Id;

    private static Chunk Solid(ChunkMap map, int x, int y, int z)
    {
        var chunk = new Chunk(null!, new Vector3(x, y, z), generateBlockData: false) { IsBlockDataReady = true };
        for (var i = 0; i < Chunk.BlockCount; i++)
        {
            var (lx, ly, lz) = (i >> 8, (i >> 4) & 15, i & 15);
            chunk.SetBlockRaw(lx, ly, lz, Stone, 0);
        }
        map[new Vector3(x, y, z)] = chunk;
        return chunk;
    }

    private static void Mesh(Chunk chunk) =>
        chunk.UpdateMesh(new ChunkMeshData([], [], [], [], false) { Visibility = ChunkVisibility.Compute(chunk.Storage) },
            null, null);

    [Fact]
    public void ATunnelJoinsOnlyTheFacesItOpensOnto()
    {
        var map = new ChunkMap();
        var chunk = Solid(map, 0, 0, 0);
        Assert.Equal(0UL, ChunkVisibility.Compute(chunk.Storage));

        // A straight tunnel along X through the middle
        for (var x = 0; x < Chunk.Size; x++)
            chunk.SetBlockRaw(x, 8, 8, BlockType.Air, 0);
        var set = ChunkVisibility.Compute(chunk.Storage);

        Assert.True(ChunkVisibility.Connected(set, 0, 1));
        Assert.True(ChunkVisibility.Connected(set, 1, 0));
        Assert.False(ChunkVisibility.Connected(set, 0, 3));
        Assert.False(ChunkVisibility.Connected(set, 2, 3));
    }

    [Fact]
    public void ACaveSealedBehindRockIsNotReachedButAnOpenOneIs()
    {
        var map = new ChunkMap();
        // Camera chunk at the origin (air, not loaded), then a solid chunk, then a hollow cave chunk past it
        var wall = Solid(map, 1, 0, 0);
        var cave = Solid(map, 2, 0, 0);
        for (var x = 2; x < 14; x++)
        for (var y = 2; y < 14; y++)
        for (var z = 2; z < 14; z++)
            cave.SetBlockRaw(x, y, z, BlockType.Air, 0);
        Solid(map, 3, 0, 0);
        Mesh(wall);
        Mesh(cave);
        Mesh(map[new Vector3(3, 0, 0)]);

        var graph = new ChunkVisibilityGraph(map);
        // Seal the camera in too, so the only way east is through the wall
        foreach (var (x, y, z) in new[] { (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
            Mesh(Solid(map, x, y, z));
        graph.Update(new Vector3(8f, 8f, 8f), 1, 1);

        Assert.True(graph.Sees(wall), "the camera's own neighbor is always reached");
        Assert.False(graph.Sees(cave), "a cave sealed behind solid rock was reached");

        // Dig through the wall: the cave opens onto the camera's chunk
        for (var x = 0; x < Chunk.Size; x++)
            wall.SetBlockRaw(x, 8, 8, BlockType.Air, 0);
        Mesh(wall);
        for (var x = 0; x < 3; x++)
            cave.SetBlockRaw(x, 8, 8, BlockType.Air, 0);
        Mesh(cave);
        graph.Update(new Vector3(8f, 8f, 8f), 1, 2);

        Assert.True(graph.Sees(cave));
    }
}
