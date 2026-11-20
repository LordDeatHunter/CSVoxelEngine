using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>Content-facing leafless contract, separate from the cheap pure proxy facts.</summary>
[Collection("Worldgen shape cache")]
public sealed class LodTreeProxyContentTests
{
    [Fact]
    public void LeaflessCactusPlaceholderCannotBecomeAShortFarWoodProxy()
    {
        TestContent.EnsureLoaded();
        var cactus = TreeSpeciesRegistry.ByName["cactus"];
        Assert.False(cactus.HasLeaves);
        Assert.Throws<ArgumentException>(() => LodTreeProxy.FromFar(cactus, 12, -8, 40));
    }

    [Fact]
    public void EligibleProfilesKeepFarCrownsInsideGrownReach()
    {
        TestContent.EnsureLoaded();
        var seed = WorldSeed.Value;
        var profile = WorldSeed.Profile;
        try
        {
            foreach (var selected in new[] { WorldGenProfile.Dynamic, WorldGenProfile.Legacy })
            {
                WorldSeed.Apply(1337, selected);
                var eligible = new HashSet<TreeSpecies>();
                if (selected == WorldGenProfile.Dynamic)
                {
                    foreach (var biome in Enum.GetValues<Biome>())
                        eligible.UnionWith(TreeGenerator.DynamicRowFor(biome));
                }
                else
                {
                    foreach (var species in TreeGenerator.SpeciesByBiome)
                        if (species != null) eligible.Add(species);
                }
                eligible.UnionWith(TreeSpeciesRegistry.All.Where(s =>
                    s.Worldgen?.Allows(selected) == true && s.Worldgen.Oasis));
                Assert.NotEmpty(eligible);
                Assert.True(2 * TreeGenerator.ReachHorizontal <= WorldGenerationConfig.TreeCellBlocks);
                foreach (var species in eligible)
                {
                    var grown = TreeRasterizer.ReachOf(species).Horizontal;
                    Assert.True(grown <= TreeGenerator.ReachHorizontal, species.Name);
                    if (species.HasLeaves)
                        Assert.True(TreeProxyReach.FitsGrownReach(species.CrownRadius, grown), species.Name);
                }
            }
        }
        finally
        {
            WorldSeed.Apply(seed, profile);
        }
    }
}
