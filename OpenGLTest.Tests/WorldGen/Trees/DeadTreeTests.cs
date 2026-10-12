using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>
/// The dead tree species: leafless, sharing the oak wood, and planted only in the desert and the
/// frozen plains by worldgen.
/// </summary>
public sealed class DeadTreeTests
{
    public DeadTreeTests() => TestContent.EnsureLoaded();

    private static TreeSpecies Species => TreeSpeciesRegistry.Require("dead_tree");

    [Fact]
    public void TheDeadTreeIsALeaflessOakWoodedSnag()
    {
        var species = Species;
        Assert.False(species.HasLeaves, "the dead tree carries leaves");
        Assert.Equal(ContentRegistry.RequireBlock("oak_log").Id, species.WoodId);

        // It shares the oak's wood, so the rims must agree (the compiler enforces it at load)
        Assert.Equal(TreeSpeciesRegistry.Require("oak").Rim, species.Rim);
        Assert.NotNull(species.Worldgen);
    }

    [Fact]
    public void WorldgenPlantsItInTheDesertAndTheFrozenPlainsOnly()
    {
        var biomes = Species.Worldgen!.Biomes;
        Assert.Contains(Biome.Desert, biomes);
        Assert.Contains(Biome.Frozen, biomes);
        Assert.DoesNotContain(Biome.Forest, biomes);
        Assert.DoesNotContain(Biome.Taiga, biomes);
        Assert.DoesNotContain(Biome.Ocean, biomes);
    }
}
