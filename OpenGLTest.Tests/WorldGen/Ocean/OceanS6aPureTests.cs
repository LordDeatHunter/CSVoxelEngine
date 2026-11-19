using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Ocean;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>Small explicit-input controls; these facts do not initialize world generation.</summary>
public sealed class OceanS6aPureTests
{
    [Fact]
    public void FieldHasSharedCornersAndZeroMask()
    {
        var left = new OceanHabitatField(0.2f, 0.8f, 0.4f, 0.6f);
        var right = new OceanHabitatField(0.8f, 0.3f, 0.6f, 0.1f);
        Assert.Equal(left.Sample(Chunk.Size, 0f), right.Sample(0f, 0f));
        Assert.Equal(left.Sample(Chunk.Size, Chunk.Size), right.Sample(0f, Chunk.Size));
        Assert.InRange(left.Sample(8f, 8f), 0.2f, 0.8f);
        Assert.Equal(OceanHabitat.None, left.Classify(false, true, 0, -20, 8f, 8f));
        Assert.Equal(OceanHabitat.None, left.Classify(true, false, 0, -20, 8f, 8f));
        Assert.Equal(OceanHabitat.None, left.Classify(true, true, -1, -20, 8f, 8f));
        Assert.Equal(OceanHabitat.None, left.Classify(true, true, 0, -2, 8f, 8f));
    }

    [Fact]
    public void PinnacleRaisesContinuouslyOnlyInsideItsSubmergedAdmission()
    {
        const float floor = -60f;
        static float Raise(float weight, float incoming, int x = 0) =>
            OceanPinnacles.RaiseForSite(true, true, 0, weight, incoming,
                x, 0, 0, 0, 28, -6);

        Assert.Equal(floor, OceanPinnacles.RaiseForSite(false, true, 0, 0.8f,
            floor, 0, 0, 0, 0, 28, -6));
        Assert.Equal(floor, OceanPinnacles.RaiseForSite(true, false, 0, 0.8f,
            floor, 0, 0, 0, 0, 28, -6));
        Assert.Equal(floor, OceanPinnacles.RaiseForSite(true, true, -1, 0.8f,
            floor, 0, 0, 0, 0, 28, -6));
        Assert.Equal(floor, Raise(0.5f, floor));
        Assert.Equal(floor, Raise(0.8f, floor, 28));
        Assert.Equal(-20f, Raise(0.8f, -20f));
        var nearWeightEdge = Raise(0.5001f, floor);
        var full = Raise(0.8f, floor);
        Assert.True(nearWeightEdge >= floor && nearWeightEdge < full);
        Assert.True(full > floor && full <= -4f);
        Assert.True(Raise(0.8f, -20.01f) > -20.01f);
        Assert.True(Raise(0.8f, -20.01f) < Raise(0.8f, -28f));
    }

    [Fact]
    public void RockColumnPreflightsEveryCellAndResetsWrittenState()
    {
        const ushort floor = 11, water = 22, rock = 33, lava = 44, ore = 55;
        const int x = 4, z = 6, floorY = -20, chunkY0 = -32;
        var blocks = new ushort[Chunk.Size * Chunk.Size * Chunk.Size];
        var state = new byte[blocks.Length];
        var host = Chunk.Idx(x, floorY - chunkY0, z);
        var first = Chunk.Idx(x, floorY - chunkY0 + 1, z);
        var second = Chunk.Idx(x, floorY - chunkY0 + 2, z);
        void Prepare()
        {
            blocks[host] = floor;
            blocks[first] = water;
            blocks[second] = water;
            state[first] = 0;
            state[second] = 0;
        }
        bool Write() => OceanRockFields.TryWriteColumn(blocks, state, x, z,
            floorY, chunkY0, 2, floor, water, rock);

        Prepare();
        blocks[second] = lava;
        Assert.False(Write());
        Assert.Equal(water, blocks[first]);
        Prepare();
        blocks[second] = 0;
        Assert.False(Write());
        Assert.Equal(water, blocks[first]);
        Prepare();
        blocks[second] = ore;
        Assert.False(Write());
        Assert.Equal(water, blocks[first]);
        Prepare();
        blocks[host] = ore;
        Assert.False(Write());
        Assert.Equal(water, blocks[first]);
        Prepare();
        state[second] = 1;
        Assert.False(Write());
        Assert.Equal(water, blocks[first]);
        Prepare();
        Assert.False(OceanRockFields.TryWriteColumn(blocks, state, x, z,
            chunkY0 - 1, chunkY0, 2, floor, water, rock));
        Assert.Equal(water, blocks[first]);
        Prepare();
        Assert.True(Write());
        Assert.Equal(rock, blocks[first]);
        Assert.Equal(rock, blocks[second]);
        Assert.Equal(0, state[first]);
        Assert.Equal(0, state[second]);
    }
}
