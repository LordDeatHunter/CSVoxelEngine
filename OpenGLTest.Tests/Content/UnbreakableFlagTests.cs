using System;
using System.Linq;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The <c>unbreakable</c> block flag at load: the validator refuses every field that would hand
/// the cell to a removal path the unbreakable guards do not cover (a fluid first of all), and the
/// shipped unbreakable blocks keep clear of those paths with the flat table agreeing.
/// </summary>
public sealed class UnbreakableFlagTests
{
    public UnbreakableFlagTests() => TestContent.EnsureLoaded();

    public static TheoryData<string> IllegalCombinations => new()
    {
        "fluid",
        "falls",
        "needs-support",
        "replaceable-by-placement",
        "flammable",
        "melts",
        "tree-wood",
        "multiblock",
        "multiblock-part",
        "upper-half",
        "prime-tnt",
    };

    private static BlockDefinition Illegal(string kind) => kind switch
    {
        "fluid" => new BlockDefinition { Unbreakable = true, Fluid = true },
        "falls" => new BlockDefinition { Unbreakable = true, Falls = true },
        "needs-support" => new BlockDefinition { Unbreakable = true, NeedsSupport = true },
        "replaceable-by-placement" => new BlockDefinition { Unbreakable = true, ReplaceableByPlacement = true },
        "flammable" => new BlockDefinition { Unbreakable = true, Flammable = true },
        "melts" => new BlockDefinition { Unbreakable = true, MeltsTo = "water" },
        "tree-wood" => new BlockDefinition { Unbreakable = true, Renderer = "tree_wood" },
        "multiblock" => new BlockDefinition { Unbreakable = true, Renderer = "multiblock" },
        "multiblock-part" => new BlockDefinition { Unbreakable = true, Renderer = "multiblock_part" },
        "upper-half" => new BlockDefinition { Unbreakable = true, UpperHalf = "oak_door_upper" },
        "prime-tnt" => new BlockDefinition { Unbreakable = true, OnBreak = "prime_tnt" },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void Validate(BlockDefinition definition, string path) =>
        BlockFieldRules.ValidateUnbreakable(definition, BlockFieldRules.ParseRenderer(definition, path), path);

    [Theory]
    [MemberData(nameof(IllegalCombinations))]
    public void IllegalUnbreakableCombinationsNameTheirFile(string kind)
    {
        var path = $"assets/data/blocks/{kind}.json";
        var error = Assert.Throws<InvalidOperationException>(() => Validate(Illegal(kind), path));
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void AFluidIsRefusedForBeingAFluid()
    {
        // Even with other refused fields set, a fluid reports the fluid rule
        var error = Assert.Throws<InvalidOperationException>(() =>
            Validate(new BlockDefinition { Unbreakable = true, Fluid = true, Flammable = true }, "lava.json"));
        Assert.Contains("fluid", error.Message);
    }

    [Fact]
    public void LegalCombinationsPass()
    {
        Validate(new BlockDefinition { Unbreakable = true }, "brick.json");
        Validate(new BlockDefinition { Fluid = true }, "water.json");
        Validate(new BlockDefinition { Unbreakable = false, Fluid = true, Flammable = true }, "odd.json");
    }

    [Fact]
    public void ShippedUnbreakableBlocksAvoidEveryUnguardedRemovalPath()
    {
        Assert.Contains(BlockType.Types.Values, t => t.Unbreakable);
        foreach (var type in BlockType.Types.Values)
        {
            Assert.Equal(type.Unbreakable, BlockType.IsUnbreakable(type.Id));
            if (!type.Unbreakable) continue;
            Assert.False(type.Fluid, $"{type.Name} is an unbreakable fluid");
            Assert.False(type.Falls, $"{type.Name} is unbreakable but falls");
            Assert.False(type.NeedsSupport, $"{type.Name} is unbreakable but needs support");
            Assert.False(BlockType.IsReplaceable(type.Id), $"{type.Name} is unbreakable but replaceable");
            Assert.False(type.Flammable, $"{type.Name} is unbreakable but flammable");
            Assert.False(BlockType.IsTreeWood(type.Id), $"{type.Name} is unbreakable tree wood");
            Assert.Equal(0, type.MeltsToId);
        }
        Assert.False(BlockType.IsUnbreakable(BlockType.Air));
    }

    [Fact]
    public void NoMultiblockIsBuiltFromAnUnbreakableBlock()
    {
        var anchors = BlockType.Types.Values.Where(t => t.Renderer == BlockRenderer.Multiblock).ToList();
        Assert.NotEmpty(anchors);
        foreach (var anchor in anchors)
            Assert.False(BlockType.IsUnbreakable(anchor.MultiblockSourceId), $"{anchor.Name} converts an unbreakable block");
    }
}
