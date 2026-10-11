using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The goblin torch rule (<see cref="GoblinTorches"/>): the carry chance is higher at night and
/// below ground than by day for the same roll, a goblin never has a torch out with none carried,
/// and the carried torches drop on death.
/// </summary>
public sealed class GoblinTorchTests
{
    public GoblinTorchTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    [Fact]
    public void TheCarryChanceRisesAtNightAndUnderground()
    {
        Assert.True(GoblinTorches.Chance(0f, underground: false) < GoblinTorches.Chance(1f, underground: false));
        Assert.True(GoblinTorches.Chance(1f, underground: false) < GoblinTorches.Chance(0f, underground: true));
        Assert.True(GoblinTorches.Chance(0f, underground: false) < GoblinTorches.Chance(0f, underground: true));

        // For one roll, more light never means fewer torches
        foreach (var roll in new[] { 0.01f, 0.05f, 0.2f, 0.5f, 0.99f })
        {
            var day = GoblinTorches.Roll(0f, underground: false, roll);
            var night = GoblinTorches.Roll(1f, underground: false, roll);
            var cave = GoblinTorches.Roll(0f, underground: true, roll);
            Assert.True(night >= day, $"night rolled fewer torches than day for {roll}");
            Assert.True(cave >= night, $"underground rolled fewer torches than night for {roll}");
            Assert.InRange(day, 0, WorldGenerationConfig.GoblinTorchMaxCount);
        }

        // A roll past every threshold carries none, a tiny one carries the maximum
        Assert.Equal(0, GoblinTorches.Roll(1f, underground: true, 0.999f));
        Assert.Equal(WorldGenerationConfig.GoblinTorchMaxCount, GoblinTorches.Roll(1f, underground: true, 1e-6f));
    }

    [Fact]
    public void ATorchIsOnlyOutWhileItIsDark()
    {
        Assert.False(GoblinTorches.Lit(0, night: 1f, underground: true));
        Assert.False(GoblinTorches.Lit(1, night: 0f, underground: false));
        Assert.True(GoblinTorches.Lit(1, night: 1f, underground: false));
        Assert.True(GoblinTorches.Lit(1, night: 0f, underground: true));

        var goblin = new Goblin(EntityRegistry.Require("goblin"), new Vector3(8.5f, 1.5f, 8.5f),
            ammo: 0, spawnIn: false, torches: 1);
        Assert.Equal(1, goblin.Torches);
        goblin.SetDark(night: 0f, underground: false);
        Assert.False(goblin.TorchLit);
        Assert.Null(goblin.TorchFlamePoint);
        goblin.SetDark(night: 1f, underground: false);
        Assert.True(goblin.TorchLit);
        Assert.NotNull(goblin.TorchFlamePoint);

        // A goblin with none is never lit, whatever the light
        var bare = new Goblin(EntityRegistry.Require("goblin"), new Vector3(8.5f, 1.5f, 8.5f),
            ammo: 0, spawnIn: false);
        bare.SetDark(night: 1f, underground: true);
        Assert.False(bare.TorchLit);
        Assert.Null(bare.TorchFlamePoint);
    }

    [Fact]
    public void TheCarriedTorchesDropOnDeath()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(8.5f, 1f + Camera.EyeHeight, 2.5f), yaw: 0f, pitch: 0f);
        var goblins = new GoblinManager(EntityRegistry.Require("goblin"));
        var goblin = goblins.Spawn(new Vector3(8.5f, 1f + Goblin.Height * 0.5f, 8.5f), ammo: 0, torches: 2)!;
        var drops = new DropManager();
        goblin.TakeDamage(goblin.MaxHealth);
        goblins.Update(Dt, chunks, drops, camera);

        var torch = ContentRegistry.RequireBlock("torch").Id;
        var torches = 0;
        foreach (var drop in drops.Drops)
            if (drop.BlockId == torch) torches++;
        Assert.Equal(2, torches);
    }
}
