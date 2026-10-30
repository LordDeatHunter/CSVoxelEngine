using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Icebergs;
using Xunit;

namespace VoxelEngine.Tests.Entities.Icebergs;

public sealed class IcebergTests
{
    public IcebergTests() => TestContent.EnsureLoaded();

    private static IcebergShape Shape(int variant = 0) =>
        IcebergShape.ForType(EntityRegistry.Require("iceberg"))[variant];

    private static Iceberg Spawn(Vector3 center, int turns = 1, float scale = 1.1f) =>
        new(Shape(), 0, scale, turns, 42, center, 100f);

    [Fact]
    public void TheBoxesFollowTheCenter()
    {
        var berg = Spawn(new Vector3(10.5f, 62f, -4.5f));
        var before = new List<(Vector3 Min, Vector3 Max)>(berg.Boxes);
        var boundsBefore = berg.Bounds;

        var offset = new Vector3(3.25f, 0f, -1.75f);
        berg.SetCenter(berg.Center + offset);

        Assert.Equal(before.Count, berg.Boxes.Count);
        for (var i = 0; i < before.Count; i++)
        {
            AssertNear(before[i].Min + offset, berg.Boxes[i].Min);
            AssertNear(before[i].Max + offset, berg.Boxes[i].Max);
        }

        AssertNear(boundsBefore.Min + offset, berg.Bounds.Min);
        AssertNear(boundsBefore.Max + offset, berg.Bounds.Max);

        var expected = new List<(Vector3 Min, Vector3 Max)>();
        berg.Shape.PlaceAll(berg.Turns, berg.Scale, berg.Center, expected);
        Assert.Equal(expected, berg.Boxes);
    }

    [Fact]
    public void AMirrorOnlyDeadReckonsUpToTheLeadCap()
    {
        var start = new Vector3(0.5f, 62f, 0.5f);
        var velocity = new Vector3(1.2f, 0f, -0.4f);
        var berg = Iceberg.Mirror(Shape(), 0, 1f, 0, 7, start, velocity, 0f);

        const float dt = 1f / 60f;
        for (var t = 0f; t < IcebergConfig.MirrorMaxLeadSeconds * 4f; t += dt)
        {
            berg.AdvanceMirror(dt);
            var travelled = Vector3.Distance(start, berg.Center);
            Assert.True(travelled <= velocity.Length() * IcebergConfig.MirrorMaxLeadSeconds + 1e-4f);
        }

        Assert.True(Vector3.Distance(start, berg.Center) > 0f);
        Assert.Equal(start.Y, berg.Center.Y);

        var snapped = new Vector3(4f, 62f, 2f);
        berg.MirrorSetState(snapped, velocity, 0f);
        Assert.Equal(snapped, berg.Center);

        // A new snapshot restarts the lead, so the mirror moves again.
        berg.AdvanceMirror(dt);
        Assert.True(Vector3.Distance(snapped, berg.Center) > 0f);
    }

    [Fact]
    public void AMirrorStartsFullyEmerged()
    {
        var center = new Vector3(0.5f, 62f, 0.5f);
        var mirror = Iceberg.Mirror(Shape(), 0, 1f, 0, 7, center, Vector3.Zero, 0f);
        var fresh = Spawn(center);

        Assert.True(mirror.VisualSinkDepth < fresh.VisualSinkDepth);
    }

    [Fact]
    public void ASinkingBergNoLongerCollides()
    {
        var berg = Spawn(new Vector3(0.5f, 62f, 0.5f));
        Assert.True(berg.CollidesWithBodies);
        Assert.False(berg.IsRemovable);

        berg.BeginSinking();
        Assert.False(berg.CollidesWithBodies);

        for (var t = 0f; t < IcebergConfig.SinkSeconds * 2f; t += 0.1f)
            berg.AdvanceSink(0.1f);
        Assert.True(berg.IsRemovable);

        var mirror = Iceberg.Mirror(Shape(), 0, 1f, 0, 7, berg.Center, Vector3.Zero, 0.5f);
        Assert.False(mirror.CollidesWithBodies);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");
}
