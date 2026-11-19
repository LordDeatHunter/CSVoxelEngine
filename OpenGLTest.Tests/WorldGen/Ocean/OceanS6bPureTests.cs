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
        var expectedBlocks = new ushort[blocks.Length];
        var expectedStates = new byte[states.Length];
        var ly = floorY - chunkY0;
        var floor = Chunk.Idx(x, ly, z);
        var farTarget = Chunk.Idx(x, ly + 3, z);
        var firstTarget = Chunk.Idx(x, ly + 1, z);
        var sideSupport = Chunk.Idx(x + 1, ly, z);
        // The independent intended shape is one horizontal cross plus two cells above its center.
        int[] moundTargets =
        [
            firstTarget,
            Chunk.Idx(x - 1, ly + 1, z), Chunk.Idx(x + 1, ly + 1, z),
            Chunk.Idx(x, ly + 1, z - 1), Chunk.Idx(x, ly + 1, z + 1),
            Chunk.Idx(x, ly + 2, z), farTarget,
        ];

        void Prepare()
        {
            System.Array.Clear(blocks);
            System.Array.Clear(states);
            blocks[0] = ore;
            states[0] = 3;
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
        void Snapshot()
        {
            blocks.CopyTo(expectedBlocks, 0);
            states.CopyTo(expectedStates, 0);
        }
        void AssertWholeBuffers() // Each failed site leaves every own-chunk cell untouched.
        {
            Assert.Equal<ushort>(expectedBlocks, blocks);
            Assert.Equal<byte>(expectedStates, states);
        }
        void RejectMound(int candidateX = x, int candidateZ = z, int candidateWorldY0 = chunkY0)
        {
            Snapshot();
            Assert.False(OceanHabitats.TryWriteCoralMound(blocks, states,
                candidateX, candidateZ, floorY, candidateWorldY0, host, water, rose));
            AssertWholeBuffers();
        }
        void RejectFloor()
        {
            Snapshot();
            Assert.False(OceanHabitats.TryDressFloor(blocks, states, x, z,
                floorY, chunkY0, host, water, moss));
            AssertWholeBuffers();
        }

        Prepare(); blocks[farTarget] = lava; RejectMound();
        Prepare(); blocks[farTarget] = 0; RejectMound();
        Prepare(); blocks[farTarget] = ore; RejectMound();
        Prepare(); states[farTarget] = 1; RejectMound();
        Prepare(); blocks[sideSupport] = ore; RejectMound();
        Prepare(); states[sideSupport] = 1; RejectMound();
        Prepare(); RejectMound(candidateX: 0);
        Prepare(); RejectMound(candidateZ: 0);
        Prepare(); RejectMound(candidateWorldY0: floorY - (Chunk.Size - 3));
        Prepare(); RejectMound(candidateWorldY0: floorY + 1);

        Prepare(); Snapshot();
        Assert.True(OceanHabitats.TryWriteCoralMound(blocks, states,
            x, z, floorY, chunkY0, host, water, rose));
        foreach (var target in moundTargets)
        {
            expectedBlocks[target] = rose;
            Assert.Equal((byte)0, states[target]);
        }
        AssertWholeBuffers(); // Includes every support and every outside cell.

        Prepare(); Snapshot();
        Assert.True(OceanHabitats.TryWriteCoralMound(blocks, null,
            x, z, floorY, chunkY0, host, water, rose));
        foreach (var target in moundTargets) expectedBlocks[target] = rose;
        AssertWholeBuffers();

        Prepare(); blocks[firstTarget] = lava; RejectFloor();
        Prepare(); states[firstTarget] = 1; RejectFloor();
        Prepare(); blocks[floor] = ore; RejectFloor();
        Prepare(); states[floor] = 1; RejectFloor();

        Prepare(); Snapshot();
        Assert.True(OceanHabitats.TryDressFloor(blocks, states,
            x, z, floorY, chunkY0, host, water, moss));
        expectedBlocks[floor] = moss;
        Assert.Equal((byte)0, states[floor]);
        AssertWholeBuffers();

        Prepare(); Snapshot();
        Assert.True(OceanHabitats.TryDressFloor(blocks, null,
            x, z, floorY, chunkY0, host, water, moss));
        expectedBlocks[floor] = moss;
        AssertWholeBuffers();
    }

    [Fact]
    public void DominantHabitatsMapToDistinctGeneratedFloorCodes()
    {
        Assert.Equal(OceanFloorMaterial.Coral,
            OceanFloorMaterials.Select(OceanHabitat.Reef, 0.8f, 12));
        Assert.Equal(OceanFloorMaterial.SeaMoss,
            OceanFloorMaterials.Select(OceanHabitat.MeadowFloor, 0.4f, 20));
        Assert.Equal(OceanFloorMaterial.GlowMoss,
            OceanFloorMaterials.Select(OceanHabitat.DeepPlain,
                WorldFeatureConfig.OceanGlowMossMinWarmth, WorldFeatureConfig.OceanGlowMossMinDepth));
        Assert.Equal(OceanFloorMaterial.None,
            OceanFloorMaterials.Select(OceanHabitat.ColdShelf, 0.1f, 20));
        Assert.Equal(OceanFloorMaterial.None,
            OceanFloorMaterials.Select(OceanHabitat.DeepPlain, 0.4f, 60));
        Assert.Equal(OceanFloorMaterial.None,
            OceanFloorMaterials.Select(OceanHabitat.DeepPlain,
                WorldFeatureConfig.OceanGlowMossMinWarmth, WorldFeatureConfig.OceanGlowMossMinDepth - 1));
        var coral = OceanFloorMaterials.LodCode(OceanFloorMaterial.Coral);
        var meadow = OceanFloorMaterials.LodCode(OceanFloorMaterial.SeaMoss);
        var glow = OceanFloorMaterials.LodCode(OceanFloorMaterial.GlowMoss);
        Assert.NotEqual(coral, meadow);
        Assert.NotEqual(coral, glow);
        Assert.NotEqual(meadow, glow);
        Assert.NotEqual(OceanFloorMaterials.LodCode(OceanFloorMaterial.None), coral);
        Assert.NotEqual(OceanFloorMaterials.LodCode(OceanFloorMaterial.None), meadow);
        Assert.NotEqual(OceanFloorMaterials.LodCode(OceanFloorMaterial.None), glow);
    }
}
