using System.Collections.Generic;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The cave life: a glow mushroom stands on a dry carved cave floor well under the surface, and a
/// stalactite hangs from a ceiling with a stalagmite standing under its drip point — all of them
/// only in air, never on the surface or in water.
/// </summary>
public sealed class CaveLifeTests
{
    public CaveLifeTests() => TestContent.EnsureLoaded();

    // A hand-built column: ground at y=20, water at y=25 (a lake), the surface block at 20
    private static ColumnData Column()
    {
        var heights = new int[Chunk.Size * Chunk.Size];
        var water = new int[Chunk.Size * Chunk.Size];
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = 20;
            water[i] = 25;
        }
        return new ColumnData(heights, new byte[heights.Length], new float[heights.Length],
            new float[heights.Length], waterLevel: water);
    }

    [Fact]
    public void AMushroomStandsOnACaveFloorUnderTheSurface()
    {
        var column = Column();
        var members = new HashSet<int>();
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            if (GlowMushroomGenerator.TryGetMushroom(x, z)) members.Add((x << Chunk.SizeShift) | z);
        Assert.NotEmpty(members);

        // A chunk at y=0: a dry carved cave with a floor at y=4 and a ceiling at y=9, except one
        // flooded column (a cave lake)
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        var waterBlock = ContentRegistry.RequireBlock("water").Id;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            blocks[Chunk.Idx(x, 4, z)] = stone;
            blocks[Chunk.Idx(x, 9, z)] = stone;
            if (x == 0 && z == 0)
                for (var y = 5; y < 9; y++)
                    blocks[Chunk.Idx(x, y, z)] = waterBlock;
        }

        GlowMushroomGenerator.Stamp(blocks, column, 0, 0, 0);

        var standing = 0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var isMushroom = blocks[Chunk.Idx(x, 5, z)] == GlowMushroomGenerator.MushroomBlock;
            // A hashed column's carved, dry floor cell takes one; the flooded column never does
            Assert.Equal(members.Contains((x << Chunk.SizeShift) | z), isMushroom);
            if (isMushroom) standing++;
        }
        Assert.True(standing > 0, "no cave column carried a mushroom");
    }

    [Fact]
    public void AStalactiteDripsAStalagmiteOntoTheFloorBelow()
    {
        var column = Column();
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        // A cave room: floor at y=4, air up to a ceiling at y=12
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            blocks[Chunk.Idx(x, 4, z)] = ContentRegistry.RequireBlock("stone").Id;
            for (var y = 5; y < 12; y++)
                blocks[Chunk.Idx(x, y, z)] = BlockType.Air;
            blocks[Chunk.Idx(x, 12, z)] = ContentRegistry.RequireBlock("stone").Id;
        }

        CaveFormationGenerator.Stamp(blocks, column, 0, 0, 0);

        var stalactites = 0;
        var stalagmites = 0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var hanging = false;
            for (var y = 11; y >= 5; y--)
            {
                var id = blocks[Chunk.Idx(x, y, z)];
                if (id == CaveFormationGenerator.StalactiteBlock)
                {
                    hanging = true;
                    stalactites++;
                }
                else if (id == CaveFormationGenerator.StalagmiteBlock)
                {
                    stalagmites++;
                    // A stalagmite under a hanging point is the pair; on its own it stands on the floor
                    Assert.True(hanging || y == 5);
                }
            }
        }

        Assert.True(stalactites > 0, "no stalactite in the cave room");
        Assert.True(stalagmites > 0, "no stalagmite in the cave room");
    }

    [Fact]
    public void ACaveFormationIsRockAndAFlowLeavesItStanding()
    {
        // A formation pops when the rock holding it up is mined, but a flow may not wash it away
        // the way it washes a pebble or a plant away: the fluid tick reads IsReplaceable
        var stalagmite = ContentRegistry.RequireBlock("stalagmite").Id;
        var stalactite = ContentRegistry.RequireBlock("stalactite").Id;
        var pebble = ContentRegistry.RequireBlock("pebble").Id;

        Assert.True(BlockType.ById[stalagmite]!.NeedsSupport);
        Assert.True(BlockType.ById[stalagmite]!.FlowResistant);
        Assert.False(BlockType.IsReplaceable(stalagmite));
        Assert.False(BlockType.IsReplaceable(stalactite));
        Assert.True(BlockType.IsReplaceable(pebble));
    }
}
