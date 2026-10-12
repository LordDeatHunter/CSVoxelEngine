using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Data;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

/// <summary>
/// The entity light query reads the snapshot a chunk publishes with its mesh, so an entity
/// standing in a cell is lit within the smooth corner light the mesher bakes around that cell
/// on the terrain face that looks into it.
/// </summary>
public class EntityLightTests
{
    private const int FloorY = 4;

    private readonly byte _stone;
    private readonly byte _emitter;

    public EntityLightTests()
    {
        TestContent.EnsureLoaded();
        _stone = ContentRegistry.RequireBlock("stone").Id;
        // Pinned to the default-colored lamp, so its light carries the default tint slot
        _emitter = ContentRegistry.RequireBlock("lamp").Id;
        Assert.True(BlockType.Emission[_emitter] > 0 && BlockType.IsSolid(_emitter) && BlockType.Occludes(_emitter));
    }

    /// <summary>A stone floor across the center chunk, high above the terrain.</summary>
    private (ChunkManager Manager, Chunk Center) Floor()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, FloorY, z, _stone, 0);
        return (manager, center);
    }

    private static ChunkMeshData Remesh(ChunkManager manager, Chunk center, bool greedy = true)
    {
        var mesh = center.GenerateMeshData(manager, greedy);
        Assert.NotNull(mesh);
        center.PublishLight(mesh!.Light);
        return mesh;
    }

    private static Vector3 WorldCenter(Chunk chunk, int x, int y, int z) =>
        chunk.Position * Chunk.Size + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);

    [Fact]
    public void SampleLiesWithinTheSmoothLightTheMesherBakesAroundTheCell()
    {
        var (manager, center) = Floor();
        center.SetBlockRaw(8, FloorY + 1, 8, _emitter, 0);
        var mesh = Remesh(manager, center, greedy: false);

        // The stone top face under the air cell (6, FloorY + 1, 8), found by its quad center.
        var verts = mesh.Vertices;
        Vertex[]? top = null;
        for (var q = 0; q + 3 < verts.Count; q += 4)
        {
            if ((verts[q].Normal & 7) != 4) continue;
            var c = (verts[q].Position + verts[q + 1].Position + verts[q + 2].Position + verts[q + 3].Position) / 4f;
            if (MathF.Abs(c.X - 6.5f) < 0.01f && MathF.Abs(c.Z - 8.5f) < 0.01f && MathF.Abs(c.Y - (FloorY + 1)) < 0.01f)
                top = [verts[q], verts[q + 1], verts[q + 2], verts[q + 3]];
        }
        Assert.NotNull(top);

        var sample = EntityLight.Sample(manager, WorldCenter(center, 6, FloorY + 1, 8));
        // Each corner averages the cell with its neighbors, so the cell's own level lies
        // between the face's darkest and brightest corner
        var sun = (byte)MathF.Round(sample.Sun * 15f * 17f);
        var block = (byte)MathF.Round(sample.Block * 15f * 17f);
        Assert.InRange(sun, top!.Min(v => v.Sun), top.Max(v => v.Sun));
        Assert.InRange(block, top.Min(v => v.Block), top.Max(v => v.Block));
        Assert.True(sample.Block > 0f);
    }

    [Fact]
    public void BlockLightFallsOffAwayFromTheEmitter()
    {
        var (manager, center) = Floor();
        center.SetBlockRaw(2, FloorY + 1, 8, _emitter, 0);
        Remesh(manager, center);

        var near = EntityLight.Sample(manager, WorldCenter(center, 3, FloorY + 1, 8));
        var mid = EntityLight.Sample(manager, WorldCenter(center, 8, FloorY + 1, 8));
        var far = EntityLight.Sample(manager, WorldCenter(center, 14, FloorY + 1, 8));

        Assert.True(near.Block > mid.Block);
        Assert.True(mid.Block >= far.Block);
    }

    [Fact]
    public void OpaqueCellTakesItsBrightestOpenNeighbor()
    {
        var (manager, center) = Floor();
        center.SetBlockRaw(8, FloorY + 1, 8, _emitter, 0);
        Remesh(manager, center);

        var (x, y, z) = (7, FloorY, 8);
        float sun = 0f, block = 0f;
        foreach (var (dx, dy, dz) in Vector3Directions.CardinalOffsets)
        {
            var id = center.GetLocalBlock(x + dx, y + dy, z + dz);
            if (!BlockType.PassesLightById[id]) continue;
            var s = EntityLight.Sample(manager, WorldCenter(center, x + dx, y + dy, z + dz));
            sun = MathF.Max(sun, s.Sun);
            block = MathF.Max(block, s.Block);
        }

        var inside = EntityLight.Sample(manager, WorldCenter(center, x, y, z));
        Assert.Equal(new EntityLightSample(sun, block), inside);
        Assert.True(inside.Block > 0f);
    }

    [Fact]
    public void UnknownLightReadsAsTheDefault()
    {
        var (manager, center) = Floor();
        Remesh(manager, center);

        Assert.Equal(EntityLightSample.Default, EntityLight.Sample(null, WorldCenter(center, 6, FloorY + 1, 8)));
        Assert.Equal(EntityLightSample.Default, EntityLight.Sample(manager, new Vector3(10_000f, 5f, 10_000f)));

        // The chunk above is loaded but was never meshed, so it has no snapshot.
        var above = manager.Chunks[center.Position + Vector3Directions.Up];
        Assert.Equal(EntityLightSample.Default, EntityLight.Sample(manager, WorldCenter(above, 6, 6, 8)));
    }

    [Fact]
    public void RepublishingReplacesTheSnapshot()
    {
        var (manager, center) = Floor();
        Remesh(manager, center);
        var cell = WorldCenter(center, 6, FloorY + 1, 8);
        var before = EntityLight.Sample(manager, cell);

        center.SetBlockRaw(7, FloorY + 1, 8, _emitter, 0);
        Remesh(manager, center);
        var after = EntityLight.Sample(manager, cell);

        Assert.True(after.Block > before.Block);
    }

    [Fact]
    public void AnEntityBesideAColoredLampSamplesItsSlot()
    {
        var blue = ContentRegistry.RequireBlock("blue_lamp").Id;
        Assert.NotEqual(0, BlockType.LightSlot[blue]);

        foreach (var lamp in new[] { blue, _emitter })
        {
            var (manager, center) = Floor();
            center.SetBlockRaw(8, FloorY + 1, 8, lamp, 0);
            Remesh(manager, center);

            var beside = EntityLight.Sample(manager, WorldCenter(center, 7, FloorY + 1, 8));
            Assert.True(beside.Block > 0f);
            Assert.Equal(BlockType.LightSlot[lamp], beside.Tint);
        }
    }

    [Fact]
    public void OpaqueCellTakesTheSlotOfItsBrightestNeighbor()
    {
        var blue = ContentRegistry.RequireBlock("blue_lamp").Id;
        var (manager, center) = Floor();
        // A stone cell with the default lamp farther off on its west and the blue lamp nearer on its east
        var (x, y, z) = (8, FloorY + 1, 8);
        center.SetBlockRaw(x, y, z, _stone, 0);
        center.SetBlockRaw(4, y, z, _emitter, 0);
        center.SetBlockRaw(10, y, z, blue, 0);
        Remesh(manager, center);

        var west = EntityLight.Sample(manager, WorldCenter(center, x - 1, y, z));
        var east = EntityLight.Sample(manager, WorldCenter(center, x + 1, y, z));
        Assert.Equal(BlockType.LightSlot[_emitter], west.Tint);
        Assert.Equal(BlockType.LightSlot[blue], east.Tint);
        Assert.True(west.Block > 0f && east.Block > west.Block);

        var inside = EntityLight.Sample(manager, WorldCenter(center, x, y, z));
        Assert.Equal(east.Block, inside.Block);
        Assert.Equal(BlockType.LightSlot[blue], inside.Tint);
    }
}
