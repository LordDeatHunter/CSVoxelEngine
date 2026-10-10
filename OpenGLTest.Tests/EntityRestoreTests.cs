using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The entity save-restore seams: <see cref="Mob.RestoreState"/> puts a mob's health, facing and
/// velocity back (health clamped into the living range of its kind), and
/// <see cref="DropManager.SpawnRestored"/> puts a drop back with its exact motion and age, the
/// age carrying the pickup delay.
/// </summary>
public sealed class EntityRestoreTests
{
    public EntityRestoreTests() => TestContent.EnsureLoaded();

    [Fact]
    public void MobRestoreRoundTripsAndClamps()
    {
        var slimes = new SlimeManager(EntityRegistry.Require("slime"));
        var slime = slimes.Spawn(new Vector3(2.5f, 1f + Slime.HalfExtent, 2.5f));
        var max = slime.Type.MaxHealth;
        var facing = 1.75f;
        var velocity = new Vector3(0.5f, -1.25f, 2f);

        slime.RestoreState(max - 1, facing, velocity);
        Assert.Equal(max - 1, slime.Health);
        Assert.Equal(facing, slime.Facing);
        Assert.Equal(velocity, slime.Velocity);

        // Never above the kind's pool, even when the save predates a retuned hp
        slime.RestoreState(max * 4, facing, velocity);
        Assert.Equal(max, slime.Health);

        // Never dead on arrival
        slime.RestoreState(0, facing, velocity);
        Assert.True(slime.Health >= 1);
        Assert.False(slime.IsDead);

        // A larger slime's pool is its own: a saved health above the kind's base pool survives, and
        // the clamp is the size's pool
        var large = slimes.Spawn(new Vector3(4.5f, 1f + Slime.HalfExtentFor(SlimeSize.Max), 4.5f), SlimeSize.Max)!;
        Assert.True(large.MaxHealth > max);
        large.RestoreState(max + 1, facing, velocity);
        Assert.Equal(max + 1, large.Health);
        large.RestoreState(large.MaxHealth * 4, facing, velocity);
        Assert.Equal(large.MaxHealth, large.Health);

        var goblins = new GoblinManager(EntityRegistry.Require("goblin"));
        var goblin = goblins.Spawn(new Vector3(6.5f, 1f + Goblin.Height * 0.5f, 6.5f), ammo: 5);
        var goblinHealth = goblin.Type.MaxHealth - 1;
        var goblinFacing = -0.5f;
        var goblinVelocity = new Vector3(-1f, 0f, 0.25f);

        goblin.RestoreState(goblinHealth, goblinFacing, goblinVelocity);
        Assert.Equal(5, goblin.Ammo);
        Assert.Equal(goblinHealth, goblin.Health);
        Assert.Equal(goblinFacing, goblin.Facing);
        Assert.Equal(goblinVelocity, goblin.Velocity);
    }

    [Fact]
    public void RestoredDropKeepsItsMotionAndAge()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var drops = new DropManager();
        var position = new Vector3(3.25f, 12.5f, -4.75f);
        var velocity = new Vector3(0.5f, 2f, -0.25f);

        Assert.True(drops.SpawnRestored(stone, position, velocity, age: 2.5f));
        Assert.Single(drops.Drops);
        Assert.Equal(position, drops.Drops[0].Position);
        Assert.Equal(velocity, drops.Drops[0].Velocity);
        Assert.Equal(2.5f, drops.Drops[0].Age);

        Assert.False(drops.SpawnRestored(0, position, velocity, age: 0f));
        Assert.Single(drops.Drops);
    }

    [Fact]
    public void ARestoredAgeCarriesThePickupDelay()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var drops = new DropManager();
        var chunks = new ChunkManager(null!);
        var positionA = new Vector3(0.5f, 10f, 0.5f);
        var positionB = new Vector3(40.5f, 10f, 40.5f);

        // A has already served its pickup delay when it was saved; B was saved the instant it
        // appeared
        drops.SpawnRestored(stone, positionA, Vector3.Zero, age: 1f);
        drops.SpawnRestored(stone, positionB, Vector3.Zero, age: 0f);
        var dropA = drops.Drops[0];
        var dropB = drops.Drops[1];

        // DropManager aims at the eye minus its collect height offset, so lifting the eye by
        // that offset puts the target right on the drop; an empty manager reads as air, and one
        // frame of gravity is negligible against the collect distance
        var lift = Vector3.UnitY * DropManager.CollectHeightOffset;
        drops.Update(1f / 60, dropA.Position + lift, chunks);
        drops.Update(1f / 60, dropB.Position + lift, chunks);

        Assert.DoesNotContain(dropA, drops.Drops);
        Assert.Contains(dropB, drops.Drops);
    }
}
