using VoxelEngine.Content;
using VoxelEngine.Interaction;
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
        var pickaxe = ContentRegistry.RequireItem("primitive_pickaxe");
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
        Assert.Null(TreeGenerator.SpeciesByBiome[(int)Biome.Charred]);
        Assert.Equal([tree], TreeGenerator.DynamicRowFor(Biome.Charred));
        var charredReach = TreeRasterizer.ReachOf(tree);
        var existingMaximum = TreeRasterizer.ReachOf(TreeSpeciesRegistry.Require("jungle_giant"));
        Assert.True(charredReach.Horizontal <= existingMaximum.Horizontal);
        Assert.True(charredReach.Up <= existingMaximum.Up);
    }
}
