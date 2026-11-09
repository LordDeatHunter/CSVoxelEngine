using System;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using Xunit;

namespace VoxelEngine.Tests.Entities.TowerMobs;

/// <summary>
/// The Skeleton King's own rules, driven headless: its shipped model satisfies its contract, it is an undead boss
/// whose boss bar replaces the floating healthbar, the replicated attack count names its three clips by remainder,
/// its leash turns it home only past the radius and puts back a king that fell off the roof, its shockwave reaches
/// only nearby feet, its volley fans symmetrically at one speed, and a target in reach is slammed once per swing.
/// </summary>
public sealed class SkeletonKingTests
{
    public SkeletonKingTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static EntityType Type => EntityRegistry.Require(SkeletonKing.EntityName);

    private static readonly Vector3 HomeFeet = new(8.5f, 1f, 8.5f);

    private static SkeletonKingHome Home => new((3, 4), HomeFeet, 6f, 2);

    private static Vector3 CenterAt(Vector3 feet) => feet + new Vector3(0f, MobConfig.SkeletonKingHeight * 0.5f, 0f);

    [Fact]
    public void ShippedModelSatisfiesItsContract()
    {
        SkeletonKing.ValidateContent(Type);
        foreach (var child in new[] { "Head", "ArmL", "ArmR" })
            Assert.Equal("Torso", Type.Model.Parts[child].Parent);
    }

    [Fact]
    public void TheKingIsAnUndeadBossWithABossBarInsteadOfAHealthbar()
    {
        var king = new SkeletonKing(Type, CenterAt(HomeFeet), Home, spawnIn: false);
        Assert.True(king.IsBoss);
        Assert.True(king.IsUndead);
        Assert.False(king.ShowsHealthbar);
        Assert.True(float.IsFinite(king.BossBarRange), "an arena boss's bar should not show from anywhere");
        Assert.Equal(king.MaxHealth, king.Health);
        Assert.Equal(Home, king.Home);
    }

    [Fact]
    public void TheAttackCountNamesTheThreeClipsByRemainder()
    {
        var king = new SkeletonKing(Type, CenterAt(HomeFeet), Home, spawnIn: false);
        for (var round = 0; round < 3; round++)
        {
            Assert.Equal(SkeletonKing.SlamClip, king.AttackClipFor(round * 3 + SkeletonKing.SlamVariant));
            Assert.Equal(SkeletonKing.VolleyClip, king.AttackClipFor(round * 3 + SkeletonKing.VolleyVariant));
            Assert.Equal(SkeletonKing.SummonClip, king.AttackClipFor(round * 3 + SkeletonKing.SummonVariant));
        }
        Assert.Equal(SkeletonKing.SlamClip, king.AttackClip);
    }

    [Fact]
    public void TheLeashTurnsTheKingHomeOnlyPastItsRadius()
    {
        Assert.Equal(Vector3.Zero, SkeletonKing.HomeMove(CenterAt(HomeFeet), Home));
        Assert.Equal(Vector3.Zero,
            SkeletonKing.HomeMove(CenterAt(HomeFeet + new Vector3(Home.LeashRadius * 0.9f, 0f, 0f)), Home));

        var away = SkeletonKing.HomeMove(CenterAt(HomeFeet + new Vector3(Home.LeashRadius * 2f, 0f, 0f)), Home);
        Assert.Equal(1f, away.Length(), 3);
        Assert.Equal(0f, away.Y);
        Assert.True(away.X < 0f, "the home move points away from home");
    }

    [Fact]
    public void EnforceLeashPutsBackAKingThatFellOrWasKnockedFar()
    {
        var home = new SkeletonKing(Type, CenterAt(HomeFeet), Home, spawnIn: false);
        Assert.False(home.EnforceLeash());
        Assert.Equal(CenterAt(HomeFeet), home.Position);

        var fallen = new SkeletonKing(Type,
            CenterAt(HomeFeet - new Vector3(0f, MobConfig.SkeletonKingLeashDropBlocks * 2f, 0f)), Home, spawnIn: false);
        Assert.True(fallen.EnforceLeash());
        Assert.Equal(CenterAt(HomeFeet), fallen.Position);
        Assert.Equal(Vector3.Zero, fallen.Velocity);

        var knocked = new SkeletonKing(Type,
            CenterAt(HomeFeet + new Vector3(Home.LeashRadius + MobConfig.SkeletonKingLeashMarginBlocks * 2f, 0f, 0f)),
            Home, spawnIn: false);
        Assert.True(knocked.EnforceLeash());
        Assert.Equal(CenterAt(HomeFeet), knocked.Position);
    }

