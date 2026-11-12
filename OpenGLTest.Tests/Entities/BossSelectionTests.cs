using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Entities.Slimes;
using Xunit;

namespace VoxelEngine.Tests.Entities;

/// <summary>Which boss the HUD's boss bar shows: a living boss only, the nearest one, with its health share.</summary>
public sealed class BossSelectionTests
{
    public BossSelectionTests() => TestContent.EnsureLoaded();

    private static Sandworm Worm(float x) => new(EntityRegistry.Require("sandworm"), new Vector3(x, 0f, 0f));

    [Fact]
    public void OrdinaryMobsNeverShowABar()
    {
        var mobs = new List<Mob> { new Slime(EntityRegistry.Require("slime"), Vector3.Zero) };
        Assert.Null(BossSelection.Nearest(mobs, Vector3.Zero));
    }

    [Fact]
    public void TheNearestLivingBossWinsAndItsShareFollowsItsHealth()
    {
        var near = Worm(5f);
        var far = Worm(50f);
        near.TakeDamage(near.MaxHealth / 2);

        var bar = BossSelection.Nearest(new List<Mob> { far, near }, Vector3.Zero);

        Assert.NotNull(bar);
        Assert.Equal(near.Type.DisplayName, bar!.Value.Name);
        Assert.Equal(near.Health / (float)near.MaxHealth, bar.Value.Share);
        Assert.True(bar.Value.Share < 1f);
    }

    [Fact]
    public void ADyingBossHidesTheBar()
    {
        var worm = Worm(5f);
        worm.BeginDying();
        Assert.Null(BossSelection.Nearest(new List<Mob> { worm }, Vector3.Zero));
    }

    [Fact]
    public void ABossBarRespectsItsRange()
    {
        const float range = 20f;
        var boss = new RangedBoss(new Vector3(range - 1f, 0f, 0f), range);
        Assert.NotNull(BossSelection.Nearest(new List<Mob> { boss }, Vector3.Zero));
        Assert.Null(BossSelection.Nearest(new List<Mob> { boss }, new Vector3(-2f, 0f, 0f)));

        // A boss beyond its range never hides one still within reach, and an unlimited boss (the sandworm) shows
        // however far away it is.
        var worm = Worm(10_000f);
        var bar = BossSelection.Nearest(new List<Mob> { boss, worm }, new Vector3(-2f, 0f, 0f));
        Assert.NotNull(bar);
        Assert.Equal(worm.Type.DisplayName, bar!.Value.Name);
    }

    /// <summary>A boss whose bar shows only within <c>range</c> blocks of the viewer.</summary>
    private sealed class RangedBoss(Vector3 position, float range) : Mob(EntityRegistry.Require("slime"), position)
    {
        public override bool IsBoss => true;
        public override float BossBarRange => range;
        public override float BodyHalfExtent => 0.5f;
        public override float BodyHalfHeight => 0.5f;
        public override float HeadAboveCenter => 0.5f;
        public override Vector3 Velocity => Vector3.Zero;
        public override void Shove(Vector3 direction, float speed) { }
        protected override void SetVelocity(Vector3 velocity) { }
    }
}
