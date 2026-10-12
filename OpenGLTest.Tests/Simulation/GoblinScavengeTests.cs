using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// Goblins scavenging flint (<see cref="GoblinManager.AppendCollectors"/>): a live goblin with
/// ammo room pulls a flint drop in like a player's pickup and takes it as a stone; a goblin at the
/// cap is no collector at all.
/// </summary>
public sealed class GoblinScavengeTests
{
    public GoblinScavengeTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static Vector3 Body(float x, float z) => new(x, 1f + Goblin.Height * 0.5f, z);

    [Fact]
    public void AGoofinPullsInFlintAndTakesItAsAmmo()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var flint = ContentRegistry.RequireItem("flint").Id;
        var goblins = new GoblinManager(EntityRegistry.Require("goblin"));
        var goblin = goblins.Spawn(Body(8.5f, 8.5f), ammo: 0)!;

        var drops = new DropManager();
        drops.SpawnRestored(flint, new Vector3(8.5f, 1.5f, 8.5f), Vector3.Zero, age: 5f);

        var collectors = new List<DropCollector>();
        var got = false;
        for (var i = 0; i < 300 && !got; i++)
        {
            collectors.Clear();
            goblins.AppendCollectors(collectors);
            Assert.Single(collectors);
            drops.Update(Dt, collectors, chunks);
            for (var c = 0; c < drops.Collected.Length; c++)
            {
                goblins.AwardScavenged(drops.CollectedBy[c], drops.Collected[c]);
                got = true;
            }
        }

        Assert.True(got, "the goblin never pulled the flint in");
        Assert.Equal(1, goblin.Ammo);
    }

    [Fact]
    public void AGoblinAtTheAmmoCapIsNoCollector()
    {
        var goblins = new GoblinManager(EntityRegistry.Require("goblin"));
        goblins.Spawn(Body(8.5f, 8.5f), ammo: WorldGenerationConfig.GoblinStoneMax);
        var full = new List<DropCollector>();
        goblins.AppendCollectors(full);
        Assert.Empty(full);

        // Taking one off its cap makes it collect again, and the cap holds through a credit
        goblins.Spawn(Body(4.5f, 4.5f), ammo: WorldGenerationConfig.GoblinStoneMax - 1);
        var room = new List<DropCollector>();
        goblins.AppendCollectors(room);
        Assert.Single(room);
        var flint = ContentRegistry.RequireItem("flint").Id;
        goblins.AwardScavenged(0, flint);
        goblins.AwardScavenged(0, flint);
        var after = new List<DropCollector>();
        goblins.AppendCollectors(after);
        Assert.Empty(after);
    }
}
