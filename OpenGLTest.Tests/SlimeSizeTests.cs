using System;
using System.Numerics;
using silkgltriangle.Content;
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

    // The same evenly spread rolls for every condition, so the shares compare like for like.
    private static float[] EvenRolls(int count)
    {
        var rolls = new float[count];
        for (var i = 0; i < count; i++)
            rolls[i] = (i + 0.5f) / count;
        return rolls;
    }

    private static float LargeShare(float night, bool underground)
    {
        var rolls = EvenRolls(1000);
        var large = 0;
        foreach (var roll in rolls)
            if (SlimeSize.Roll(night, underground, roll) > SlimeSize.Min)
                large++;
        return large / (float)rolls.Length;
    }

    [Fact]
    public void ByDayOnTheSurfaceMostSlimesAreSmallAndLargeOnesAreCommonerAtNightOrUnderground()
    {
        var day = LargeShare(night: 0f, underground: false);
        var night = LargeShare(night: 1f, underground: false);
        var cave = LargeShare(night: 0f, underground: true);

        Assert.True(day < 0.5f, $"by day {day:P0} of the slimes are large");
        Assert.True(day > 0f, "by day a larger slime must still be possible");
        Assert.True(night > day, $"night {night:P0} vs day {day:P0}");
        Assert.True(cave > day, $"underground {cave:P0} vs day {day:P0}");
    }

    [Fact]
    public void ForOneRollADarkerOrDeeperSpawnIsNeverSmaller()
    {
        foreach (var roll in EvenRolls(400))
        {
            var day = SlimeSize.Roll(0f, false, roll);
            var dusk = SlimeSize.Roll(0.5f, false, roll);
            var night = SlimeSize.Roll(1f, false, roll);
            var cave = SlimeSize.Roll(0f, true, roll);
            Assert.InRange(day, SlimeSize.Min, SlimeSize.Max);
            Assert.InRange(night, SlimeSize.Min, SlimeSize.Max);
            Assert.True(day <= dusk && dusk <= night, $"roll {roll}: day {day}, dusk {dusk}, night {night}");
            Assert.True(day <= cave, $"roll {roll}: day {day}, underground {cave}");
        }
    }

    [Fact]
    public void OnlyASpawnWellUnderItsColumnSurfaceCountsAsUnderground()
    {
        const int surface = 64;
        Assert.False(SlimeSize.IsUnderground(surface, surface));
        Assert.True(SlimeSize.IsUnderground(surface - 40, surface));

        // Deeper never turns it back into a surface spawn
        var wasUnderground = false;
        for (var depth = 0; depth <= 40; depth++)
        {
            var underground = SlimeSize.IsUnderground(surface - depth, surface);
            Assert.True(underground || !wasUnderground, $"depth {depth} reads as the surface again");
            wasUnderground = underground;
        }
    }

    [Fact]
    public void TheNightFactorIsFullWithTheSunWellBelowTheHorizonAndNoneWithItWellUp()
    {
        var steps = 2400;
        for (var i = 0; i < steps; i++)
        {
            var time = i * (double)WorldGenerationConfig.DayLengthSeconds / steps;
            var elevation = DayCycle.SunDirection(time).Y;
            var night = DayCycle.NightFactor(time);
            Assert.InRange(night, 0f, 1f);
            if (elevation > 0.3f) Assert.Equal(0f, night);
            if (elevation < -0.1f) Assert.Equal(1f, night);
        }

        // A higher sun is never more night
        for (var e = -0.5f; e < 0.5f; e += 0.01f)
            Assert.True(DayCycle.Dayness(e + 0.01f) >= DayCycle.Dayness(e));
    }

    [Fact]
    public void ARingSpawnedSlimeOfAnyRolledSizeFitsWhereItStands()
    {
        var chunks = MobTestWorld.Surface();
        var player = new Vector3(0.5f, 100f, 0.5f);
        var found = 0;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            // Full night and the lowest roll ask for the largest size the spot allows
            if (!SlimeManager.TryFindSpawn(chunks, player, 1f, 0f, out var position, out var size))
                continue;
            found++;
            Assert.InRange(size, SlimeSize.Min, SlimeSize.Max);
            var half = Slime.HalfExtentFor(size);
            Assert.False(Slime.IsColliding(position, half, chunks), $"a size {size} slime spawned inside a block");
            var feet = position.Y - half;
            Assert.Equal(MathF.Round(feet), feet, 3);
        }

        Assert.True(found > 0);
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
