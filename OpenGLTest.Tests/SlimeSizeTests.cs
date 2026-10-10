using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Slime sizes (<see cref="SlimeSize"/>): every scaled stat grows with the size, the smallest size
/// is the plain slime, and the live slime's body box, contact damage, health pool and loot follow
/// its size.
/// </summary>
public sealed class SlimeSizeTests
{
    public SlimeSizeTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static EntityType SlimeType => EntityRegistry.Require("slime");

    // A body centre resting on the stone floor (feet at y = 1) for a slime of this size.
    private static Vector3 GroundBody(float x, float z, int size) => new(x, 1f + Slime.HalfExtentFor(size), z);

    [Fact]
    public void EveryScaledStatGrowsWithTheSize()
    {
        var baseHealth = SlimeType.MaxHealth;
        Assert.True(SlimeSize.Max > SlimeSize.Min, "there must be more than one size");
        for (var size = SlimeSize.Min; size < SlimeSize.Max; size++)
        {
            Assert.True(SlimeSize.Scale(size + 1) > SlimeSize.Scale(size));
            Assert.True(SlimeSize.Health(baseHealth, size + 1) > SlimeSize.Health(baseHealth, size));
            Assert.True(SlimeSize.ContactDamage(size + 1) > SlimeSize.ContactDamage(size));
            Assert.True(SlimeSize.LootRolls(size + 1) > SlimeSize.LootRolls(size));
        }
    }

    [Fact]
    public void TheSmallestSizeIsThePlainSlime()
    {
        var slime = new Slime(SlimeType, GroundBody(2.5f, 2.5f, SlimeSize.Min));
        Assert.Equal(SlimeSize.Min, slime.Size);
        Assert.Equal(SlimeType.MaxHealth, slime.MaxHealth);
        Assert.Equal(slime.MaxHealth, slime.Health);
        Assert.Equal(Slime.HalfExtent, slime.BodyHalfExtent);
        Assert.Equal(WorldGenerationConfig.SlimeContactDamage, slime.ContactDamage);
        Assert.Equal(1, slime.LootRolls);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void AnOutOfRangeSizeIsClampedToARealOne(int size)
    {
        var slime = new Slime(SlimeType, GroundBody(2.5f, 2.5f, SlimeSize.Min), size);
        Assert.InRange(slime.Size, SlimeSize.Min, SlimeSize.Max);
        Assert.Equal(slime.MaxHealth, slime.Health);
    }

    [Fact]
    public void ALargerSlimeRestsOnTheGroundInALargerBody()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        Slime? smaller = null;
        for (var size = SlimeSize.Min; size <= SlimeSize.Max; size++)
        {
            var slime = new Slime(SlimeType, GroundBody(8.5f, 8.5f, size), size);
            // The player stands on the slime's own column, so it never hops
            for (var i = 0; i < 120; i++)
                slime.Update(Dt, chunks, new Vector3(8.5f, 1.62f, 8.5f));

            Assert.True(slime.IsGrounded);
            Assert.Equal(1f, slime.Position.Y - slime.BodyHalfHeight, 2);
            if (smaller != null)
            {
                Assert.True(slime.BodyHalfExtent > smaller.BodyHalfExtent);
                Assert.True(slime.HeadAboveCentre > smaller.HeadAboveCentre);
                Assert.True(slime.MaxHealth > smaller.MaxHealth);
            }
            smaller = slime;
        }
    }

    [Fact]
    public void ALargerSlimeHitsHarder()
    {
        int HitBy(int size)
        {
            var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
            var manager = new SlimeManager(SlimeType);
            var drops = new DropManager();
            var camera = new Camera(null, null);
            camera.TestTeleport(new Vector3(3.0f, 1f + Camera.EyeHeight, 2.5f), yaw: 0f, pitch: 0f);
            var slime = manager.Spawn(GroundBody(2.5f, 2.5f, size), size)!;
            for (var i = 0; i < 300; i++)
            {
                manager.Update(Dt, chunks, drops, camera);
                if (manager.DamageDealtThisFrame > 0)
                {
                    Assert.Equal(slime.ContactDamage, manager.DamageDealtThisFrame);
                    return manager.DamageDealtThisFrame;
                }
            }

            Assert.Fail($"a size {size} slime never hit the player beside it");
            return 0;
        }

        Assert.True(HitBy(SlimeSize.Max) > HitBy(SlimeSize.Min));
    }

    [Fact]
    public void ALargerSlimeDropsMoreLoot()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(8.5f, 1.62f, 8.5f), yaw: 0f, pitch: 0f);

        int TotalDrops(int size)
        {
            var drops = new DropManager();
            for (var trial = 0; trial < 50; trial++)
            {
                var manager = new SlimeManager(SlimeType);
                var slime = manager.Spawn(GroundBody(8.5f, 8.5f, size), size)!;
                slime.TakeDamage(slime.MaxHealth);
                manager.Update(Dt, chunks, drops, camera);
            }
            return drops.Drops.Count;
        }

        Assert.True(TotalDrops(SlimeSize.Max) > TotalDrops(SlimeSize.Min));
    }
}
