using System;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>
/// The dead tree species: leafless, sharing the oak wood, and planted by worldgen only where no
/// leafy species grows.
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
    public void ItNeverSharesABiomeWithALeafyWorldgenSpecies()
    {
        var worldgen = Species.Worldgen!;
        var grows = 0;
        foreach (var biome in Enum.GetValues<Biome>())
        {
            if (!worldgen.GrowsIn(biome))
                continue;
            grows++;

            // A snag standing among a leafy species' trees would read as a dead forest tree. An
            // oasis species is planted only inside its own disc, never by the ordinary placement.
            foreach (var other in TreeSpeciesRegistry.All)
            {
                if (other.Worldgen == null || other.Worldgen.Oasis || !other.HasLeaves)
                    continue;
                Assert.False(other.Worldgen.GrowsIn(biome),
                    $"the dead tree shares {biome} with the leafy species {other.Name}");
            }
        }

        Assert.True(grows > 0, "the dead tree grows in no biome");
    }
}
