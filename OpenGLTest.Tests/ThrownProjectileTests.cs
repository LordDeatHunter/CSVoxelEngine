using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class ThrownProjectileTests
{
    public ThrownProjectileTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ChargeScalesDamageFromHalfToFull()
    {
        var full = ThrownProjectile.DamageFor(4f, 1f);
        Assert.Equal((int)MathF.Round(4f, MidpointRounding.AwayFromZero), full);
        Assert.Equal((int)MathF.Round(2f, MidpointRounding.AwayFromZero), ThrownProjectile.DamageFor(4f, 0f));
        Assert.True(ThrownProjectile.DamageFor(4f, 1f) > ThrownProjectile.DamageFor(4f, 0f));
        Assert.Equal(ThrownProjectile.DamageFor(4f, 0.5f),
            (int)MathF.Round(4f * ThrownProjectile.ChargeFactor(0.5f), MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void HarmlessThrowStaysHarmlessAtAnyCharge()
    {
        Assert.Equal(0, ThrownProjectile.DamageFor(0f, 0f));
        Assert.Equal(0, ThrownProjectile.DamageFor(0f, 1f));
    }

    [Fact]
    public void LaunchSpeedScalesWithCharge()
    {
        var slow = ThrownProjectile.LaunchVelocity(Vector3.UnitX, 0f).Length();
        var fast = ThrownProjectile.LaunchVelocity(Vector3.UnitX, 1f).Length();
        Assert.True(fast > slow);
        Assert.Equal(fast * WorldGenerationConfig.ThrowMinCharge, slow, precision: 3);
    }

    [Fact]
    public void ThrowableFlagFollowsThrowDamagePresence()
    {
        var spec = new ItemFactory.ItemSpec(7, "x", "t", ItemUseAction.None,
            0, 0, 0, 1f, 0, 0f, 1, 1, 1, ItemAnimationMode.None, 8f,
            Vector3.Zero, Vector3.Zero, 1f, false, 0.5f, 300f, AttackPattern.Punch, 0.5f, 1f, null, "x");
        Assert.False(ItemFactory.Create("plain", spec).IsThrowable);
        var harmless = spec with { ThrowDamage = 0f };
        Assert.True(ItemFactory.Create("mud", harmless).IsThrowable);
    }

    [Fact]
    public void RequestedThrowablesAreThrowableWithOrderedDamage()
    {
        var snow = ContentRegistry.RequireItem("snow");
        var mud = ContentRegistry.RequireItem("mudball");
        var flint = ContentRegistry.RequireItem("flint");
        var obsidian = ContentRegistry.RequireItem("obsidian");
        Assert.True(snow.IsThrowable && mud.IsThrowable && flint.IsThrowable && obsidian.IsThrowable);
        Assert.Equal(snow.ThrowDamage, mud.ThrowDamage);
        Assert.True(flint.ThrowDamage > snow.ThrowDamage);
        Assert.True(obsidian.ThrowDamage > flint.ThrowDamage);
    }

    [Fact]
    public void ProjectileHitsSlimeInPathAndMissesBeyondWall()
    {
        var (chunks, _) = MeshTestWorld.WalledArea();
        var slimeType = EntityRegistry.Require("slime");
        var slime = new Slime(slimeType, new Vector3(3f, 0.5f, 0.5f));
        var projectile = new ThrownProjectile(1, new Vector3(0f, 0.5f, 0.5f), new Vector3(20f, 2f, 0f), 2f, 1f);
        var alive = true;
        Slime? hit = null;
        for (var i = 0; i < 120 && alive && hit == null; i++)
            alive = projectile.Update(1f / 60f, chunks, [slime], out hit);
        Assert.NotNull(hit);
        Assert.Same(slime, hit);

        var wallChunks = MeshTestWorld.WalledArea().Manager;
        wallChunks.Chunks[Vector3.Zero].SetBlockRaw(2, 0, 0,
            ContentRegistry.RequireBlock("stone").Id, 0);
        var blocked = new ThrownProjectile(1, new Vector3(0f, 0.5f, 0.5f), new Vector3(20f, 2f, 0f), 2f, 1f);
        var far = new Slime(slimeType, new Vector3(5f, 0.5f, 0.5f));
        Slime? wallHit = null;
        var wallAlive = true;
        for (var i = 0; i < 120 && wallAlive && wallHit == null; i++)
            wallAlive = blocked.Update(1f / 60f, wallChunks, [far], out wallHit);
        Assert.Null(wallHit);
        Assert.False(wallAlive);
    }
}
