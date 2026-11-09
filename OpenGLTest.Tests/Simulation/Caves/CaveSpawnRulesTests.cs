using System;
using System.Collections.Generic;
using VoxelEngine.Simulation.Caves;
using VoxelEngine.WorldGen.Caves;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Caves;

/// <summary>
/// Which cave biomes hold which mobs (<see cref="CaveSpawnRules"/>): no cave slime in the magma deep, goblins
/// favoring the crystal and fungal caves over the rest and never taking the magma deep, each sub-biome holding its own
/// cave mob kind, and every mask agreeing with the predicate it is built from.
/// </summary>
public sealed class CaveSpawnRulesTests
{
    [Fact]
    public void NoCaveSlimeInTheMagmaDeepButSlimesElsewhere()
    {
        Assert.False(CaveSpawnRules.SlimeAllowedIn(CaveBiome.MagmaDeep));
        foreach (var biome in Enum.GetValues<CaveBiome>())
        {
            if (biome == CaveBiome.MagmaDeep) continue;
            Assert.True(CaveSpawnRules.SlimeAllowedIn(biome), $"no cave slime in {biome}");
        }
    }

    [Fact]
    public void GoblinsFavourCrystalAndFungalOverPlainAndNeverTheMagmaDeep()
    {
        var plain = CaveSpawnRules.GoblinWeight(CaveBiome.Plain);
        Assert.True(plain > 0f);
        Assert.True(CaveSpawnRules.GoblinWeight(CaveBiome.Crystal) > plain);
        Assert.True(CaveSpawnRules.GoblinWeight(CaveBiome.Fungal) > plain);
        Assert.Equal(0f, CaveSpawnRules.GoblinWeight(CaveBiome.MagmaDeep));
        foreach (var biome in Enum.GetValues<CaveBiome>())
            Assert.InRange(CaveSpawnRules.GoblinWeight(biome), 0f, 1f);
    }

    [Fact]
    public void MasksAgreeWithTheirPredicates()
    {
        foreach (var biome in Enum.GetValues<CaveBiome>())
        {
            Assert.True(CaveSpawnRules.Allows(CaveSpawnRules.AllBiomes, biome));
            Assert.Equal(CaveSpawnRules.SlimeAllowedIn(biome), CaveSpawnRules.Allows(CaveSpawnRules.SlimeMask, biome));
            Assert.Equal(CaveSpawnRules.GoblinWeight(biome) > 0f, CaveSpawnRules.Allows(CaveSpawnRules.GoblinMask, biome));
        }
    }

    [Fact]
    public void EachSubBiomeHoldsItsOwnKindAndNoneElse()
    {
        var subBiomes = new[] { CaveBiome.Crystal, CaveBiome.Fungal, CaveBiome.MagmaDeep };
        var kinds = new HashSet<string>();
        foreach (var biome in subBiomes)
        {
            var kind = CaveSpawnRules.CaveMobKindFor(biome);
            Assert.NotNull(kind);
            Assert.True(kinds.Add(kind), $"{biome} shares its kind with another sub-biome");
        }

        foreach (var biome in Enum.GetValues<CaveBiome>())
        {
            if (Array.IndexOf(subBiomes, biome) < 0) Assert.Null(CaveSpawnRules.CaveMobKindFor(biome));
            Assert.Equal(CaveSpawnRules.CaveMobKindFor(biome) != null,
                CaveSpawnRules.Allows(CaveSpawnRules.CaveMobMask, biome));
        }
    }
}
