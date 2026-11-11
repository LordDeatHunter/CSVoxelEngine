using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Icebergs;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Icebergs;

/// <summary>
/// The iceberg simulation (<see cref="IcebergManager"/>): a berg drifts but stops at the pack ice
/// and at other bergs, freezes over unloaded chunks, is pushed by the bodies leaning on it (a big
/// berg less), carries its rider, pushes a body out of itself, mirrors only dead-reckon on a
/// client, and a berg despawns far from everyone and sinks at the end of its life once nobody is
/// near.
/// </summary>
public sealed class IcebergManagerTests
{
    public IcebergManagerTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 20f;
    private const float LongLife = 100000f;

    private static int Small => 0;
    private static int Large => IcebergTestWorld.Type.Variants.Count - 1;

    // A target hovering over the sea: keeps the bergs simulated without touching them
    private static StubTarget Watcher() => new(new Vector3(0f, 30f, 0f)) { IsFlying = true };

    // A lighting time at which seed's drift heads mostly along +X
    private static double EastwardTime(int seed)
    {
        for (var t = 0d; t < 100000d; t += 7d)
        {
            var drift = IcebergMotion.Drift(seed, t);
            if (drift.X > 0.9f * drift.Length())
                return t;
        }
        throw new InvalidOperationException("the wind never blew east");
    }

    private static bool OverlapsBlock(ChunkManager chunks, (Vector3 Min, Vector3 Max) box, ushort id)
    {
        VoxelCoords.CellRange(box.Min, box.Max - new Vector3(1e-4f), out var minX, out var minY, out var minZ,
            out var maxX, out var maxY, out var maxZ);
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
            if (chunks.GetBlockGlobal(x, y, z) == id)
                return true;
        return false;
    }

    [Fact]
    public void ABergDriftsButStopsAtThePackIce()
    {
        var world = IcebergTestWorld.Create(ice: true);
        var manager = IcebergTestWorld.Manager();
        const int seed = 7;
        var time = EastwardTime(seed);
        var berg = manager.Spawn(world.Chunks, Small, 1f, 0, seed, world.Center(14.5f, 0.5f), LongLife);
        Assert.NotNull(berg);
        var start = berg.Center;
        var ice = ContentRegistry.RequireBlock("ice").Id;
        var targets = new List<IMobTarget> { Watcher() };

        for (var t = 0f; t < 150f; t += Dt)
        {
            manager.Update(Dt, world.Chunks, targets, time);
            foreach (var box in berg.Boxes)
                Assert.False(OverlapsBlock(world.Chunks, box, ice), $"the berg reached into the ice at {berg.Center}");
        }

        Assert.True(berg.Center.X > start.X + 1f, "the berg never drifted toward the ice");
        Assert.True(berg.Bounds.Max.X <= IcebergTestWorld.IceMinX + 1e-3f);
    }

    [Fact]
    public void TwoBergsNeverOverlap()
    {
        var world = IcebergTestWorld.Create(ice: false);
        var manager = IcebergTestWorld.Manager();
        var west = manager.Spawn(world.Chunks, Small, 1f, 0, 1, world.Center(-6.5f, 0.5f), LongLife);
        var westBounds = west!.Bounds;
        var gap = 1f;
        var eastCenter = world.Center(-6.5f + (westBounds.Max.X - westBounds.Min.X) + gap, 0.5f);
        var east = manager.Spawn(world.Chunks, Small, 1f, 0, 2, eastCenter, LongLife);
        Assert.NotNull(east);
        var targets = new List<IMobTarget> { Watcher() };

        west.Velocity = new Vector3(IcebergConfig.MaxSpeed, 0f, 0f);
        east.Velocity = new Vector3(-IcebergConfig.MaxSpeed, 0f, 0f);
        for (var t = 0f; t < 10f; t += Dt)
        {
            manager.Update(Dt, world.Chunks, targets, 0d);
            Assert.False(IcebergContact.Overlaps(west.Bounds.Min, west.Bounds.Max, east.Bounds.Min, east.Bounds.Max));
        }
    }

