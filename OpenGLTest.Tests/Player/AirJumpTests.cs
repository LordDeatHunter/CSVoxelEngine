using System;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.VisualTest;
using Xunit;

namespace VoxelEngine.Tests.Player;

/// <summary>
/// The cloud boots' air jump (<see cref="PlayerPhysics"/> with <see cref="Camera.GearAirJump"/>): one
/// extra jump per airtime on a fresh Space press, back on landing, as high as a ground jump, keeping the
/// horizontal speed, never in water or flight, and never counted as a bellows jump.
/// </summary>
public sealed class AirJumpTests
{
    public AirJumpTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;
    private static readonly Key[] Space = [Key.Space];
    private static readonly Key[] None = [];

    // Facing +X; a walk starts near the -X edge so it stays inside the one loaded chunk
    private static Camera BodyAt(float feetY, float x = 8.5f)
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(x, feetY + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        return camera;
    }

    private static float Feet(Camera camera) => camera.Position.Y - Camera.EyeHeight;

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    // One frame with exactly these keys held
    private static void Step(Camera camera, ChunkManager chunks, ScriptedKeyboard keys, Key[] held)
    {
        keys.Press(held);
        camera.Update(Dt, keys, chunks);
    }

    private static void Run(Camera camera, ChunkManager chunks, ScriptedKeyboard keys, Key[] held, float seconds)
    {
        for (var t = 0f; t < seconds; t += Dt)
            Step(camera, chunks, keys, held);
    }

    // A body standing settled on the stone floor
    private static Camera Settled(ChunkManager chunks, ScriptedKeyboard keys, bool airJump, float x = 8.5f)
    {
        var camera = BodyAt(1f, x);
        camera.GearAirJump = airJump;
        Run(camera, chunks, keys, None, 0.5f);
        Assert.True(camera.IsGrounded);
        return camera;
    }

    // Released frames until the body has passed its apex (falling, still in the air)
    private static void RunToApex(Camera camera, ChunkManager chunks, ScriptedKeyboard keys, Key[] held)
    {
        do Step(camera, chunks, keys, held);
        while (camera.VerticalVelocity > 0f);
        Assert.False(camera.IsGrounded);
    }

    [Fact]
    public void AnAirJumpGrantsExactlyOneExtraJumpAndRefillsOnLanding()
    {
        var chunks = StoneFloor();
        var keys = new ScriptedKeyboard();
        var camera = Settled(chunks, keys, airJump: true);

        Step(camera, chunks, keys, Space);
        Assert.False(camera.IsGrounded);
        Run(camera, chunks, keys, None, 0.1f);

        var airJumps = camera.AirJumpCount;
        var before = camera.VerticalVelocity;
        Step(camera, chunks, keys, Space);
        Assert.Equal(airJumps + 1, camera.AirJumpCount);
        Assert.True(camera.VerticalVelocity > before);
        Assert.True(camera.VerticalVelocity <= PlayerPhysics.JumpSpeed);

        // A second fresh press in the same airtime does nothing
        Run(camera, chunks, keys, None, 0.1f);
        before = camera.VerticalVelocity;
        Step(camera, chunks, keys, Space);
        Assert.Equal(airJumps + 1, camera.AirJumpCount);
        Assert.True(camera.VerticalVelocity < before);

        // Landing gives the charge back
        var landed = 0f;
        while (!camera.IsGrounded && landed < 5f)
        {
            Step(camera, chunks, keys, None);
            landed += Dt;
        }
        Assert.True(camera.IsGrounded);
        Step(camera, chunks, keys, Space);
        Run(camera, chunks, keys, None, 0.1f);
        Step(camera, chunks, keys, Space);
        Assert.Equal(airJumps + 2, camera.AirJumpCount);
    }

    [Fact]
    public void HoldingSpaceNeverFiresTheAirJump()
    {
        var chunks = StoneFloor();
        var keys = new ScriptedKeyboard();
        var camera = Settled(chunks, keys, airJump: true);
        var jumps = camera.JumpCount;

        Run(camera, chunks, keys, Space, 2f);

        Assert.True(camera.JumpCount > jumps + 1, "holding Space should keep ground jumping");
        Assert.Equal(0, camera.AirJumpCount);
    }