    [Fact]
    public void TheShockwaveReachesOnlyNearbyFeet()
    {
        var feet = new Vector3(0f, 10f, 0f);
        Assert.True(SkeletonKing.InShockwave(feet, StubTarget.EyeAt(feet + new Vector3(MobConfig.SkeletonKingSlamRadius * 0.5f, 0f, 0f))));
        Assert.False(SkeletonKing.InShockwave(feet, StubTarget.EyeAt(feet + new Vector3(MobConfig.SkeletonKingSlamRadius + 1f, 0f, 0f))));
        Assert.False(SkeletonKing.InShockwave(feet, StubTarget.EyeAt(feet + new Vector3(1f, MobConfig.SkeletonKingSlamHeight + 2f, 0f))));

        // The shockwave reaches beyond the reach that starts a slam
        Assert.True(MobConfig.SkeletonKingSlamRadius > MobConfig.SkeletonKingSlamTriggerReach);
    }

    [Fact]
    public void ABurstBoneStraysOnlyAsFarAsItsOffsetsAndKeepsItsScaledSpeed()
    {
        var baseVelocity = new Vector3(18f, 6f, 5f);
        Assert.Equal(baseVelocity.X, SkeletonKing.ScatterVelocity(baseVelocity, 0f, 0f, 1f).X, 3);
        Assert.Equal(baseVelocity.Y, SkeletonKing.ScatterVelocity(baseVelocity, 0f, 0f, 1f).Y, 3);
        Assert.Equal(baseVelocity.Z, SkeletonKing.ScatterVelocity(baseVelocity, 0f, 0f, 1f).Z, 3);

        var baseYaw = MathF.Atan2(baseVelocity.Z, baseVelocity.X);
        var baseElevation = MathF.Atan2(baseVelocity.Y, new Vector2(baseVelocity.X, baseVelocity.Z).Length());
        foreach (var (yaw, pitch, scale) in new[] { (0.2f, 0f, 1f), (-0.15f, 0.08f, 0.9f), (0.05f, -0.07f, 1.1f) })
        {
            var bone = SkeletonKing.ScatterVelocity(baseVelocity, yaw, pitch, scale);
            Assert.Equal(baseVelocity.Length() * scale, bone.Length(), 3);
            Assert.Equal(baseYaw + yaw, MathF.Atan2(bone.Z, bone.X), 3);
            Assert.Equal(baseElevation + pitch, MathF.Atan2(bone.Y, new Vector2(bone.X, bone.Z).Length()), 3);
        }

        // The volley band stays inside the aimed throw's level range (a jittered bone may fall short or long: the spray)
        var levelRange = MobConfig.SkeletonKingVolleySpeed * MobConfig.SkeletonKingVolleySpeed / ThrownProjectile.Gravity;
        Assert.True(MobConfig.SkeletonKingVolleyMaxRange < levelRange * 0.9f);
    }

    [Fact]
    public void ABurstLetsItsBonesGoOneAfterAnotherAcrossItsWindowAndNeverSkipsOne()
    {
        var burst = new BoneBurst();
        burst.Start(5, 0.4f);
        Assert.True(burst.TryTake(), "the first bone did not leave at the release");
        Assert.False(burst.TryTake(), "two bones left at once");

        var taken = 1;
        for (var i = 0; i < 4; i++)
        {
            burst.Tick(0.1f + 1e-4f);
            Assert.True(burst.TryTake());
            Assert.False(burst.TryTake());
            taken++;
        }
        Assert.Equal(5, taken);
        Assert.False(burst.Active);

        // One long frame owes every bone that came due, and a stop drops the rest
        burst.Start(4, 0.3f);
        burst.Tick(1f);
        var due = 0;
        while (burst.TryTake()) due++;
        Assert.Equal(4, due);
        burst.Start(4, 0.3f);
        burst.Stop();
        burst.Tick(1f);
        Assert.False(burst.TryTake());
    }

    [Fact]
    public void ATargetInReachIsSlammedOncePerSwing()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var king = new SkeletonKing(Type, CenterAt(HomeFeet), Home, spawnIn: false, roll: () => 1f);
        var target = new StubTarget(StubTarget.EyeAt(HomeFeet + new Vector3(MobConfig.SkeletonKingSlamTriggerReach * 0.5f, 0f, 0f)));

        var swings = 0;
        var impacts = 0;
        var wasSwinging = false;
        for (var i = 0; i < (int)(15f / Dt) && impacts < 2; i++)
        {
            king.Update(Dt, chunks, target);
            var swinging = king.Animation.IsPlaying(SkeletonKing.SlamClip);
            if (swinging && !wasSwinging) swings++;
            wasSwinging = swinging;
            Assert.False(king.TryTakeBurstBone(out _, out _), "volleyed at a target in reach");
            if (!king.TryTakeSlamImpact()) continue;
            impacts++;
            Assert.True(swinging, "the slam landed after its clip had ended");
            Assert.False(king.TryTakeSlamImpact(), "one slam gave two impacts");
        }
        Assert.Equal(2, impacts);
        Assert.Equal(impacts, swings);
    }
}
