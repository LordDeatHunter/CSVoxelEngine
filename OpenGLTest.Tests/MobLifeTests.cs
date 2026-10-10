using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class MobLifeTests
{
    public MobLifeTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static Slime NewSlime(float x = 2.5f) =>
        new(EntityRegistry.Require("slime"), new Vector3(x, 1f + Slime.HalfExtent, 2.5f));

    [Fact]
    public void ADyingMobTakesNoFurtherDamageAndDealsNone()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var slime = NewSlime();
        slime.BeginDying();
        var health = slime.Health;

        slime.TakeDamage(5);
        slime.Update(Dt, chunks, new Vector3(5.5f, 1.62f, 2.5f));

        Assert.Equal(health, slime.Health);
        Assert.False(slime.CanDealContactDamage);
    }

    [Fact]
    public void ADeathRunsFromZeroToOneAndThenIsRemovable()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var slime = NewSlime();
        Assert.False(slime.IsRemovable);
        Assert.Equal(0f, slime.DeathProgress);

        slime.BeginDying();
        slime.Update(slime.DeathSeconds * 0.5f, chunks, null);
        Assert.InRange(slime.DeathProgress, 0.4f, 0.6f);
        Assert.False(slime.IsRemovable);

        slime.Update(slime.DeathSeconds, chunks, null);
        Assert.True(slime.IsRemovable);
    }

    [Fact]
    public void AFreshMobPopsInAndSettlesAtFullScale()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var slime = NewSlime();
        Assert.Equal(0f, slime.SpawnProgress);

        for (var i = 0; i < 240; i++)
            slime.Update(Dt, chunks, null);

        Assert.Equal(1f, slime.SpawnProgress);
        Assert.InRange(slime.HorizontalScale, 0.9f, 1.1f);
        Assert.InRange(slime.VerticalScale, 0.9f, 1.1f);
    }

    [Fact]
    public void ASlimeCrouchesBeforeItLeavesTheGround()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var slime = NewSlime();
        var player = new Vector3(8.5f, 1.62f, 2.5f);
        var crouched = false;
        var wasWindingUpWhenLaunched = false;

        for (var i = 0; i < 600; i++)
        {
            var windingUp = slime.IsWindingUp;
            slime.Update(Dt, chunks, player);
            if (windingUp) crouched |= slime.VerticalScale < 0.95f;
            if (slime.IsAirborne)
            {
                wasWindingUpWhenLaunched = windingUp;
                break;
            }
        }

        Assert.True(wasWindingUpWhenLaunched);
        Assert.True(crouched);
    }

    [Fact]
    public void ALandingSquashesAndSpringsBack()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        // Dropped from height with nobody to chase: lands once and rings out.
        var slime = new Slime(EntityRegistry.Require("slime"), new Vector3(2.5f, 5f, 2.5f));
        var lowest = float.MaxValue;

        for (var i = 0; i < 90; i++)
        {
            slime.Update(Dt, chunks, null);
            if (slime.IsGrounded) lowest = MathF.Min(lowest, slime.VerticalScale);
        }

        Assert.True(lowest < 0.9f);

        for (var i = 0; i < 360; i++)
            slime.Update(Dt, chunks, null);

        Assert.InRange(slime.VerticalScale, 0.93f, 1.07f);
    }

    [Fact]
    public void AGoblinTurnsOverSeveralFramesInsteadOfSnapping()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var goblin = new Goblin(EntityRegistry.Require("goblin"), new Vector3(2.5f, 1f + Goblin.Height * 0.5f, 2.5f), 0);
        var first = new PlayerTarget(new Vector3(2.5f, 1.62f, -20f));
        goblin.Update(Dt, chunks, first);
        var facingFirst = goblin.Facing;

        var behind = new PlayerTarget(new Vector3(2.5f, 1.62f, 30f));
        goblin.Update(Dt, chunks, behind);

        // The opposite side is half a turn away; one frame covers part of it, not all of it.
        Assert.InRange(MathF.Abs(goblin.Facing - facingFirst), 0.01f, MathF.PI - 0.3f);

        for (var i = 0; i < 120; i++) goblin.Update(Dt, chunks, behind);
        Assert.InRange(MathF.Abs(MathF.Cos(goblin.Facing) - -1f), 0f, 0.05f);
    }

    [Fact]
    public void AHitFlinchesOverTheRunningActionWithoutCancellingIt()
    {
        var goblin = new Goblin(EntityRegistry.Require("goblin"), new Vector3(2.5f, 1f + Goblin.Height * 0.5f, 2.5f), 3);
        var animator = goblin.Animation;
        // Let the spawn clip end, then start a throw.
        for (var i = 0; i < 60; i++) animator.Update(Dt, 0f);
        Assert.True(animator.Play(Goblin.ThrowClip));

        goblin.TakeDamage(1);

        Assert.True(animator.IsPlaying(Goblin.ThrowClip));
        Assert.True(animator.IsOverlayPlaying);

        // The overlay is authored to return to rest, so it ends on its own.
        for (var i = 0; i < 60; i++) animator.Update(Dt, 0f);
        Assert.False(animator.IsOverlayPlaying);
    }

    [Fact]
    public void AnOverlayRotatesTheBonesItKeysAndLeavesTheRest()
    {
        var goblin = new Goblin(EntityRegistry.Require("goblin"), new Vector3(2.5f, 1f + Goblin.Height * 0.5f, 2.5f), 0);
        var animator = goblin.Animation;
        for (var i = 0; i < 60; i++) animator.Update(Dt, 0f);
        var before = animator.LocalTransform("LegL").Rotation;

        animator.PlayOverlay(Goblin.HurtClip);
        for (var i = 0; i < 4; i++) animator.Update(Dt, 0f);

        Assert.Equal(before, animator.LocalTransform("LegL").Rotation);
        Assert.NotEqual(Quaternion.Identity, animator.LocalTransform("Torso").Rotation);
    }

    private sealed class PlayerTarget(Vector3 eye) : IMobTarget
    {
        public Vector3 Eye { get; } = eye;
        public bool IsImmune => true;
        public void TakeHit(int damage, Vector3 away, float speed, float lift) { }
    }
}
