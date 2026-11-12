using System;
using System.Linq;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The block flags a status effect's block interaction reads: <c>flammable</c> (an igniting effect
/// burns the block away), <c>freezesTo</c> (a freezing effect turns a fluid source solid) and
/// <c>meltsTo</c> (a thawing effect melts a block back into a fluid). The validator refuses each
/// combination the interaction could not honor, and the shipped links always join a fluid and a solid.
/// </summary>
public sealed class StatusBlockFlagTests
{
    public StatusBlockFlagTests() => TestContent.EnsureLoaded();

    public static TheoryData<string> IllegalCombinations => new()
    {
        "flammable-fluid",
        "flammable-multiblock",
        "flammable-multiblock-part",
        "freezes-solid",
        "melts-fluid",
    };

    private static BlockDefinition Illegal(string kind) => kind switch
    {
        "flammable-fluid" => new BlockDefinition { Fluid = true, Flammable = true },
        "flammable-multiblock" => new BlockDefinition { Renderer = "multiblock", Flammable = true },
        "flammable-multiblock-part" => new BlockDefinition { Renderer = "multiblock_part", Flammable = true },
        "freezes-solid" => new BlockDefinition { FreezesTo = "ice" },
        "melts-fluid" => new BlockDefinition { Fluid = true, MeltsTo = "water" },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(IllegalCombinations))]
    public void IllegalStatusFlagsNameTheirFile(string kind)
    {
        var path = $"assets/data/blocks/{kind}.json";
        var error = Assert.Throws<InvalidOperationException>(
            () => BlockFieldRules.ValidateStatusFlags(Illegal(kind), path));
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void LegalStatusFlagsPass()
    {
        BlockFieldRules.ValidateStatusFlags(new BlockDefinition(), "plain.json");
        BlockFieldRules.ValidateStatusFlags(new BlockDefinition { Flammable = true }, "leaves.json");
        BlockFieldRules.ValidateStatusFlags(new BlockDefinition { Fluid = true, FreezesTo = "ice" }, "water.json");
        BlockFieldRules.ValidateStatusFlags(new BlockDefinition { MeltsTo = "water" }, "ice.json");
    }

    [Fact]
    public void ShippedFreezeAndMeltLinksJoinAFluidAndASolid()
    {
        var freezing = BlockType.Types.Values.Where(t => t.FreezesToId != 0).ToList();
        var melting = BlockType.Types.Values.Where(t => t.MeltsToId != 0).ToList();
        Assert.NotEmpty(freezing);
        Assert.NotEmpty(melting);

        foreach (var type in freezing)
        {
            Assert.True(type.Fluid, $"{type.Name} freezes but is not a fluid");
            Assert.False(BlockType.IsFluid(type.FreezesToId), $"{type.Name} freezes into a fluid");
        }
        foreach (var type in melting)
        {
            Assert.False(type.Fluid, $"{type.Name} melts but is itself a fluid");
            Assert.True(BlockType.IsFluid(type.MeltsToId), $"{type.Name} melts into a non-fluid");
            Assert.NotEqual(type.Id, type.MeltsToId);
        }
    }

    [Fact]
    public void NoFluidIsFlammableAndTheFlatTablesAgree()
    {
        Assert.Contains(BlockType.Types.Values, t => t.Flammable);
        foreach (var type in BlockType.Types.Values)
        {
            Assert.False(type.Fluid && type.Flammable, $"{type.Name} is a flammable fluid");
            Assert.Equal(type.Flammable, BlockType.FlammableById[type.Id]);
            Assert.Equal(type.FreezesToId, BlockType.FreezesToById[type.Id]);
            Assert.Equal(type.MeltsToId, BlockType.MeltsToById[type.Id]);
        }
        Assert.False(BlockType.FlammableById[BlockType.Air]);
        Assert.Equal(0, BlockType.FreezesToById[BlockType.Air]);
        Assert.Equal(0, BlockType.MeltsToById[BlockType.Air]);
    }
}
