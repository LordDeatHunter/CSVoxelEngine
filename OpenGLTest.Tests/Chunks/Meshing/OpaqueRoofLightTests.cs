using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Chunks.Meshing;

/// <summary>Real placement, removal and streamed restore of a roof above the terrain height.</summary>
public sealed class OpaqueRoofLightTests
{
    public OpaqueRoofLightTests() => TestContent.EnsureLoaded();

    [Fact]
    public void PlacingAndRemovingAnOpaqueRoofRelightsTheRoomAndNearbyChunks()
    {
        var (manager, center) = Room();
        var roofCell = center.Position * Chunk.Size + new Vector3(8, 12, 8);
        var below = center.Position + new Vector3(0, -1, 0);
        var beyond = center.Position + new Vector3(0, 2, 0);
        manager.Chunks[beyond] = Ready(beyond);
        manager.Headless = true;

        Assert.Equal(15, Sun(center.GenerateMeshData(manager)!, 8, 10, 8));
        Assert.True(center.PlaceBlockAt(roofCell, Block("oak_planks"), manager));
        Assert.True(center.TryGetLocalLight(8, 10, 8, out var covered, out _));
        Assert.True(covered < 15, "the placed plank roof still exposes the room to direct sky");
        Assert.True(manager.IsChunkDirty(ChunkKey.Pack(below)), "the chunk below was not invalidated");
        Assert.False(manager.IsChunkDirty(ChunkKey.Pack(beyond)), "invalidation passed the 15-cell radius");

        Assert.True(center.RemoveBlockAt(roofCell, manager));
        Assert.True(center.TryGetLocalLight(8, 10, 8, out var reopened, out _));
        Assert.Equal(15, reopened);
    }

    [Fact]
    public void AStreamedAndRestoredOpaqueRoofHasTheSameLightBytes()
    {
        var (manager, center) = Room();
        center.SetBlockRaw(8, 12, 8, Block("oak_planks"), 0);
        var expected = center.GenerateMeshData(manager)!.Light;
        Assert.NotNull(expected);

        var columns = new StreamedColumns();
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            var cx = (int)center.Position.X + dx;
            var cz = (int)center.Position.Z + dz;
            Assert.True(WorldgenColumnSource.Instance.TryGet(cx, cz, out var terrain));
            columns.Add(cx, cz, terrain);
        }

        var client = new ChunkManager(null!) { ColumnSource = columns };
        var saved = new ChunkManager(null!);
        foreach (var chunk in manager.Chunks.Values)
        {
            var pos = chunk.Position;
            var snapshot = chunk.TakeSnapshot(null);
            var streamed = Ready(pos);
            streamed.RestoreFrom(snapshot, streamedColumns: true);
            client.Chunks[pos] = streamed;

            var restored = Ready(pos);
            restored.RestoreFrom(snapshot, ColumnCache.Get((int)pos.X, (int)pos.Z));
            saved.Chunks[pos] = restored;
        }

        Assert.Equal(expected, client.Chunks[center.Position].GenerateMeshData(client)!.Light);
        Assert.Equal(expected, saved.Chunks[center.Position].GenerateMeshData(saved)!.Light);
    }

    private static (ChunkManager Manager, Chunk Center) Room()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = Block("stone");
        var planks = Block("oak_planks");
        for (var x = 3; x <= 11; x++)
        for (var z = 3; z <= 11; z++)
        {
            center.SetBlockRaw(x, 4, z, stone, 0);
            if (x is 3 or 11 || z is 3 or 11)
                for (var y = 5; y < 12; y++) center.SetBlockRaw(x, y, z, planks, 0);
            if (x != 8 || z != 8) center.SetBlockRaw(x, 12, z, planks, 0);
        }
        return (manager, center);
    }

    private static Chunk Ready(Vector3 pos)
    {
        var chunk = new Chunk(null!, pos, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        return chunk;
    }

    private static ushort Block(string name) => ContentRegistry.RequireBlock(name).Id;
    private static byte Sun(ChunkMeshData mesh, int x, int y, int z) =>
        (byte)(mesh.Light![Chunk.Idx(x, y, z)] >> 4);

    private sealed class StreamedColumns : IColumnSource
    {
        private readonly Dictionary<(int, int), ColumnTerrain> _columns = new();
        public void Add(int x, int z, ColumnTerrain terrain) => _columns[(x, z)] = terrain;
        public bool IsReady(int x, int z) => _columns.ContainsKey((x, z));
        public bool TryGet(int x, int z, out ColumnTerrain terrain) => _columns.TryGetValue((x, z), out terrain);
    }
}