    [Fact]
    public void TheAirJumpNeverMovesJumpCount()
    {
        var chunks = StoneFloor();
        var keys = new ScriptedKeyboard();
        var camera = Settled(chunks, keys, airJump: true);
        var jumps = camera.JumpCount;
        var airJumps = camera.AirJumpCount;

        Step(camera, chunks, keys, Space);
        RunToApex(camera, chunks, keys, None);
        Step(camera, chunks, keys, Space);
        Run(camera, chunks, keys, None, 0.2f);

        Assert.Equal(jumps + 1, camera.JumpCount);
        Assert.Equal(airJumps + 1, camera.AirJumpCount);
    }

    [Fact]
    public void AnAirJumpRisesAsHighAsAGroundJump()
    {
        var chunks = StoneFloor();
        var keys = new ScriptedKeyboard();
        var camera = Settled(chunks, keys, airJump: true);

        var groundStart = Feet(camera);
        Step(camera, chunks, keys, Space);
        RunToApex(camera, chunks, keys, None);
        var groundGain = Feet(camera) - groundStart;
        // Land again before measuring the air jump
        while (!camera.IsGrounded)
            Step(camera, chunks, keys, None);

        Step(camera, chunks, keys, Space);
        RunToApex(camera, chunks, keys, None);
        var airStart = Feet(camera);
        Step(camera, chunks, keys, Space);
        Assert.Equal(1, camera.AirJumpCount);
        RunToApex(camera, chunks, keys, None);
        var airGain = Feet(camera) - airStart;

        Assert.True(groundGain > 0.5f);
        Assert.InRange(airGain, groundGain - 0.05f, groundGain + 0.05f);
    }

    [Fact]
    public void NoAirJumpWithoutTheAbilityInWaterOrWhileFlying()
    {
        var keys = new ScriptedKeyboard();

        // Without the ability
        var chunks = StoneFloor();
        var plain = Settled(chunks, keys, airJump: false);
        Step(plain, chunks, keys, Space);
        RunToApex(plain, chunks, keys, None);
        Step(plain, chunks, keys, Space);
        Run(plain, chunks, keys, None, 0.3f);
        Assert.Equal(0, plain.AirJumpCount);

        // Swimming in deep water, tapping Space over and over
        var pool = MobTestWorld.Pool(12);
        var swimmer = BodyAt(12f + FluidState.SourceHeight - Camera.EyeHeight);
        swimmer.GearAirJump = true;
        for (var i = 0; i < 120; i++)
            Step(swimmer, pool, keys, i % 2 == 0 ? Space : None);
        Assert.Equal(0, swimmer.AirJumpCount);

        // Flying (creative flight), tapping Space slower than a double tap, so flight stays on
        var flyer = BodyAt(8f);
        flyer.GearAirJump = true;
        flyer.TestSetFlight(true);
        for (var i = 0; i < 90; i++)
            Step(flyer, chunks, keys, i % 30 == 0 ? Space : None);
        Assert.True(flyer.IsFlying);
        Assert.Equal(0, flyer.AirJumpCount);
    }

    [Fact]
    public void TheAirJumpKeepsSprintMomentum()
    {
        var chunks = StoneFloor();
        var keys = new ScriptedKeyboard();
        Key[] sprint = [Key.W, Key.ShiftLeft];
        Key[] sprintJump = [Key.W, Key.ShiftLeft, Key.Space];

        // Two bodies sprinting in lockstep; only one of them air jumps
        var jumper = Settled(chunks, keys, airJump: true, x: 1.5f);
        var twin = Settled(chunks, keys, airJump: true, x: 1.5f);
        void Both(Key[] jumperKeys, Key[] twinKeys)
        {
            Step(jumper, chunks, keys, jumperKeys);
            Step(twin, chunks, keys, twinKeys);
        }

        for (var t = 0f; t < 0.3f; t += Dt) Both(sprint, sprint);
        Both(sprintJump, sprintJump);
        while (jumper.VerticalVelocity > 0f) Both(sprint, sprint);

        var before = jumper.HorizontalVelocity.Length();
        Both(sprintJump, sprint);

        Assert.Equal(1, jumper.AirJumpCount);
        Assert.Equal(0, twin.AirJumpCount);
        Assert.True(before > PlayerPhysics.BaseSpeed, "the body should be sprinting");
        Assert.Equal(twin.HorizontalVelocity.Length(), jumper.HorizontalVelocity.Length(), 3);
        Assert.InRange(jumper.HorizontalVelocity.Length(), before * 0.99f, before * 1.01f);
    }
}
