using System;
using System.Numerics;
using VoxelEngine.Juice;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Juice;

public sealed class DamageDirectionTests
{
    public DamageDirectionTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;
    private const float ShowSeconds = WorldGenerationConfig.DamageArcHoldSeconds + WorldGenerationConfig.DamageArcFadeSeconds;

    // The knockback a blow from a source standing along `from` gives: away from it
    private static Vector3 AwayFrom(Vector3 from) => -from;

    [Fact]
    public void TheArcPointsAtTheSourceAndFollowsItAsTheViewTurns()
    {
        var arc = new DamageDirection();
        arc.Hit(AwayFrom(Vector3.UnitX));

        // Yaw 0 faces +X: the source is ahead, at the top edge
        AssertNear(new Vector2(0f, 1f), arc.ScreenDirection(0f));
        // Turned away to face -X it is behind, at the bottom edge
        AssertNear(new Vector2(0f, -1f), arc.ScreenDirection(180f));

        // For any facing, a source on the camera's own right (Cross(forward, up), the vector the view
        // and the strafe use) sits at the right edge and one on its left at the left edge.
        for (var yaw = -180f; yaw < 180f; yaw += 15f)
        {
            var forward = Vector3Utils.FromYawPitch(yaw, 0f);
            var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));

            var fromRight = new DamageDirection();
            fromRight.Hit(AwayFrom(right));
            AssertNear(new Vector2(1f, 0f), fromRight.ScreenDirection(yaw));

            var fromLeft = new DamageDirection();
            fromLeft.Hit(AwayFrom(-right));
            AssertNear(new Vector2(-1f, 0f), fromLeft.ScreenDirection(yaw));

            var fromAhead = new DamageDirection();
            fromAhead.Hit(AwayFrom(forward));
            AssertNear(new Vector2(0f, 1f), fromAhead.ScreenDirection(yaw));
        }
    }

    [Fact]
    public void ABlowWithNoDirectionShowsNoArcAndLeavesAnEarlierOneAlone()
    {
        var arc = new DamageDirection();
        arc.Hit(Vector3.Zero);
        arc.Hit(new Vector3(0f, -5f, 0f));
        arc.Hit(new Vector3(float.NaN, 0f, 1f));
        Assert.Equal(0f, arc.Intensity);

        arc.Hit(AwayFrom(Vector3.UnitZ));
        Step(arc, WorldGenerationConfig.DamageArcHoldSeconds + WorldGenerationConfig.DamageArcFadeSeconds * 0.5f);
        var fading = arc.Intensity;
        var direction = arc.ScreenDirection(0f);
        Assert.InRange(fading, 0.001f, 0.999f);

        // A fall or lava tick in the meantime neither refreshes nor turns it
        arc.Hit(Vector3.Zero);
        Assert.Equal(fading, arc.Intensity);
        Assert.Equal(direction, arc.ScreenDirection(0f));
    }

    [Fact]
    public void TheArcFadesOutAndTheNextBlowBringsItBack()
    {
        var arc = new DamageDirection();
        arc.Hit(AwayFrom(Vector3.UnitX));
        Assert.Equal(1f, arc.Intensity);

        var previous = arc.Intensity;
        for (var t = 0f; t < ShowSeconds + 0.5f; t += Dt)
        {
            arc.Update(Dt);
            Assert.True(arc.Intensity <= previous);
            previous = arc.Intensity;
        }
        Assert.Equal(0f, arc.Intensity);

        arc.Hit(AwayFrom(-Vector3.UnitX));
        Assert.Equal(1f, arc.Intensity);
        AssertNear(new Vector2(0f, -1f), arc.ScreenDirection(0f));
    }

    [Fact]
    public void AMobBlowOnTheCameraTurnsItsArcTowardTheMobAndAFallShowsNone()
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 1.62f, 0.5f), yaw: -90f, pitch: 0f);

        // A fall or lava tick kicks the view with no direction
        camera.AddHitKick(Vector3.Zero, 0.5f);
        Assert.Equal(0f, camera.DamageArcStrength);
        // A shove that dealt nothing is no blow
        camera.TakeHit(0, new Vector3(-1f, 0f, 0f), 4f, 0f);
        Assert.Equal(0f, camera.DamageArcStrength);

        // Facing -Z, a mob on the +X side is on the camera's right
        var mob = camera.Position + new Vector3(2f, 0f, 0f);
        var away = camera.Position - mob;
        camera.TakeHit(2, new Vector3(away.X, 0f, away.Z), 4f, 0f);
        Assert.Equal(1f, camera.DamageArcStrength);
        Assert.True(camera.DamageArcScreenDirection.X > 0.99f);

        // Turning to face the mob brings it to the top edge, then the arc fades out
        camera.TestSetCameraLook(0f, 0f);
        Assert.True(camera.DamageArcScreenDirection.Y > 0.99f);
        for (var t = 0f; t < ShowSeconds + 0.5f; t += Dt)
            camera.UpdateHeadMotion(Dt, 0f, moving: false, swimming: false);
        Assert.Equal(0f, camera.DamageArcStrength);
    }

    private static void Step(DamageDirection arc, float seconds)
    {
        for (var t = 0f; t < seconds; t += Dt)
            arc.Update(Dt);
    }

    private static void AssertNear(Vector2 expected, Vector2 actual)
    {
        Assert.InRange(actual.X, expected.X - 1e-3f, expected.X + 1e-3f);
        Assert.InRange(actual.Y, expected.Y - 1e-3f, expected.Y + 1e-3f);
    }
}
