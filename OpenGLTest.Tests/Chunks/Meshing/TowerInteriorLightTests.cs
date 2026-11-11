using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.Chunks.Meshing;

/// <summary>
/// The battle tower's dark interiors (BT-10) on a real generated tower: a floor's room cells stand above the terrain
/// height, so the heightmap rule alone would light them as open sky, but under their slab of covering bricks they get no
/// sun at all; and a client, which only has the streamed blocks and column terrain, lights the same chunk byte for byte.
/// </summary>
[Collection("Worldgen seed")]
public sealed class TowerInteriorLightTests
{
    public TowerInteriorLightTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    private static TowerSite Site()
    {
        Assert.True(TowerPlacement.TryFindNearest(0, 0, 4, out var site), "no tower near the origin");
        return site;
    }

    // The chunk inside floor 1's room, a little off the tower's axis
    private static Vector3 CenterChunk(TowerSite s) => new(
        VoxelCoords.WorldToChunk(s.CenterX + 4),
        VoxelCoords.WorldToChunk(s.FloorY(1) + 2),
        VoxelCoords.WorldToChunk(s.CenterZ + 4));

    /// <summary>The 27 generated chunks around <paramref name="center"/>, registered in a fresh manager on worldgen
    /// columns.</summary>
    private static (ChunkManager Manager, Dictionary<Vector3, Chunk> Chunks) Generate(Vector3 center)
    {
        var manager = new ChunkManager(null!);
        var chunks = new Dictionary<Vector3, Chunk>();
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            var pos = center + new Vector3(dx, dy, dz);
            var chunk = new Chunk(null!, pos, generateBlockData: false);
            ChunkTerrainFill.GenerateBlockData(chunk);
            manager.Chunks[pos] = chunk;
            chunks[pos] = chunk;
        }
        return (manager, chunks);
    }

    [Fact]
    public void ATowersFloorsGetNoSunUnderTheirSlabs()
    {
        var s = Site();
        var centerPos = CenterChunk(s);
        var (manager, chunks) = Generate(centerPos);
        var center = chunks[centerPos];
        Assert.True(HasCover(chunks.Values), "the tower chunks carry no cover");

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        var light = mesh!.Light;
        Assert.NotNull(light);

        var cx0 = (int)centerPos.X * Chunk.Size;
        var cy0 = (int)centerPos.Y * Chunk.Size;
        var cz0 = (int)centerPos.Z * Chunk.Size;
        var roomCells = 0;
        var darkCells = 0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var wx = cx0 + x;
            var wz = cz0 + z;
            var dx = wx - s.CenterX;
            var dz = wz - s.CenterZ;
            if (TowerShell.RingOf(s, dx, dz, TowerShell.StepOf(s, dx, dz)) != TowerRing.Room) continue;
            var height = ColumnCache.GetHeights(VoxelCoords.WorldToChunk(wx), VoxelCoords.WorldToChunk(wz))
                [((wx - VoxelCoords.WorldToChunk(wx) * Chunk.Size) << Chunk.SizeShift) | (wz - VoxelCoords.WorldToChunk(wz) * Chunk.Size)];
            for (var y = 0; y < Chunk.Size; y++)
            {
                var wy = cy0 + y;
                // Strictly between two slabs (the roof over the top floor) and above the terrain height, so the
                // heightmap rule alone would have made it sky
                if (wy <= s.GroundY || wy >= s.RoofY || (wy - s.GroundY) % WorldGenerationConfig.TowerFloorHeight == 0) continue;
                if (wy < height) continue;
                if (TowerGenerator.CellAt(s, wx, wy, wz) != BlockType.Air) continue;
                roomCells++;
                var sun = light![Chunk.Idx(x, y, z)] >> 4;
                Assert.True(sun < 15, $"the room cell ({wx}, {wy}, {wz}) sees the sky");
                if (sun == 0) darkCells++;
            }
        }
        Assert.True(roomCells > 0, "the chunk holds no room cell above the terrain height");
        Assert.True(darkCells > 0, "no room cell is fully dark");
    }

    [Fact]
    public void AStreamedCopyOfTheTowerChunksLightsTheSame()
    {
        var s = Site();
        var centerPos = CenterChunk(s);
        var (manager, chunks) = Generate(centerPos);
        var expected = chunks[centerPos].GenerateMeshData(manager)!.Light;
        Assert.NotNull(expected);

        // What a client holds: each chunk restored from its snapshot with no worldgen, and the streamed column terrain
        var columns = new StreamedColumns();
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            var cx = (int)centerPos.X + dx;
            var cz = (int)centerPos.Z + dz;
            Assert.True(WorldgenColumnSource.Instance.TryGet(cx, cz, out var terrain));
            columns.Add(cx, cz, terrain);
        }
        var client = new ChunkManager(null!) { ColumnSource = columns };
        foreach (var (pos, chunk) in chunks)
        {
            var copy = new Chunk(null!, pos, generateBlockData: false);
            copy.RestoreFrom(chunk.TakeSnapshot(null), streamedColumns: true);
            client.Chunks[pos] = copy;
        }

        var actual = client.Chunks[centerPos].GenerateMeshData(client)!.Light;
        Assert.Equal(expected, actual);
    }

    private static bool HasCover(IEnumerable<Chunk> chunks)
    {
        foreach (var chunk in chunks)
            if (chunk.MayHaveSkyCover) return true;
        return false;
    }

    /// <summary>A dictionary of streamed column terrain, the client's column source.</summary>
    private sealed class StreamedColumns : IColumnSource
    {
        private readonly Dictionary<(int, int), ColumnTerrain> _columns = new();

        public void Add(int chunkX, int chunkZ, ColumnTerrain terrain) => _columns[(chunkX, chunkZ)] = terrain;

        public bool IsReady(int chunkX, int chunkZ) => _columns.ContainsKey((chunkX, chunkZ));

        public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain) =>
            _columns.TryGetValue((chunkX, chunkZ), out terrain);
    }
}
