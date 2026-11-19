using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Ocean;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>Explicit-input ocean rules; no content, seed, column or engine initialization.</summary>
public sealed class OceanS6bPureTests
{
    [Fact]
    public void SharedCornerAndZeroAdmissionProtectTheDominantFloor()
    {
        var west = new OceanHabitatField(0.1f, 0.8f, 0.3f, 0.7f);
        var east = new OceanHabitatField(0.8f, 0.2f, 0.7f, 0.4f);
        Assert.Equal(west.Sample(Chunk.Size, 5f), east.Sample(0f, 5f));
        Assert.Equal(west.Sample(Chunk.Size, Chunk.Size), east.Sample(0f, Chunk.Size));
        Assert.Equal(OceanHabitat.None, west.Classify(false, true, 0, -12, 8f, 8f));
        Assert.Equal(OceanHabitat.None, west.Classify(true, false, 0, -12, 8f, 8f));
        Assert.Equal(OceanHabitat.None, west.Classify(true, true, -1, -12, 8f, 8f));
        Assert.Equal(OceanHabitat.None, west.Classify(true, true, 0, -2, 8f, 8f));
        Assert.Equal(OceanFloorMaterial.None,
            OceanFloorMaterials.Select(OceanHabitat.None, west.Sample(8f, 8f), 12));
        Assert.Equal(OceanFloorMaterial.Coral,
            OceanFloorMaterials.Select(east.Classify(true, true, 0, -12, 0f, 0f), 0.8f, 12));
    }

    [Fact]
    public void CoralAndMossPreflightTheWholeOwnedSite()
    {
        const ushort host = 11, water = 22, rose = 33, moss = 44, lava = 55, ore = 66;
        const int x = 6, z = 7, floorY = -20, chunkY0 = -32;
        var blocks = new ushort[Chunk.Size * Chunk.Size * Chunk.Size];
        var states = new byte[blocks.Length];
        var ly = floorY - chunkY0;

        void Prepare()
        {
            for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (System.Math.Abs(dx) + System.Math.Abs(dz) > 1) continue;
                blocks[Chunk.Idx(x + dx, ly, z + dz)] = host;
                blocks[Chunk.Idx(x + dx, ly + 1, z + dz)] = water;
                states[Chunk.Idx(x + dx, ly, z + dz)] = 0;
                states[Chunk.Idx(x + dx, ly + 1, z + dz)] = 0;
            }
            blocks[Chunk.Idx(x, ly + 2, z)] = water;
            blocks[Chunk.Idx(x, ly + 3, z)] = water;
            states[Chunk.Idx(x, ly + 2, z)] = 0;
            states[Chunk.Idx(x, ly + 3, z)] = 0;
        }
        bool Mound() => OceanHabitats.TryWriteCoralMound(blocks, states, x, z,
            floorY, chunkY0, host, water, rose);
        var farTarget = Chunk.Idx(x, ly + 3, z);
        var firstTarget = Chunk.Idx(x, ly + 1, z);
        var sideSupport = Chunk.Idx(x + 1, ly, z);

        Prepare(); blocks[farTarget] = lava;
        Assert.False(Mound()); Assert.Equal(water, blocks[firstTarget]);
        Prepare(); blocks[farTarget] = 0;
        Assert.False(Mound()); Assert.Equal(water, blocks[firstTarget]);
        Prepare(); blocks[farTarget] = ore;
        Assert.False(Mound()); Assert.Equal(water, blocks[firstTarget]);
        Prepare(); states[farTarget] = 1;
        Assert.False(Mound()); Assert.Equal(water, blocks[firstTarget]);
        Prepare(); blocks[sideSupport] = ore;
        Assert.False(Mound()); Assert.Equal(water, blocks[firstTarget]);
        Prepare(); states[sideSupport] = 1;
        Assert.False(Mound()); Assert.Equal(water, blocks[firstTarget]);
        Prepare();
        Assert.False(OceanHabitats.TryWriteCoralMound(blocks, states, 0, z,
            floorY, chunkY0, host, water, rose));
        Assert.Equal(water, blocks[firstTarget]);
        Prepare();
        Assert.True(Mound());
        Assert.Equal(rose, blocks[farTarget]);
        Assert.Equal(rose, blocks[Chunk.Idx(x + 1, ly + 1, z)]);
        Assert.Equal(0, states[farTarget]);
        Assert.Equal(0, states[firstTarget]);

        Prepare(); blocks[firstTarget] = lava;
        Assert.False(OceanHabitats.TryDressFloor(blocks, states, x, z,
            floorY, chunkY0, host, water, moss));
        Assert.Equal(host, blocks[Chunk.Idx(x, ly, z)]);
        Prepare(); states[firstTarget] = 1;
        Assert.False(OceanHabitats.TryDressFloor(blocks, states, x, z,
            floorY, chunkY0, host, water, moss));
        Assert.Equal(host, blocks[Chunk.Idx(x, ly, z)]);
        Prepare(); blocks[Chunk.Idx(x, ly, z)] = ore;
        Assert.False(OceanHabitats.TryDressFloor(blocks, states, x, z,
            floorY, chunkY0, host, water, moss));
        Assert.Equal(water, blocks[firstTarget]);
        Prepare();
        Assert.True(OceanHabitats.TryDressFloor(blocks, states, x, z,
            floorY, chunkY0, host, water, moss));
        Assert.Equal(moss, blocks[Chunk.Idx(x, ly, z)]);
        Assert.Equal(0, states[Chunk.Idx(x, ly, z)]);
    }

    [Fact]
    public void DominantHabitatsMapToDistinctGeneratedFloorCodes()
    {
        Assert.Equal(OceanFloorMaterial.Coral,
            OceanFloorMaterials.Select(OceanHabitat.Reef, 0.8f, 12));
        Assert.Equal(OceanFloorMaterial.SeaMoss,
            OceanFloorMaterials.Select(OceanHabitat.MeadowFloor, 0.4f, 20));
        Assert.Equal(OceanFloorMaterial.GlowMoss,
            OceanFloorMaterials.Select(OceanHabitat.DeepPlain, 0.7f, 60));
        Assert.Equal(OceanFloorMaterial.None,
            OceanFloorMaterials.Select(OceanHabitat.ColdShelf, 0.1f, 20));
        Assert.Equal(OceanFloorMaterial.None,
            OceanFloorMaterials.Select(OceanHabitat.DeepPlain, 0.4f, 60));
        Assert.Equal((byte)8, OceanFloorMaterials.LodCode(OceanFloorMaterial.Coral));
        Assert.Equal((byte)9, OceanFloorMaterials.LodCode(OceanFloorMaterial.SeaMoss));
        Assert.Equal((byte)10, OceanFloorMaterials.LodCode(OceanFloorMaterial.GlowMoss));
    }
}