    [Fact]
    public void ABergInAnUnloadedNeighborhoodDoesNotMove()
    {
        var world = IcebergTestWorld.Create(ice: false);
        var manager = IcebergTestWorld.Manager();
        var berg = manager.Spawn(world.Chunks, Small, 1f, 0, 3, world.Center(0.5f, 0.5f), LongLife)!;
        berg.Velocity = new Vector3(IcebergConfig.MaxSpeed, 0f, 0f);
        var start = berg.Center;
        Assert.True(world.Chunks.Chunks.TryRemove(VoxelCoords.ChunkPosOf(berg.Bounds.Max), out _));

        for (var t = 0f; t < 5f; t += Dt)
            manager.Update(Dt, world.Chunks, [Watcher()], 0d);

        Assert.Equal(start, berg.Center);
    }

    // The berg's net move along +X over seconds, with a body kept leaning on its -X shelf face
    // every frame when pushing (or a body that flies or is dead).
    private static float PushedDistance(int variant, float scale, StubTarget? body, float seconds = 10f)
    {
        var world = IcebergTestWorld.Create(ice: false);
        var manager = IcebergTestWorld.Manager();
        var berg = manager.Spawn(world.Chunks, variant, scale, 0, 5, world.Center(-8.5f, 0.5f), LongLife)!;
        var start = berg.Center;
        var targets = new List<IMobTarget> { Watcher() };
        if (body != null)
            targets.Add(body);

        for (var t = 0f; t < seconds; t += Dt)
        {
            if (body != null)
            {
                var shelf = berg.Boxes.First(b => b.Min.Y < berg.Center.Y && b.Max.Y > berg.Center.Y);
                var feet = new Vector3(shelf.Min.X - PlayerPhysics.PlayerRadius - BoxSweep.StepSize,
                    berg.Center.Y - 0.5f, berg.Center.Z);
                body.Eye = StubTarget.EyeAt(feet);
            }
            manager.Update(Dt, world.Chunks, targets, 0d);
        }

        return berg.Center.X - start.X;
    }

    [Fact]
    public void AWalkingBodyPushesASmallBergFartherThanALargeOne()
    {
        var small = PushedDistance(Small, IcebergConfig.ScaleMin, new StubTarget(Vector3.Zero))
                    - PushedDistance(Small, IcebergConfig.ScaleMin, null);
        var large = PushedDistance(Large, IcebergConfig.ScaleMax, new StubTarget(Vector3.Zero))
                    - PushedDistance(Large, IcebergConfig.ScaleMax, null);

        Assert.True(large > 0f, $"the large berg did not move under the push ({large})");
        Assert.True(small > large, $"the small berg moved {small}, the large one {large}");
    }

    [Fact]
    public void AFlyingOrDeadTargetDoesNotPush()
    {
        var free = PushedDistance(Small, 1f, null);
        Assert.Equal(free, PushedDistance(Small, 1f, new StubTarget(Vector3.Zero) { IsFlying = true }), 4);
        Assert.Equal(free, PushedDistance(Small, 1f, new StubTarget(Vector3.Zero) { IsDead = true }), 4);
        Assert.NotEqual(free, PushedDistance(Small, 1f, new StubTarget(Vector3.Zero)), 4);
    }

    [Fact]
    public void AForcedSpawnIsRefusedAtTheCapOrInsideIce()
    {
        var world = IcebergTestWorld.Create(ice: true);
        var manager = IcebergTestWorld.Manager();

        // A footprint over the ice strip is refused
        Assert.Null(manager.Spawn(world.Chunks, Small, 1f, 0, 1, world.Center(IcebergTestWorld.IceMinX + 0.5f, 0.5f), LongLife));

        // Spawns on a grid of free spots succeed until the hard cap, then a free spot is refused
        var spots = new List<Vector3>();
        for (var x = -28; x <= 14; x += 7)
        for (var z = -28; z <= 14; z += 7)
            spots.Add(world.Center(x + 0.5f, z + 0.5f));
        var used = 0;
        while (manager.Icebergs.Count < IcebergConfig.MaxAlive)
            Assert.NotNull(manager.Spawn(world.Chunks, Small, 1f, 0, used, spots[used++], LongLife));
        Assert.True(used < spots.Count, "the test grid ran out of free spots before the cap");
        Assert.Null(manager.Spawn(world.Chunks, Small, 1f, 0, used, spots[used], LongLife));

        // Inside another berg is refused too
        var other = IcebergTestWorld.Manager();
        Assert.NotNull(other.Spawn(world.Chunks, Small, 1f, 0, 1, spots[0], LongLife));
        Assert.Null(other.Spawn(world.Chunks, Small, 1f, 0, 2, spots[0] + new Vector3(0.5f, 0f, 0f), LongLife));
    }

