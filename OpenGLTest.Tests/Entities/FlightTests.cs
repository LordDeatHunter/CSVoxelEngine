using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Models;
using VoxelEngine.Entities;
using Xunit;

namespace VoxelEngine.Tests.Entities;

/// <summary>
/// Flight in the shared mob layer (<see cref="RiggedMob.Fly"/>, <see cref="FlightSteering"/>), through a
/// test-only flyer: it hovers in its band over the floor with no gravity drift, seeks a target to hover
/// above it, slides along a wall and climbs over it without ever entering a solid cell, never takes fall
/// damage or ledge caution, still burns in lava, wanders in 3D through air, plays its fly clip by its 3D
/// speed, is knocked in 3D, banks, and keeps its height as a client mirror. A walker built the same way
/// keeps every ground rule.
/// </summary>
public sealed class FlightTests
{
    public FlightTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;
    private const float FloorTop = 1f;

    private static readonly FlightTuning Tuning = new(
        CruiseSpeed: 4f, VerticalSpeed: 3f, Acceleration: 4f,
        HoverMinBlocks: 3f, HoverMaxBlocks: 5f, MinClearanceBlocks: 1.5f,
        BobBlocks: 0.2f, BobHz: 0.5f);

    // Bone positions the three locomotion clips hold, so a pose tells which clip shows
    private const float IdleY = 0f;
    private const float WalkY = 1f;
    private const float FlyY = 2f;

    /// <summary>A rigged mob for the tests: a flyer with a <see cref="FlightTuning"/>, a walker without.</summary>
    private sealed class TestMob(EntityType type, Vector3 position, FlightTuning? flight)
        : RiggedMob(type, position, 3f, spawnIn: false, flight)
    {
        public override float BodyHeight => 1f;
        public override float BodyHalfWidth => 0.3f;

        /// <summary>One frame of the mob's own update toward <paramref name="goal"/>.</summary>
        public void Step(float deltaTime, ChunkManager chunks, Vector3? goal, bool chasing)
        {
            TickBody(deltaTime);
            Fly(goal, chasing, 1f, deltaTime, chunks);
        }

        public void Chase(float deltaTime, ChunkManager chunks, Vector3 targetEye) =>
            Step(deltaTime, chunks, FlightSteering.ChaseGoal(targetEye, HoverHeight), chasing: true);
    }

    private static AnimationClip HoldClip(string name, float y) =>
        new(name, 1f, AnimationClip.LoopMode.Loop, new Dictionary<string, AnimationClip.BoneTrack>
        {
            ["Body"] = new()
            {
                Position = [new AnimationClip.Keyframe(0f, new Vector3(0f, y, 0f), AnimationClip.Interpolation.Linear)],
            },
        }, Array.Empty<AnimationClip.Event>());

    private static IReadOnlyList<AnimationClip> LocomotionClips() =>
        [HoldClip(EntityAnimator.IdleClip, IdleY), HoldClip(EntityAnimator.WalkClip, WalkY), HoldClip(EntityAnimator.FlyClip, FlyY)];

    private static EntityType MobType()
    {
        var model = new BbmodelData();
        model.Animations.AddRange(LocomotionClips());
        return new EntityType { Name = "test_flyer", DisplayName = "Test flyer", MaxHealth = 20, ModelPath = "", Model = model };
    }

    private static TestMob Flyer(Vector3 position) => new(MobType(), position, Tuning);
    private static TestMob Walker(Vector3 position) => new(MobType(), position, null);

    private static ChunkManager Floor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    private static Vector3 Half(TestMob mob) => new(mob.BodyHalfWidth, mob.BodyHalfHeight, mob.BodyHalfWidth);

    private static float Clearance(TestMob mob) => mob.Position.Y - mob.BodyHalfHeight - FloorTop;

    [Fact]
    public void AnIdleFlyerSettlesIntoItsHoverBandAndStaysThereWithoutDrift()
    {
        var chunks = Floor();
        var low = Flyer(new Vector3(8.5f, FloorTop + 0.6f, 8.5f));
        var high = Flyer(new Vector3(8.5f, 14f, 8.5f));
        const float slack = 0.25f;

        for (var i = 0; i < (int)(30f / Dt); i++)
        {
            low.Step(Dt, chunks, null, chasing: false);
            high.Step(Dt, chunks, null, chasing: false);
            Assert.False(BoxSweep.IsColliding(low.Position, Half(low), chunks), "the low flyer entered the floor");
            if (i < (int)(10f / Dt)) continue;

            // Settled: no gravity pulls it down and nothing lifts it out of its band
            foreach (var mob in new[] { low, high })
                Assert.InRange(Clearance(mob), Tuning.HoverMinBlocks - slack, Tuning.HoverMaxBlocks + slack);
        }

        Assert.Equal(low.MaxHealth, low.Health);
        Assert.Equal(high.MaxHealth, high.Health);
    }

