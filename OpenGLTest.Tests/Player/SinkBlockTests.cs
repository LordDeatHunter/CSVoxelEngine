using System;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Player;
using VoxelEngine.VisualTest;
using Xunit;

namespace VoxelEngine.Tests.Player;

/// <summary>
/// The sink and soft-landing block flags: a body caught by a sink block's soft top layer descends
/// through it no faster than <see cref="PlayerConfig.SinkBlockSpeed"/>, stands grounded meanwhile and
/// comes to rest sunk by the block's share, and a fall onto a soft-landing block bills no damage where
/// the same fall onto stone does. The flags are refused where the layer could not be cut.
/// </summary>
public sealed class SinkBlockTests
{
    public SinkBlockTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static ChunkManager Floor(byte topId) => MobTestWorld.Floor(topId);

    private static Camera DroppedFrom(float feet)
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(2.5f, feet + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        return camera;
    }

    private static float Feet(Camera camera) => camera.Position.Y - Camera.EyeHeight;

    [Fact]
    public void AFallIsCaughtAtTheLayerTopAndSinksSlowlyToRestAtTheSinkShare()
    {
        var sink = SinkTestBlock.Ensure();
        var manager = Floor(sink);
        var camera = DroppedFrom(6f);
        var keys = new ScriptedKeyboard();
        var top = 1f;
        var restFeet = SinkLayer.SolidHeight(BlockType.SinkOf(sink));

        var enteredAt = -1;
        var prevFeet = Feet(camera);
        for (var i = 0; i < 600; i++)
        {
            camera.Update(Dt, keys, manager);
            var feet = Feet(camera);
            if (enteredAt < 0 && feet <= top + SinkLayer.FeetTolerance)
            {
                enteredAt = i;
                // The fall is caught near the top, not where its speed would have carried it
                Assert.True(feet > top - PlayerConfig.SinkBlockSpeed * Dt * 2f, $"the fall plunged to {feet}");
            }
            if (enteredAt >= 0)
                Assert.True(camera.IsGrounded, $"not grounded in the layer at frame {i}");
            // Below the layer's top a body never descends faster than the sink speed
            if (prevFeet <= top)
                Assert.True(prevFeet - feet <= PlayerConfig.SinkBlockSpeed * Dt + 1e-4f,
                    $"sank {prevFeet - feet} in one frame from {prevFeet}");
            prevFeet = feet;
        }

        Assert.True(enteredAt >= 0, "never reached the block");
        Assert.InRange(Feet(camera), restFeet, restFeet + SinkLayer.FeetTolerance);
        Assert.True(camera.IsGrounded);
    }

    [Fact]
    public void ASunkBodyJumpsBackOut()
    {
        var manager = Floor(SinkTestBlock.Ensure());
        var camera = DroppedFrom(1f);
        var keys = new ScriptedKeyboard();
        for (var i = 0; i < 300; i++)
            camera.Update(Dt, keys, manager);

        keys.Press([Key.Space]);
        var peak = Feet(camera);
        for (var i = 0; i < 30; i++)
        {
            camera.Update(Dt, keys, manager);
            peak = MathF.Max(peak, Feet(camera));
        }
        Assert.True(peak > 1.5f, $"the jump out of the layer peaked at {peak}");
    }

    // Drops a body from 12 blocks with the fall tracker fed exactly as LocalPlayer feeds it
    private static int FallDamageOnto(byte topId)
    {
        var manager = Floor(topId);
        var camera = DroppedFrom(12f);
        var keys = new ScriptedKeyboard();
        var tracker = new FallTracker();
        var damage = 0;
        for (var i = 0; i < 300; i++)
        {
            camera.Update(Dt, keys, manager);
            damage += tracker.Observe(Feet(camera), camera.IsGrounded, flying: false, inWater: false, creative: false,
                FallTracker.DefaultMaxStepBlocks, cushioned: PlayerBodyProbe.LandingCushioned(camera.Position, manager));
        }
        return damage;
    }

    [Fact]
    public void ASoftLandingBillsNoFallDamageWhereStoneDoes()
    {
        Assert.Equal(0, FallDamageOnto(SinkTestBlock.Ensure()));
        Assert.True(FallDamageOnto(ContentRegistry.RequireBlock("stone").Id) > 0);
    }

    [Fact]
    public void TheLayerHoldsFeetToTheSinkSpeedAndLeavesRisesAlone()
    {
        const float speed = 0.6f;
        // A fall from far above is caught at the top
        Assert.Equal(1f - speed * Dt, SinkLayer.HoldFeet(5f, 0.6f, 1f, speed, Dt), 5);
        // Inside the layer a body descends at most the sink speed
        Assert.Equal(0.9f - speed * Dt, SinkLayer.HoldFeet(0.9f, 0.7f, 1f, speed, Dt), 5);
        // A slower descent and a rise are untouched
        Assert.Equal(0.899f, SinkLayer.HoldFeet(0.9f, 0.899f, 1f, speed, Dt), 5);
        Assert.Equal(0.95f, SinkLayer.HoldFeet(0.7f, 0.95f, 1f, speed, Dt), 5);
    }

    [Fact]
    public void OnlyASinkBlockHasALayerAndItReachesFromTheTopToItsSolidPart()
    {
        Assert.False(SinkLayer.FeetIn(1f, 0, 0f));
        Assert.Equal(1f, SinkLayer.SolidHeight(0f));

        var share = SinkLayer.MaxSink;
        Assert.True(SinkLayer.FeetIn(1f, 0, share));
        Assert.True(SinkLayer.FeetIn(SinkLayer.SolidHeight(share), 0, share));
        Assert.False(SinkLayer.FeetIn(SinkLayer.SolidHeight(share) - 0.2f, 0, share));
        Assert.False(SinkLayer.FeetIn(1.5f, 0, share));
        // A deeper share sinks the body further
        Assert.True(SinkLayer.SolidHeight(share) < SinkLayer.SolidHeight(share * 0.5f));
    }

    private static BodySurface Parse(BlockDefinition definition) =>
        BlockFieldRules.ParseBodySurface(definition, BlockRenderer.Model, definition.Fluid ?? false,
            !(definition.Fluid ?? false) && (definition.Solid ?? true), definition.MovementSpeed ?? 0f,
            definition.CollidesByHitbox ?? false, definition.StackMax ?? 0, "test.json");

    [Fact]
    public void TheFlagsAreRefusedWhereNoLayerCanBeCut()
    {
        Assert.Equal(SinkLayer.MaxSink, Parse(new BlockDefinition { Sink = SinkLayer.MaxSink, SoftLanding = true }).Sink);
        Assert.True(Parse(new BlockDefinition { SoftLanding = true }).SoftLanding);

        BlockDefinition[] refused =
        [
            new() { Sink = SinkLayer.MaxSink + 0.1f },
            new() { Sink = -0.1f },
            new() { Sink = 0.3f, Fluid = true },
            new() { Sink = 0.3f, Solid = false },
            new() { Sink = 0.3f, MovementSpeed = 0.5f },
            new() { Sink = 0.3f, CollidesByHitbox = true },
            new() { Sink = 0.3f, StackMax = 4 },
            new() { Sink = 0.3f, Bounce = true },
            new() { SoftLanding = true, Fluid = true },
        ];
        foreach (var definition in refused)
            Assert.Throws<InvalidOperationException>(() => Parse(definition));
    }
}
