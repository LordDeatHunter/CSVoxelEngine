using System;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Models;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Items;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Entities;

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
        Assert.Equal(fast * PlayerConfig.ThrowMinCharge, slow, precision: 3);
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
        Mob? hit = null;
        for (var i = 0; i < 120 && alive && hit == null; i++)
            alive = projectile.Update(1f / 60f, chunks, [slime], out hit);
        Assert.NotNull(hit);
        Assert.Same(slime, hit);

        var wallChunks = MeshTestWorld.WalledArea().Manager;
        wallChunks.Chunks[Vector3.Zero].SetBlockRaw(2, 0, 0,
            ContentRegistry.RequireBlock("stone").Id, 0);
        var blocked = new ThrownProjectile(1, new Vector3(0f, 0.5f, 0.5f), new Vector3(20f, 2f, 0f), 2f, 1f);
        var far = new Slime(slimeType, new Vector3(5f, 0.5f, 0.5f));
        Mob? wallHit = null;
        var wallAlive = true;
        for (var i = 0; i < 120 && wallAlive && wallHit == null; i++)
            wallAlive = blocked.Update(1f / 60f, wallChunks, [far], out wallHit);
        Assert.Null(wallHit);
        Assert.False(wallAlive);
    }

    public static TheoryData<float, float, float> Headings => new()
    {
        { 0f, 0f, -1f }, { 1f, 0f, 0f }, { 0.3f, -0.8f, 0.5f }, { 0f, 1f, 0f }, { 0f, -1f, 0f }, { -4f, 2f, 7f },
    };

    [Theory]
    [MemberData(nameof(Headings))]
    public void AnAlignedProjectilePointsItsTipAlongItsVelocityWithoutDistortingTheSprite(float x, float y, float z)
    {
        var velocity = new Vector3(x, y, z) * 13f;
        var rotation = ThrownProjectile.AlignedRotation(velocity);

        var tip = Vector3.TransformNormal(Vector3.Normalize(new Vector3(1f, 1f, 0f)), rotation);
        Assert.True(Vector3.Dot(tip, Vector3.Normalize(velocity)) > 0.9999f);
        Assert.Equal(1f, rotation.GetDeterminant(), 3);
        // A pure rotation keeps lengths, so the sprite is neither squashed nor mirrored
        Assert.Equal(1f, Vector3.TransformNormal(Vector3.UnitZ, rotation).Length(), 3);
        Assert.Equal(1f, Vector3.TransformNormal(Vector3.UnitX, rotation).Length(), 3);
    }

    [Theory]
    [MemberData(nameof(Headings))]
    public void AHeldModelAuthoredTipUpFliesTipFirstLikeASprite(float x, float y, float z)
    {
        // The model stands in for the sprite in the sprite's space, so the one aligned rotation serves both
        var velocity = new Vector3(x, y, z) * 13f;
        var drawn = HeldModel.ToSpriteSpace * ThrownProjectile.AlignedRotation(velocity);

        var tip = Vector3.TransformNormal(Vector3.UnitY, drawn);
        Assert.True(Vector3.Dot(tip, Vector3.Normalize(velocity)) > 0.9999f);
        Assert.Equal(1f, drawn.GetDeterminant(), 3);
    }

    [Theory]
    [MemberData(nameof(Headings))]
    public void AWoundUpPointFirstItemLiesAlongTheAimInThePalm(float x, float y, float z)
    {
        var aim = Vector3.Normalize(new Vector3(x, y, z));
        var hand = Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(3f, 20f, -5f);
        var held = HeldItemPose.Pose(hand, Item.ComposeDisplay(new Vector3(10f, -90f, 45f), Vector3.Zero, 1.3f), 0.5f,
            HeldItemPose.HeldItemGrip);

        // At rest the aim changes nothing; fully cocked the tip (the sprite's diagonal) is on the aim, as it will fly
        Assert.Equal(held, HeldItemPose.Aimed(held, hand, 0f, aim, 0.65f, HeldItemPose.HeldItemGrip));
        var cocked = HeldItemPose.Aimed(held, hand, 1f, aim, 0.65f, HeldItemPose.HeldItemGrip);
        var tip = Vector3.Normalize(Vector3.TransformNormal(new Vector3(1f, 1f, 0f), cocked));
        Assert.True(Vector3.Dot(tip, aim) > 0.999f);
        // Its size in the hand is kept, and its middle stays within reach of the palm
        Assert.Equal(0.65f, Vector3.TransformNormal(Vector3.UnitZ, cocked).Length(), 3);
        var palm = Vector3.Transform(HeldItemPose.HeldItemGrip, hand);
        Assert.True(Vector3.Distance(cocked.Translation, palm) < 0.2f);
    }

    [Fact]
    public void AReleaseBeforeTheNeededChargeThrowsNothing()
    {
        var javelin = ContentRegistry.RequireItem("javelin");
        var flint = ContentRegistry.RequireItem("flint");
        Assert.True(javelin.ThrowChargeNeeded > 0f);

        Assert.False(javelin.ThrowsAt(javelin.ThrowChargeNeeded * 0.9f));
        Assert.True(javelin.ThrowsAt(javelin.ThrowChargeNeeded));
        Assert.True(javelin.ThrowsAt(1f));
        // An item that names no wind-up leaves on any release; one that cannot be thrown never does
        Assert.True(flint.ThrowsAt(0f));
        Assert.False(ContentRegistry.RequireItem("bandage").ThrowsAt(1f));
    }

    [Fact]
    public void OnlyTheJavelinFliesPointFirst()
    {
        var javelin = ContentRegistry.RequireItem("javelin");
        Assert.True(javelin.IsThrowable && javelin.ThrowAligned);
        Assert.False(ContentRegistry.RequireItem("flint").ThrowAligned);
        Assert.True(new ThrownProjectile(javelin.Id, Vector3.Zero, Vector3.UnitX, 1f, 1f).Aligned);
        Assert.False(new ThrownProjectile(ContentRegistry.RequireItem("flint").Id, Vector3.Zero, Vector3.UnitX, 1f, 1f).Aligned);
    }
}