    [Fact]
    public void AChasingFlyerReachesItsTargetAndHoversAboveIt()
    {
        var chunks = Floor();
        var eye = new Vector3(12.5f, FloorTop + 1.6f, 12.5f);
        var flyer = Flyer(new Vector3(3.5f, 4f, 3.5f));
        var start = Vector2.Distance(new Vector2(flyer.Position.X, flyer.Position.Z), new Vector2(eye.X, eye.Z));

        for (var i = 0; i < (int)(10f / Dt); i++)
            flyer.Chase(Dt, chunks, eye);

        var reach = Vector2.Distance(new Vector2(flyer.Position.X, flyer.Position.Z), new Vector2(eye.X, eye.Z));
        Assert.True(reach < MobConfig.MobFlightArriveBlocks && reach < start, $"the flyer stayed {reach} blocks off");
        Assert.InRange(flyer.Position.Y - eye.Y,
            Tuning.HoverMinBlocks - Tuning.BobBlocks - 0.25f, Tuning.HoverMaxBlocks + Tuning.BobBlocks + 0.25f);
        // It turned to face where it flew
        var toward = Vector3.Normalize(new Vector3(eye.X - 3.5f, 0f, eye.Z - 3.5f));
        Assert.True(Vector3.Dot(new Vector3(-MathF.Sin(flyer.Facing), 0f, -MathF.Cos(flyer.Facing)), toward) > 0.9f);
    }

    // A floor with a wall across x = 10 up to the top of the chunk (y 1..15), the far side open
    private static ChunkManager Walled()
    {
        var chunks = Floor();
        var chunk = chunks.Chunks[Vector3.Zero];
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var y = 1; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(10, y, z, stone, 0);
        return chunks;
    }

    [Fact]
    public void AWallStopsAFlyerWhichSlidesAlongItAndClimbsOverWithoutEverEnteringIt()
    {
        var chunks = Walled();
        var flyer = Flyer(new Vector3(5.5f, 4f, 3.5f));
        var goal = new Vector3(13.5f, 4f, 12.5f);
        var slid = false;
        var wasNear = true;

        for (var i = 0; i < (int)(30f / Dt); i++)
        {
            flyer.Step(Dt, chunks, goal, chasing: true);
            Assert.False(BoxSweep.IsColliding(flyer.Position, Half(flyer), chunks), $"the flyer entered a block at {flyer.Position}");

            // It only ever leaves the near side over the wall's top, never through it
            var near = flyer.Position.X + flyer.BodyHalfWidth <= 10f + 1e-3f;
            if (wasNear && !near)
                Assert.True(flyer.Position.Y - flyer.BodyHalfHeight >= Chunk.Size - 1e-3f, $"the flyer tunneled to {flyer.Position}");
            if (near) slid |= flyer.Position.Z > 11f;
            wasNear = near;
        }

        Assert.True(slid, "the flyer never slid along the wall toward its goal");
        Assert.True(flyer.Position.X > 11f, $"the flyer never got over the wall: {flyer.Position}");
    }

    [Fact]
    public void AFlyerNeverTakesFallDamageEvenKnockedIntoTheFloor()
    {
        var chunks = Floor();
        var flyer = Flyer(new Vector3(8.5f, 14f, 8.5f));

        for (var i = 0; i < (int)(60f / Dt); i++)
        {
            // A hard downward knock every few seconds slams it toward the floor
            if (i % (int)(5f / Dt) == 0) flyer.Shove(-Vector3.UnitY, 30f);
            flyer.Step(Dt, chunks, null, chasing: false);
        }

        Assert.Equal(flyer.MaxHealth, flyer.Health);
    }

    // Two plateaus (stone up to y = 8) either side of a trench x 7..10 eight blocks deep
    private static ChunkManager Trench()
    {
        var chunks = Floor();
        var chunk = chunks.Chunks[Vector3.Zero];
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        {
            if (x is >= 7 and <= 10) continue;
            for (var y = 1; y <= 8; y++)
            for (var z = 0; z < Chunk.Size; z++)
                chunk.SetBlockRaw(x, y, z, stone, 0);
        }
        return chunks;
    }