    // --- Carry, push-out and mirrors ---

    private static Camera RiderOn(IcebergManager manager, (Vector3 Min, Vector3 Max) box, float x, float z)
    {
        var camera = new Camera(null, null) { BodySolids = manager };
        camera.TestTeleport(new Vector3(x, box.Max.Y + 0.01f + Camera.EyeHeight, z), 0f, 0f);
        return camera;
    }

    private static bool RidesAny(Camera camera, Iceberg berg) =>
        berg.Boxes.Any(b => IcebergContact.IsRiding(IcebergContact.BodyMin(camera.Position),
            IcebergContact.BodyMax(camera.Position), b.Min, b.Max));

    [Fact]
    public void ARiderIsCarriedByItsBerg()
    {
        var world = IcebergTestWorld.Create(ice: false);
        var manager = IcebergTestWorld.Manager();
        var berg = manager.Spawn(world.Chunks, Large, 1f, 0, 9, world.Center(0.5f, 0.5f), LongLife)!;
        var top = berg.Boxes.OrderByDescending(b => b.Max.Y).First();
        var camera = RiderOn(manager, top, (top.Min.X + top.Max.X) * 0.5f, (top.Min.Z + top.Max.Z) * 0.5f);
        var start = camera.Position;
        var bergStart = berg.Center;
        berg.Velocity = new Vector3(IcebergConfig.MaxSpeed, 0f, IcebergConfig.MaxSpeed * 0.5f);

        for (var t = 0f; t < 2f; t += Dt)
        {
            manager.Update(Dt, world.Chunks, [Watcher()], 0d);
            manager.CarryAndPushOut(camera, world.Chunks);
        }

        var moved = berg.Center - bergStart;
        Assert.True(moved.Length() > 0.1f);
        Assert.True(Vector3.Distance(start + moved, camera.Position) < 1e-3f,
            $"the berg moved {moved}, the rider {camera.Position - start}");
        Assert.True(RidesAny(camera, berg));
    }

    [Fact]
    public void ARiderLeaningOnAHigherStepIsCarriedNotLifted()
    {
        var world = IcebergTestWorld.Create(ice: false);
        var manager = IcebergTestWorld.Manager();
        var berg = manager.Spawn(world.Chunks, Large, 1f, 0, 9, world.Center(0.5f, 0.5f), LongLife)!;
        var boxes = berg.Boxes.Where(b => b.Max.Y > berg.Center.Y).OrderBy(b => b.Max.Y).ToList();
        var shelf = boxes[0];
        var step = boxes[1];
        // On the shelf, leaning on the step's -X face
        var camera = RiderOn(manager, shelf, step.Min.X - PlayerPhysics.PlayerRadius - 0.01f,
            (step.Min.Z + step.Max.Z) * 0.5f);
        var start = camera.Position;
        manager.CarryAndPushOut(camera, world.Chunks);

        // The berg moves toward the rider (-X), its step's face sweeping into the body
        var shift = new Vector3(-0.2f, 0f, 0f);
        berg.SetCenter(berg.Center + shift);
        manager.CarryAndPushOut(camera, world.Chunks);

        Assert.True(Vector3.Distance(start + shift, camera.Position) < 1e-3f,
            $"the rider moved {camera.Position - start}, the berg {shift}");
        Assert.True(RidesAny(camera, berg));
        Assert.False(manager.Overlaps(IcebergContact.BodyMin(camera.Position), IcebergContact.BodyMax(camera.Position)));
    }

    [Fact]
    public void ABodyLeftInsideABergIsPushedOut()
    {
        var world = IcebergTestWorld.Create(ice: false);
        var manager = IcebergTestWorld.Manager();
        var shape = manager.ShapeOf(Small);
        var center = world.Center(0.5f, 0.5f);
        var mirror = Iceberg.Mirror(shape, Small, 1f, 0, 1, center, Vector3.Zero, 0f);
        manager.ReplaceMirrors([mirror]);
        var shelf = mirror.Boxes.First(b => b.Min.Y < center.Y && b.Max.Y > center.Y);

        // Swimming just west of the shelf
        var camera = new Camera(null, null) { BodySolids = manager };
        camera.TestTeleport(StubTarget.EyeAt(new Vector3(shelf.Min.X - PlayerPhysics.PlayerRadius - 0.05f,
            center.Y - 0.5f, center.Z)), 0f, 0f);
        manager.CarryAndPushOut(camera, world.Chunks);

        // The next snapshot puts the berg into the body
        mirror.MirrorSetState(center - new Vector3(0.3f, 0f, 0f), Vector3.Zero, 0f);
        Assert.True(manager.Overlaps(IcebergContact.BodyMin(camera.Position), IcebergContact.BodyMax(camera.Position)));
        manager.CarryAndPushOut(camera, world.Chunks);

        Assert.False(manager.Overlaps(IcebergContact.BodyMin(camera.Position), IcebergContact.BodyMax(camera.Position)));
    }

