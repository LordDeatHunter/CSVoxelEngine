using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Loot;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>The copper dagger is the melee skeleton's weapon: only the warrior drops it, now and then.</summary>
public sealed class CopperDaggerLootTests
{
    public CopperDaggerLootTests() => TestContent.EnsureLoaded();

    private const int Kills = 20000;

    private static int DaggersIn(string entity)
    {
        var table = EntityRegistry.Require(entity).LootTable!;
        var dagger = ContentRegistry.RequireItem("copper_dagger").Id;
        var random = new Random(11);
        var daggers = 0;
        for (var i = 0; i < Kills; i++)
        {
            var into = new List<byte>();
            table.Roll(new LootContext(0, 0, 0), into, random);
            var count = into.Count(id => id == dagger);
            Assert.True(count <= 1, "a kill dropped more than one dagger");
            daggers += count;
        }

        return daggers;
    }

    [Fact]
    public void TheWarriorDropsTheDaggerRarelyButNotNever()
    {
        var rate = DaggersIn(Skeleton.WarriorEntityName) / (double)Kills;
        Assert.InRange(rate, 0.03, 0.07);
    }

    [Fact]
    public void TheArcherCarriesNoDaggerToDrop() => Assert.Equal(0, DaggersIn("skeleton"));
}
