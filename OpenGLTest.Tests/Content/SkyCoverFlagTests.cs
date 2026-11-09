using System;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The <c>coversSky</c> block flag at load: a covering block must be an opaque, plain full cell,
/// so the validator refuses every kind that would leave the cells under it lit by the sky it
/// claims to hide, and the shipped covering blocks agree with the flat table and the search set.
/// </summary>
public sealed class SkyCoverFlagTests
{
    public SkyCoverFlagTests() => TestContent.EnsureLoaded();

    public static TheoryData<string> IllegalKinds => new()
    {
        "fluid",
        "cutout",
        "translucent",
        "not-solid",
        "passes-light",
        "pane",
    };

    private static BlockDefinition Illegal(string kind) => kind switch
    {
        "fluid" => new BlockDefinition { CoversSky = true, Fluid = true },
        "cutout" => new BlockDefinition { CoversSky = true, Cutout = true },
        "translucent" => new BlockDefinition { CoversSky = true, Translucent = true },
        "not-solid" => new BlockDefinition { CoversSky = true, Solid = false },
        "passes-light" => new BlockDefinition
        {
            CoversSky = true,
            Light = new BlockDefinition.LightDefinition { Passes = true },
        },
        "pane" => new BlockDefinition { CoversSky = true, Renderer = "pane" },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void Validate(BlockDefinition definition, string path) =>
        BlockFieldRules.ValidateCoversSky(definition, BlockFieldRules.ParseRenderer(definition, path), path);

    [Theory]
    [MemberData(nameof(IllegalKinds))]
    public void CoversSkyIsRefusedOnABlockThatCannotShadeItsCells(string kind)
    {
        var path = $"assets/data/blocks/{kind}.json";
        var error = Assert.Throws<InvalidOperationException>(() => Validate(Illegal(kind), path));
        Assert.Contains(path, error.Message);
        Assert.Contains("coversSky", error.Message);
    }

    [Fact]
    public void APlainOpaqueBlockMayCoverTheSky()
    {
        Validate(new BlockDefinition { CoversSky = true }, "brick.json");
        Validate(new BlockDefinition { CoversSky = true, Solid = true }, "solid_brick.json");
        // Without the flag nothing is refused, whatever the block is
        Validate(new BlockDefinition { Fluid = true, Cutout = true }, "odd.json");
        Validate(new BlockDefinition { CoversSky = false, Renderer = "pane" }, "pane.json");
    }

    [Fact]
    public void TheSearchSetFindsExactlyTheCoveringBlocks()
    {
        Assert.Contains(BlockType.Types.Values, t => t.CoversSky);
        foreach (var type in BlockType.Types.Values)
        {
            Assert.Equal(type.CoversSky, BlockType.CoversSkyById[type.Id]);
            Assert.Equal(type.CoversSky, BlockType.ContainsSkyCover([type.Id]));
        }
        Assert.False(BlockType.ContainsSkyCover(new byte[4096]));
    }
}
