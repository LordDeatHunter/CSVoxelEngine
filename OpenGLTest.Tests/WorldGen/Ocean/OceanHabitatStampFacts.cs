using System;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Ocean;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>Controlled registered-block stamp facts; no generated columns or chunk fills.</summary>
[Collection("Worldgen seed")]
public sealed class OceanHabitatStampFacts
{
    [Fact]
    public void ThreeOceanFloorsAndFirstCoralMoundUseSupportedSourceWater()
    {
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var applied = false;
        try
        {
            TestContent.EnsureLoaded();
            var amber = ContentRegistry.RequireBlock("coral_amber").Id;
            var rose = ContentRegistry.RequireBlock("coral_rose").Id;
            var blue = ContentRegistry.RequireBlock("coral_blue").Id;
            var seaMoss = ContentRegistry.RequireBlock("sea_moss").Id;
            var glowMoss = ContentRegistry.RequireBlock("glow_moss").Id;
            var water = ContentRegistry.RequireBlock("water").Id;
            var gravel = ContentRegistry.RequireBlock("gravel").Id;
            var stone = ContentRegistry.RequireBlock("stone").Id;

            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;

            var reef = StampFloor(-12, -16, 0.8f, gravel, water);
            var meadow = StampFloor(-20, -32, 0.5f, stone, water);
            var deep = StampFloor(-60, -64, 0.75f, stone, water);

            AssertFloor(reef, amber);
            AssertFloor(meadow, seaMoss);
            AssertFloor(deep, glowMoss);
            AssertNoCoralColors(meadow, rose, blue);
            AssertNoCoralColors(deep, rose, blue);

            // Independent OCOR arithmetic at seed 1337, world origin: (1,1) roll
            // .765833199 is rejected; (1,2) roll .103742421 is first admitted.
            // Its color roll .014733374 selects rose. Five supports and seven
            // distinct target cells are explicit here rather than built by the stamp.
            const int x = 1, z = 2;
            var ly = reef.LocalFloorY;
            int[] supports =
            [
                Chunk.Idx(x, ly, z), Chunk.Idx(x - 1, ly, z),
                Chunk.Idx(x + 1, ly, z), Chunk.Idx(x, ly, z - 1),
                Chunk.Idx(x, ly, z + 1),
            ];
            int[] targets =
            [
                Chunk.Idx(x, ly + 1, z), Chunk.Idx(x - 1, ly + 1, z),
                Chunk.Idx(x + 1, ly + 1, z), Chunk.Idx(x, ly + 1, z - 1),
                Chunk.Idx(x, ly + 1, z + 1), Chunk.Idx(x, ly + 2, z),
                Chunk.Idx(x, ly + 3, z),
            ];
            foreach (var index in supports)
            {
                Assert.Equal(amber, reef.Blocks[index]);
                Assert.Equal((byte)0, reef.States[index]);
            }
            foreach (var index in targets)
            {
                Assert.Equal(rose, reef.Blocks[index]);
                Assert.Equal((byte)0, reef.States[index]);
            }
            Assert.True(ly >= 0 && ly + 3 < Chunk.Size);
            Assert.True(-12 + 3 <= WorldGenerationConfig.SeaLevel - 2);
        }
        finally
        {
            if (applied) WorldSeed.Apply(previousSeed, previousProfile);
        }
    }

    private static Fixture StampFloor(int floorY, int worldY0, float warmth, ushort host, ushort water)
    {
        var heights = new int[Chunk.Size * Chunk.Size];
        var waterLevels = new int[heights.Length];
        var biomes = new Biome[heights.Length];
        Array.Fill(heights, floorY);
        Array.Fill(waterLevels, WorldGenerationConfig.SeaLevel);
        Array.Fill(biomes, Biome.Ocean);
        var column = new ColumnData(heights, new byte[heights.Length],
            new float[heights.Length], new float[heights.Length],
            biomes: biomes, bed: heights, waterLevel: waterLevels);
        var blocks = new ushort[Chunk.BlockCount];
        var states = new byte[Chunk.BlockCount];
        Array.Fill(blocks, water);
        var localFloorY = floorY - worldY0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            blocks[Chunk.Idx(x, localFloorY, z)] = host;

        var field = new OceanHabitatField(warmth, warmth, warmth, warmth);
        OceanHabitats.Stamp(blocks, states, column, 0, worldY0, 0, field);
        return new Fixture(blocks, states, localFloorY);
    }

    private static void AssertFloor(Fixture fixture, ushort expected)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var index = Chunk.Idx(x, fixture.LocalFloorY, z);
            Assert.Equal(expected, fixture.Blocks[index]);
            Assert.Equal((byte)0, fixture.States[index]);
        }
    }

    private static void AssertNoCoralColors(Fixture fixture, ushort rose, ushort blue)
    {
        foreach (var id in fixture.Blocks)
            Assert.True(id != rose && id != blue, "moss floor may not grow a coral mound");
    }

    private readonly record struct Fixture(ushort[] Blocks, byte[] States, int LocalFloorY);
}