    [Fact]
    public void LedgeCautionHoldsAWalkerButNeverAFlyer()
    {
        var chunks = Trench();
        var at = new Vector3(6.5f, 9.5f, 8.5f);
        var walker = Walker(at);
        var flyer = Flyer(at);

        Assert.True(walker.IsLedgeAhead(Vector3.UnitX, 1f, chunks, null));
        Assert.False(flyer.IsLedgeAhead(Vector3.UnitX, 1f, chunks, null));
    }

    [Fact]
    public void AFlyerTouchingLavaBurns()
    {
        var chunks = Floor();
        var chunk = chunks.Chunks[Vector3.Zero];
        var lava = ContentRegistry.RequireBlock("lava").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 1, z, lava, 0);
        var flyer = Flyer(new Vector3(8.5f, 1.4f + 0.5f, 8.5f));

        for (var t = 0f; t < MobConfig.MobLavaTickSeconds * 2f; t += Dt)
            flyer.UpdateHazards(Dt, chunks);

        Assert.True(flyer.Health < flyer.MaxHealth);
    }

    [Fact]
    public void AFlyingWanderPointStaysInItsRingAndHeightBand()
    {
        var origin = new Vector3(3f, 40f, -7f);
        for (var a = 0f; a < 1f; a += 0.125f)
        for (var r = 0f; r <= 1f; r += 0.25f)
        for (var h = 0f; h <= 1f; h += 0.25f)
        {
            var point = MobInterest.FlyingWanderPoint(origin, a, r, h);
            var flat = Vector2.Distance(new Vector2(point.X, point.Z), new Vector2(origin.X, origin.Z));
            Assert.InRange(flat, MobConfig.MobWanderMinRadiusBlocks - 1e-3f, MobConfig.MobWanderMaxRadiusBlocks + 1e-3f);
            Assert.InRange(point.Y - origin.Y, -MobConfig.MobFlightWanderRiseBlocks - 1e-3f, MobConfig.MobFlightWanderRiseBlocks + 1e-3f);
        }

        // The rise covers both directions, unlike a walker's level wander point
        Assert.True(MobInterest.FlyingWanderPoint(origin, 0f, 0f, 0f).Y < origin.Y);
        Assert.True(MobInterest.FlyingWanderPoint(origin, 0f, 0f, 0.99f).Y > origin.Y);
    }

    [Fact]
    public void AFlyersWanderGoalIsAlwaysAnAirCellAndAWalkersStaysLevel()
    {
        // Solid stone up to y = 6 under a flyer at y = 8, so many raw points land in rock
        var chunks = Floor();
        var chunk = chunks.Chunks[Vector3.Zero];
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var y = 1; y <= 6; y++)
            chunk.SetBlockRaw(x, y, z, stone, 0);
        var flyer = Flyer(new Vector3(8.5f, 8.5f, 8.5f));
        var walker = Walker(new Vector3(8.5f, 8.5f, 8.5f));

        var rose = false;
        for (var i = 0; i < 200; i++)
        {
            // A whole repick interval per call, so every call picks afresh
            var goal = flyer.WanderGoal(1, MobConfig.MobWanderRepickSeconds * 2f, chunks)!.Value;
            var cell = goal.BlockCell();
            Assert.Equal(0, chunks.GetBlockGlobal(cell.X, cell.Y, cell.Z));
            rose |= goal.Y > flyer.Position.Y;

            Assert.Equal(walker.Position.Y, walker.WanderGoal(1, MobConfig.MobWanderRepickSeconds * 2f, chunks)!.Value.Y);
        }
        Assert.True(rose, "a flyer's wander goals never left its own height");
    }

    [Fact]
    public void AFlyerPlaysItsFlyClipByItsThreeDimensionalSpeedAndAWalkerItsWalkByItsHorizontalOne()
    {
        var flyer = Flyer(new Vector3(8.5f, 8f, 8.5f));
        var walker = Walker(new Vector3(8.5f, 8f, 8.5f));
        var climbing = new Vector3(0f, 2f, 0f);
        flyer.NetworkSetState(flyer.Position, 0f, flyer.MaxHealth, climbing);
        walker.NetworkSetState(walker.Position, 0f, walker.MaxHealth, climbing);
        for (var i = 0; i < (int)(2f / Dt); i++)
        {
            flyer.AnimateMirror(Dt);
            walker.AnimateMirror(Dt);
        }

        // Straight up: a flyer is moving, a walker (horizontal speed 0) is standing
        Assert.True(flyer.Animation.LocalTransform("Body").Translation.Y > (WalkY + FlyY) * 0.5f);
        Assert.True(walker.Animation.LocalTransform("Body").Translation.Y < (IdleY + WalkY) * 0.5f);

        walker.NetworkSetState(walker.Position, 0f, walker.MaxHealth, new Vector3(2f, 0f, 0f));
        for (var i = 0; i < (int)(2f / Dt); i++)
            walker.AnimateMirror(Dt);
        Assert.InRange(walker.Animation.LocalTransform("Body").Translation.Y, (IdleY + WalkY) * 0.5f, (WalkY + FlyY) * 0.5f);
    }

    [Fact]
    public void TheMovingClipIsTheOneTheAnimatorWasBuiltFor()
    {
        var flying = new EntityAnimator(LocomotionClips(), 3f, EntityAnimator.FlyClip);
        var walking = new EntityAnimator(LocomotionClips(), 3f);
        for (var i = 0; i < (int)(2f / Dt); i++)
        {
            flying.Update(Dt, 3f);
            walking.Update(Dt, 3f);
        }

        Assert.True(MathF.Abs(flying.LocalTransform("Body").Translation.Y - FlyY) < 0.05f);
        Assert.True(MathF.Abs(walking.LocalTransform("Body").Translation.Y - WalkY) < 0.05f);
    }

    [Fact]
    public void AKnockSendsAFlyerAlongItsWholeDirectionButAWalkerOnlySideways()
    {
        var direction = new Vector3(1f, 1f, 0f);
        var flyer = Flyer(new Vector3(8.5f, 8f, 8.5f));
        var walker = Walker(new Vector3(8.5f, 8f, 8.5f));

        flyer.Shove(direction, 5f);
        walker.Shove(direction, 5f);

        Assert.True(flyer.Velocity.Y > 0f);
        Assert.Equal(5f, flyer.Velocity.Length(), 3);
        Assert.Equal(0f, walker.Velocity.Y);
        Assert.Equal(5f, walker.Velocity.Length(), 3);
    }

    [Fact]
    public void AFlyerBanksWhileItsVelocityRunsAcrossItsFacingAndAWalkerNeverRolls()
    {
        var flyer = Flyer(new Vector3(8.5f, 8f, 8.5f));
        var walker = Walker(new Vector3(8.5f, 8f, 8.5f));
        // Facing -Z while moving +X: what a turn looks like, the velocity lagging the facing
        flyer.NetworkSetState(flyer.Position, 0f, flyer.MaxHealth, new Vector3(4f, 0f, 0f));
        walker.NetworkSetState(walker.Position, 0f, walker.MaxHealth, new Vector3(4f, 0f, 0f));
        for (var i = 0; i < (int)(1f / Dt); i++)
        {
            flyer.AnimateMirror(Dt);
            walker.AnimateMirror(Dt);
        }

        Assert.True(MathF.Abs(Vector3.TransformNormal(Vector3.UnitY, flyer.BodyEffect(flyer.Feet)).X) > 0.05f);
        Assert.True(MathF.Abs(Vector3.TransformNormal(Vector3.UnitY, walker.BodyEffect(walker.Feet)).X) < 1e-4f);
    }

    [Fact]
    public void AClientMirrorOfAFlyerKeepsItsHeight()
    {
        var flyer = Flyer(new Vector3(0f, 0f, 0f));
        var aloft = new Vector3(4.5f, 37.25f, -2.5f);
        flyer.NetworkSetState(aloft, 1f, flyer.MaxHealth, new Vector3(0f, -1f, 2f));

        for (var i = 0; i < (int)(3f / Dt); i++)
            flyer.AnimateMirror(Dt);

        Assert.Equal(aloft, flyer.Position);
        Assert.Equal(-1f, flyer.Velocity.Y);
    }

    [Fact]
    public void AMisorderedFlightIsRefused()
    {
        Assert.Throws<ArgumentException>(() => (Tuning with { HoverMaxBlocks = Tuning.HoverMinBlocks - 1f }).Validate());
        Assert.Throws<ArgumentException>(() => (Tuning with { MinClearanceBlocks = Tuning.HoverMinBlocks + 1f }).Validate());
        Assert.Throws<ArgumentException>(() => (Tuning with { CruiseSpeed = 0f }).Validate());
        Tuning.Validate();
    }
}
