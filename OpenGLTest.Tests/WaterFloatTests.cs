using System;
using System.Numerics;
using Silk.NET.Input;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The water depth states' movement (<see cref="PlayerPhysics"/> over <see cref="WaterImmersion"/>):
/// a body in deep water floats at the surface instead of sinking, a stroke up hops it and it settles
/// back, a down stroke dives under for good, and a shallow pool is waded through on foot.
/// </summary>
public sealed class WaterFloatTests
{
    public WaterFloatTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;
    // MobTestWorld.Pool(12): the surface 8/9 into the y = 12 cell
    private const float DeepSurface = 12f + FluidState.SourceHeight;

    // Facing +X; a walk starts near the -X edge so it stays inside the one loaded chunk
    private static Camera BodyAt(float feetY, float x = 8.5f)
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(x, feetY + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        return camera;
    }

    private static float Feet(Camera camera) => camera.Position.Y - Camera.EyeHeight;

    private static void Run(Camera camera, ChunkManager chunks, ScriptedKeyboard keys, float seconds,
        Action<Camera>? eachFrame = null)
    {
        for (var t = 0f; t < seconds; t += Dt)
        {
            camera.Update(Dt, keys, chunks);
            eachFrame?.Invoke(camera);
        }
    }

    // The feet height at which WaterFloatRestShare of the body is under the deep pool's surface
    private static float RestFeet => DeepSurface - WorldGenerationConfig.WaterFloatRestShare * PlayerPhysics.PlayerHeight;

    [Fact]
    public void ABodyInDeepWaterComesToRestFloatingAndNeverWades()
    {
        var pool = MobTestWorld.Pool(12);
        // The eye just out of the water, the rest of the body under
        var camera = BodyAt(DeepSurface - Camera.EyeHeight + 0.05f);
        Run(camera, pool, new ScriptedKeyboard(), 6f,
            c => Assert.True(c.WaterPhase >= WaterPhase.Float, $"{c.WaterPhase} at feet {Feet(c)}"));

        Assert.Equal(WaterPhase.Float, camera.WaterPhase);
        Assert.InRange(Feet(camera), RestFeet - 0.05f, RestFeet + 0.05f);
        Assert.InRange(camera.VerticalVelocity, -0.05f, 0.05f);
        Assert.False(camera.IsEyeInWater);
        Assert.True(camera.IsInWater);
    }

    [Fact]
    public void AFallIntoDeepWaterAndABodyThatNeverDivedBothEndAtTheFloat()
    {
        var pool = MobTestWorld.Pool(12);
        var keys = new ScriptedKeyboard();

        var dropped = BodyAt(DeepSurface + 3f);
        Run(dropped, pool, keys, 10f);
        Assert.Equal(WaterPhase.Float, dropped.WaterPhase);
        Assert.InRange(Feet(dropped), RestFeet - 0.05f, RestFeet + 0.05f);

        var sunk = BodyAt(4f);
        Run(sunk, pool, keys, 12f);
        Assert.Equal(WaterPhase.Float, sunk.WaterPhase);
        Assert.InRange(Feet(sunk), RestFeet - 0.05f, RestFeet + 0.05f);
    }

    [Fact]
    public void AStrokeUpHopsTheFloaterAndItSettlesBack()
    {
        var pool = MobTestWorld.Pool(12);
        var camera = BodyAt(RestFeet);
        var keys = new ScriptedKeyboard();
        Run(camera, pool, keys, 2f);

        keys.Press([Key.Space]);
        var highest = Feet(camera);
        Run(camera, pool, keys, 0.1f, c => highest = MathF.Max(highest, Feet(c)));
        keys.ReleaseAll();
        Run(camera, pool, keys, 6f, c => highest = MathF.Max(highest, Feet(c)));

        Assert.True(highest > RestFeet + 0.3f, "the stroke up did not lift the floater");
        Assert.Equal(WaterPhase.Float, camera.WaterPhase);
        Assert.InRange(Feet(camera), RestFeet - 0.05f, RestFeet + 0.05f);
    }

    [Fact]
    public void ADownStrokeDivesAndTheDiverKeepsSinking()
    {
        var pool = MobTestWorld.Pool(12);
        var camera = BodyAt(RestFeet);
        var keys = new ScriptedKeyboard();
        Run(camera, pool, keys, 2f);

        keys.Press([Key.ControlLeft]);
        Run(camera, pool, keys, 0.1f);
        keys.ReleaseAll();
        Run(camera, pool, keys, 1.5f);
        Assert.Equal(WaterPhase.Swim, camera.WaterPhase);
        var under = Feet(camera);

        // With no input the diver goes on sinking slowly, as swimming always did
        Run(camera, pool, keys, 1f);
        Assert.True(Feet(camera) < under - 0.5f);
        Assert.Equal(WaterPhase.Swim, camera.WaterPhase);
    }

    [Fact]
    public void AShallowPoolIsWadedOnFootSlowerThanDryGroundAndJumpedAsOnLand()
    {
        var keys = new ScriptedKeyboard();
        keys.Press([Key.W]);

        var dry = BodyAt(1f, x: 1.5f);
        var dryGround = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        Run(dry, dryGround, keys, 1.2f);
        var drySpeed = dry.HorizontalVelocity.Length();

        var wader = BodyAt(1f, x: 1.5f);
        var shallow = MobTestWorld.Pool(1);
        Run(wader, shallow, keys, 1.2f, c => Assert.Equal(WaterPhase.Wade, c.WaterPhase));
        var wadeSpeed = wader.HorizontalVelocity.Length();

        // Walked, not stroked: a steady speed below the dry walk but above the swim's slowdown, and
        // no stroke ever started
        Assert.Equal(0, wader.SwimStrokeCount);
        Assert.True(wadeSpeed < drySpeed - 0.1f);
        Assert.True(wadeSpeed > PlayerPhysics.SwimSpeedFactor * drySpeed);

        // The jump is the land jump: it clears the water and lands back on the floor
        keys.ReleaseAll();
        Run(wader, shallow, keys, 0.5f);
        keys.Press([Key.Space]);
        var peak = Feet(wader);
        Run(wader, shallow, keys, 0.05f, c => peak = MathF.Max(peak, Feet(c)));
        keys.ReleaseAll();
        Run(wader, shallow, keys, 1.5f, c => peak = MathF.Max(peak, Feet(c)));
        var landJump = PlayerPhysics.JumpSpeed * PlayerPhysics.JumpSpeed / (2f * PlayerPhysics.Gravity);
        Assert.True(peak - 1f > 0.9f * landJump, $"the wading jump rose {peak - 1f}, the land jump {landJump}");
        Assert.True(wader.IsGrounded);
    }
}
