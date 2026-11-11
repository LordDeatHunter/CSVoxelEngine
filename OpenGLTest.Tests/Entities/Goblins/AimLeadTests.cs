using System;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Entities.Goblins;

/// <summary>The rules of a ranged mob's lead: the shot meets a target that keeps moving as it moved at the
/// release, a jump or a fall is led down to the ground and no further, and the aim follows what the target does
/// during the wind-up rather than what it did when the wind-up began.</summary>
public sealed class AimLeadTests
{
    public AimLeadTests() => TestContent.EnsureLoaded();

    private const float Speed = 20f;
    private const float Gravity = ThrownProjectile.Gravity;
    private const float Dt = 1f / 60f;
    private static readonly Vector3 Origin = new(0.5f, 2f, 8.5f);

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    // How far the projectile passes from a target that moves on at `velocity` (airborne: on the player's
    // parabola down to floorY), at the moment the projectile has covered the ground to it
    private static float MissDistance(Vector3 launch, Vector3 chest, Vector3 velocity, float floorY)
    {
        var best = float.MaxValue;
        for (var t = 0f; t < 3f; t += 0.002f)
        {
            var shot = Origin + launch * t - new Vector3(0f, 0.5f * Gravity * t * t, 0f);
            var y = velocity.Y == 0f
                ? chest.Y
                : MathF.Max(chest.Y + velocity.Y * t - 0.5f * PlayerPhysics.Gravity * t * t, floorY);
            var target = new Vector3(chest.X + velocity.X * t, y, chest.Z + velocity.Z * t);
            best = MathF.Min(best, Vector3.Distance(shot, target));
        }

        return best;
    }

    [Theory]
    [InlineData(0f, 0f, 4.5f)]    // strafing across
    [InlineData(4.5f, 0f, 0f)]    // running straight away
    [InlineData(-4.5f, 0f, 0f)]   // charging
    [InlineData(3f, 0f, -3f)]     // diagonal
    public void TheShotMeetsATargetThatKeepsRunning(float vx, float vy, float vz)
    {
        var chest = Origin + new Vector3(9f, 0f, 0f);
        var velocity = new Vector3(vx, vy, vz);

        Assert.True(GoblinAim.TryLeadVelocity(Origin, chest, velocity, chest.Y, Speed, Gravity, out var launch));

        Assert.True(MissDistance(launch, chest, velocity, chest.Y) < 0.15f);
    }

    [Fact]
    public void AJumpIsLedAlongItsArcAndAFallOnlyDownToTheGround()
    {
        var chest = Origin + new Vector3(9f, 0f, 0f);

        // Leaving the ground: met in the air, above where a standing target would be shot
        var jump = new Vector3(0f, PlayerPhysics.JumpSpeed, 0f);
        GoblinAim.TryLeadVelocity(Origin, chest, jump, chest.Y, Speed, Gravity, out var atJumper);
        GoblinAim.TryLaunchVelocity(Origin, chest, Speed, Gravity, out var atStander);
        Assert.True(atJumper.Y > atStander.Y);
        Assert.True(MissDistance(atJumper, chest, jump, chest.Y) < 0.15f);

        // Coming down half a block over the ground: met on the ground, not under it
        var falling = new Vector3(0f, -8f, 0f);
        var floorY = chest.Y - 0.5f;
        GoblinAim.TryLeadVelocity(Origin, chest, falling, floorY, Speed, Gravity, out var atFaller);
        Assert.True(MissDistance(atFaller, chest, falling, floorY) < 0.15f);
    }

    [Fact]
    public void TheAimFollowsWhatTheTargetDoesDuringTheWindUp()
    {
        var chunks = StoneFloor();
        var runner = new Vector3(0f, 0f, 4.5f);

        // Running when the wind-up began, standing still since: shot where it stands
        var stopped = new StubTarget(StubTarget.EyeAt(new Vector3(9.5f, 1f, 8.5f))) { Velocity = runner };
        var aim = new AimTracker();
        aim.Lock(stopped, chunks);
        stopped.Velocity = Vector3.Zero;
        for (var i = 0; i < 60; i++) aim.Track(Dt, attacking: true);
        aim.Solve(Origin, Speed, Gravity, out var atStopped);
        Assert.True(MissDistance(atStopped, RealmMob.ChestOf(stopped.Eye), Vector3.Zero, 0f) < 0.15f);

        // Standing when the wind-up began, running since: led from where it has got to
        var started = new StubTarget(StubTarget.EyeAt(new Vector3(9.5f, 1f, 8.5f)));
        aim.Lock(started, chunks);
        started.Velocity = runner;
        for (var i = 0; i < 60; i++)
        {
            started.Eye += runner * Dt;
            aim.Track(Dt, attacking: true);
        }
        aim.Solve(Origin, Speed, Gravity, out var atStarted);
        Assert.True(MissDistance(atStarted, RealmMob.ChestOf(started.Eye), runner, 0f) < 0.15f);
    }

    [Fact]
    public void ATargetJigglingLeftAndRightIsShotWhereItStands()
    {
        var chunks = StoneFloor();
        var target = new StubTarget(StubTarget.EyeAt(new Vector3(9.5f, 1f, 8.5f)));
        var aim = new AimTracker();
        aim.Lock(target, chunks);

        // Flipping direction every other frame, never leaving the spot
        for (var i = 0; i < 60; i++)
        {
            target.Velocity = new Vector3(0f, 0f, i / 2 % 2 == 0 ? 4.5f : -4.5f);
            aim.Track(Dt, attacking: true);
        }
        aim.Solve(Origin, Speed, Gravity, out var launch);

        Assert.True(MissDistance(launch, RealmMob.ChestOf(target.Eye), Vector3.Zero, 0f) < 0.3f);
    }

    [Fact]
    public void AFallIsLedToTheGroundUnderTheTarget()
    {
        var chunks = StoneFloor();
        // Feet two blocks over the y = 1 floor top, coming down fast
        var target = new StubTarget(StubTarget.EyeAt(new Vector3(9.5f, 3f, 8.5f))) { Velocity = new Vector3(0f, -12f, 0f) };
        var aim = new AimTracker();
        aim.Lock(target, chunks);
        aim.Solve(Origin, Speed, Gravity, out var launch);

        var landedChest = RealmMob.ChestOf(StubTarget.EyeAt(new Vector3(9.5f, 1f, 8.5f)));
        Assert.True(MissDistance(launch, landedChest, Vector3.Zero, 0f) < 0.15f);
    }
}
