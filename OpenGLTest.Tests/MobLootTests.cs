using System;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The loot toss (<see cref="MobLoot"/>): every item a killed mob drops leaves its body moving up and
/// outward, faster sideways than a drop's own pop alone.
/// </summary>
public sealed class MobLootTests
{
    public MobLootTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    [Fact]
    public void TheTossGoesUpAndOutAndAFasterRollGoesFarther()
    {
        var previous = 0f;
        for (var s = 0f; s < 1f; s += 0.05f)
        {
            var toss = MobLoot.Toss(0.3f, s);
            Assert.True(toss.Y > 0f, "the toss does not lift the loot");
            var outward = MathF.Sqrt(toss.X * toss.X + toss.Z * toss.Z);
            Assert.True(outward > 0f, "the toss does not carry the loot out");
            Assert.True(outward >= previous, $"a faster roll tossed {outward}, less than {previous}");
            previous = outward;
        }

        // The heading roll turns the toss right round
        var a = MobLoot.Toss(0f, 0.5f);
        var b = MobLoot.Toss(0.5f, 0.5f);
        Assert.True(a.X * b.X + a.Z * b.Z < 0f, "opposite heading rolls toss the same way");
    }

    private static float MeanOutwardSpeed(DropManager drops)
    {
        var sum = 0f;
        foreach (var drop in drops.Drops)
        {
            Assert.True(drop.Velocity.Y > 0f, "a dropped item left the body moving down");
            sum += MathF.Sqrt(drop.Velocity.X * drop.Velocity.X + drop.Velocity.Z * drop.Velocity.Z);
        }
        return sum / drops.Drops.Count;
    }

    [Fact]
    public void AKilledMobsLootArcsOutFasterThanAPlainDrop()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(8.5f, 1f + Camera.EyeHeight, 2.5f), yaw: 0f, pitch: 0f);
        var body = new Vector3(8.5f, 1f + Goblin.Height * 0.5f, 8.5f);

        var killed = new DropManager();
        for (var trial = 0; trial < 40; trial++)
        {
            var goblins = new GoblinManager(EntityRegistry.Require("goblin"));
            var goblin = goblins.Spawn(body, ammo: 2)!;
            goblin.TakeDamage(goblin.MaxHealth);
            goblins.Update(Dt, chunks, killed, camera);

            var slimes = new SlimeManager(EntityRegistry.Require("slime"));
            var slime = slimes.Spawn(body)!;
            slime.TakeDamage(slime.MaxHealth);
            slimes.Update(Dt, chunks, killed, camera);
        }
        Assert.NotEmpty(killed.Drops);

        // The same number of drops spawned at the same spot with nothing but their own pop
        var plain = new DropManager();
        foreach (var drop in killed.Drops)
            plain.SpawnAt(drop.BlockId, body);

        Assert.True(MeanOutwardSpeed(killed) > MeanOutwardSpeed(plain),
            "the killed mobs' loot did not leave faster than a plain drop");
    }
}
