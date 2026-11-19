using System;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Ocean;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>Controlled stepped reef fact; this is not a natural generated-column observation.</summary>
[Collection("Worldgen seed")]
public sealed class OceanCoralSteppedStampFacts
{
    [Fact]
    public void OneStepReefFloorMakesAConnectedSevenCellMound()
    {
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var applied = false;
        try
        {
            TestContent.EnsureLoaded();
            var amber = ContentRegistry.RequireBlock("coral_amber").Id;
            var blue = ContentRegistry.RequireBlock("coral_blue").Id;
            var gravel = ContentRegistry.RequireBlock("gravel").Id;
            var water = ContentRegistry.RequireBlock("water").Id;
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;

            const int x = 1, z = 10, floorY = -13, worldY0 = -16;
            const int worldX0 = -2624, worldZ0 = 3776;
            var heights = new int[Chunk.Size * Chunk.Size];
            var waterLevels = new int[heights.Length];
            var biomes = new Biome[heights.Length];
            Array.Fill(heights, floorY);
            int[] columnXs = [x, x - 1, x + 1, x, x];
            int[] columnZs = [z, z, z, z - 1, z + 1];
            heights[((x + 1) << Chunk.SizeShift) | z] = floorY + 1;
            for (var i = 0; i < columnXs.Length; i++)
            {
                var ci = (columnXs[i] << Chunk.SizeShift) | columnZs[i];
                biomes[ci] = Biome.Ocean;
                waterLevels[ci] = WorldGenerationConfig.SeaLevel;
            }
            var column = new ColumnData(heights, new byte[heights.Length],
                new float[heights.Length], new float[heights.Length],
                biomes: biomes, bed: heights, waterLevel: waterLevels);
            var blocks = new ushort[Chunk.BlockCount];
            var states = new byte[Chunk.BlockCount];
            Array.Fill(blocks, water);
            blocks[0] = gravel;
            states[0] = 7;
            int[] supports =
            [
                Chunk.Idx(x, 3, z), Chunk.Idx(x - 1, 3, z),
                Chunk.Idx(x + 1, 4, z), Chunk.Idx(x, 3, z - 1),
                Chunk.Idx(x, 3, z + 1),
            ];
            foreach (var index in supports) blocks[index] = gravel;
            var expectedBlocks = (ushort[])blocks.Clone();
            var expectedStates = (byte[])states.Clone();
            foreach (var index in supports) expectedBlocks[index] = amber;
            // Independent shape: three spine cells, three level-4 spokes and east level-5 spoke.
            int[] targets =
            [
                Chunk.Idx(x, 4, z), Chunk.Idx(x, 5, z), Chunk.Idx(x, 6, z),
                Chunk.Idx(x - 1, 4, z), Chunk.Idx(x + 1, 5, z),
                Chunk.Idx(x, 4, z - 1), Chunk.Idx(x, 4, z + 1),
            ];
            foreach (var index in targets) expectedBlocks[index] = blue;

            var field = new OceanHabitatField(0.8f, 0.8f, 0.8f, 0.8f);
            OceanHabitats.Stamp(blocks, states, column, worldX0, worldY0, worldZ0, field);

            Assert.Equal<ushort>(expectedBlocks, blocks);
            Assert.Equal<byte>(expectedStates, states);
            foreach (var index in supports) Assert.Equal(amber, blocks[index]);
            foreach (var index in targets)
            {
                Assert.Equal(blue, blocks[index]);
                Assert.Equal((byte)0, states[index]);
            }
            Assert.Equal(blue, blocks[Chunk.Idx(x, 5, z)]);
            Assert.Equal(blue, blocks[Chunk.Idx(x + 1, 5, z)]); // Raised spoke shares a face.
        }
        finally
        {
            if (applied) WorldSeed.Apply(previousSeed, previousProfile);
        }
    }
}
