using System.Linq;
using VoxelEngine.Content;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Volcanoes;

/// <summary>Registered Charred material and profile ownership without a terrain scan.</summary>
[Collection("Worldgen seed")]
public sealed class CharredContentTests
{
    public CharredContentTests() => TestContent.EnsureLoaded();

    [Fact]
    public void CharredStoneIsEarlyPickaxeRockAndNotTheHellBasalt()
    {
        var rock = ContentRegistry.RequireBlock("charred_stone");
        var basalt = ContentRegistry.RequireBlock("basalt");
        var pickaxe = ItemRegistry.All.Where(i => ContentRegistry.IsInTag("pickaxe", i.Id))
            .OrderBy(i => i.ToolPower).First(i => i.ToolPower >= 1);
        Assert.NotEqual(basalt.Id, rock.Id);
        Assert.False(Mining.CanMine(rock.Id, null));
        Assert.True(Mining.CanMine(rock.Id, pickaxe));
    }

    [Fact]
    public void TheExistingLeaflessHellTreeOnlyJoinsDynamicCharredWorldgen()
    {
        var tree = TreeSpeciesRegistry.Require("charred_tree");
        Assert.False(tree.HasLeaves);
        Assert.NotNull(tree.Worldgen);
        Assert.True(tree.Worldgen.GrowsIn(Biome.Charred));
        Assert.False(tree.Worldgen.Allows(WorldGenProfile.Legacy));
        Assert.True(tree.Worldgen.Allows(WorldGenProfile.Dynamic));
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Legacy);
            Assert.Null(TreeGenerator.SpeciesByBiome[(int)Biome.Charred]);
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            Assert.Equal([tree], TreeGenerator.DynamicRowFor(Biome.Charred));
            Assert.True(2 * TreeRasterizer.ReachOf(tree).Horizontal
                <= WorldGenerationConfig.TreeCellBlocks);
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Legacy);
            Assert.Null(TreeGenerator.SpeciesByBiome[(int)Biome.Charred]);
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }
}