    [Fact]
    public void MirrorsAreReplacedWholesaleAndOnlyDeadReckon()
    {
        var manager = IcebergTestWorld.Manager();
        var shape = manager.ShapeOf(Small);
        var velocity = new Vector3(1f, 0f, 0f);
        var a = Iceberg.Mirror(shape, Small, 1f, 0, 1, Vector3.Zero, Vector3.Zero, 0f);
        var b = Iceberg.Mirror(shape, Small, 1f, 0, 2, new Vector3(20f, 0f, 0f), velocity, 0f);
        manager.ReplaceMirrors([a, b]);
        Assert.Equal([a, b], manager.Icebergs);
        manager.ReplaceMirrors([b]);
        Assert.Equal([b], manager.Icebergs);

        // A short step moves it along its velocity; a long gap never carries it past the lead cap
        manager.AnimateMirrors(0.1f);
        Assert.Equal(20.1f, b.Center.X, 3);
        manager.AnimateMirrors(5f);
        Assert.True(b.Center.X <= 20f + velocity.X * IcebergConfig.MirrorMaxLeadSeconds + 1e-3f);

        // A mirror the server reports sunk away is dropped
        b.MirrorSetState(b.Center, Vector3.Zero, 1f);
        manager.AnimateMirrors(0.1f);
        Assert.Empty(manager.Icebergs);
    }

    // --- Life cycle ---

    [Fact]
    public void ABergPastEveryTargetsDespawnRadiusIsRemoved()
    {
        var world = IcebergTestWorld.Create(ice: false);
        var manager = IcebergTestWorld.Manager();
        manager.Climate = NoClimate;
        var berg = manager.Spawn(world.Chunks, Small, 1f, 0, 1, world.Center(0.5f, 0.5f), LongLife)!;
        var reach = berg.Bounds.Max.X + IcebergConfig.DespawnRadiusBlocks;

        manager.Update(Dt, world.Chunks, [new StubTarget(new Vector3(reach - 1f, 5f, 0.5f))], 0d);
        Assert.Contains(berg, manager.Icebergs);

        manager.Update(Dt, world.Chunks, [new StubTarget(new Vector3(reach + 1f, 5f, 0.5f))], 0d);
        Assert.DoesNotContain(berg, manager.Icebergs);
    }

    [Fact]
    public void AnExpiredBergSinksOnlyOnceNobodyIsOnOrNearIt()
    {
        var world = IcebergTestWorld.Create(ice: false);
        var manager = IcebergTestWorld.Manager();
        manager.Climate = NoClimate;
        var berg = manager.Spawn(world.Chunks, Small, 1f, 0, 1, world.Center(0.5f, 0.5f), 0.1f)!;
        var near = new StubTarget(new Vector3(berg.Bounds.Max.X + IcebergConfig.SpawnClearRadiusBlocks - 1f, 5f, 0.5f))
        {
            IsFlying = true,
        };

        for (var t = 0f; t < 1f; t += Dt)
            manager.Update(Dt, world.Chunks, [near], 0d);
        Assert.False(berg.IsSinking);

        near.Eye = new Vector3(berg.Bounds.Max.X + IcebergConfig.SpawnClearRadiusBlocks + 1f, 5f, 0.5f);
        manager.Update(Dt, world.Chunks, [near], 0d);
        Assert.True(berg.IsSinking);
        Assert.False(berg.CollidesWithBodies);

        for (var t = 0f; t < IcebergConfig.SinkSeconds + 1f; t += Dt)
            manager.Update(Dt, world.Chunks, [near], 0d);
        Assert.DoesNotContain(berg, manager.Icebergs);
    }

    private static bool NoClimate(ChunkManager chunks, int x, int z, int height, out float cold, out bool frozen)
    {
        cold = 0f;
        frozen = false;
        return false;
    }
}
