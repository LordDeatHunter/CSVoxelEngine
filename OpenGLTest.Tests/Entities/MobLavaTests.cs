using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Mobs burn in lava (<see cref="MobLava"/>, <see cref="Mob.UpdateHazards"/>): a body in lava loses
/// health tick by tick until it dies and drops its loot, a body beside it is untouched, and a brush
/// shorter than one tick costs nothing.
/// </summary>
public sealed class MobLavaTests
{
    public MobLavaTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    // A stone floor with a lava pool (y = 1..2) over x 0..7 and dry stone floor over x 8..15.
    private static ChunkManager HalfLava()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var chunk = chunks.Chunks[Vector3.Zero];
        var lava = ContentRegistry.RequireBlock("lava").Id;
        for (var x = 0; x < 8; x++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var y = 1; y <= 2; y++)
            chunk.SetBlockRaw(x, y, z, lava, 0);
        return chunks;
    }

    // Nobody to chase, so the mobs stay where they were put and nothing despawns
    private static readonly IMobTarget[] Nobody = [];

    [Fact]
    public void ASlimeInLavaBurnsToDeathAndDropsItsLoot()
    {
        var chunks = HalfLava();
        var drops = new DropManager();
        var manager = new SlimeManager(EntityRegistry.Require("slime"));
        var slime = manager.Spawn(new Vector3(3.5f, 1.05f + Slime.HalfExtent, 8.5f))!;

        var lowest = slime.Health;
        var flashed = false;
        for (var i = 0; i < (int)(20f / Dt) && !slime.IsDying; i++)
        {
            manager.Update(Dt, chunks, drops, Nobody);
            Assert.True(slime.Health <= lowest, "a slime in lava healed");
            if (slime.Health < lowest) flashed |= slime.HitFlash > 0f;
            lowest = slime.Health;
        }

        Assert.True(slime.IsDying, "the slime never burned to death");
        Assert.True(flashed, "the burns never flashed");
        Assert.NotEmpty(drops.Drops);
    }

    [Fact]
    public void AGoblinInLavaBurnsAndOneBesideItDoesNot()
    {
        var chunks = HalfLava();
        var drops = new DropManager();
        var manager = new GoblinManager(EntityRegistry.Require("goblin"));
        var burning = manager.Spawn(new Vector3(3.5f, 1.05f + Goblin.Height * 0.5f, 8.5f), ammo: 0)!;
        var dry = manager.Spawn(new Vector3(12.5f, 1.05f + Goblin.Height * 0.5f, 8.5f), ammo: 0)!;

        for (var i = 0; i < (int)(1.5f / Dt); i++)
            manager.Update(Dt, chunks, drops, Nobody);

        Assert.True(burning.Health < burning.MaxHealth);
        Assert.Equal(dry.MaxHealth, dry.Health);
    }

    [Fact]
    public void ABrushShorterThanATickCostsNothing()
    {
        var chunks = HalfLava();
        var slime = new Slime(EntityRegistry.Require("slime"), new Vector3(3.5f, 1.05f + Slime.HalfExtent, 8.5f));
        var almost = WorldGenerationConfig.MobLavaTickSeconds * 0.8f;
        for (var t = 0f; t < almost; t += Dt)
            slime.UpdateHazards(Dt, chunks);

        // Out of the lava the partial tick is forgotten, so going back in starts over
        var dryChunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        slime.UpdateHazards(Dt, dryChunks);
        for (var t = 0f; t < almost; t += Dt)
            slime.UpdateHazards(Dt, chunks);

        Assert.Equal(slime.MaxHealth, slime.Health);
    }

    [Fact]
    public void ALargerPoolNeverBurnsLessPerTick()
    {
        var previous = 0;
        for (var health = 1; health <= 200; health++)
        {
            var tick = MobLava.DamagePerTick(health);
            Assert.InRange(tick, 1, health);
            Assert.True(tick >= previous);
            previous = tick;
        }
    }
}
