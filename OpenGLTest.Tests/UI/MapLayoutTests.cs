using System;
using System.Numerics;
using VoxelEngine.UI.Map;
using Xunit;

namespace VoxelEngine.Tests.UI;

public sealed class MapLayoutTests
{
    private static readonly Vector2 Canvas = new(1920f, 1080f);

    [Fact]
    public void AWorldPointRoundTripsThroughTheMapScreen()
    {
        var center = new Vector2(-123.5f, 456.25f);
        foreach (var zoom in new[] { MapConfig.MinZoom, 1f, MapConfig.MaxZoom })
        foreach (var world in new[] { center, new Vector2(0f, 0f), new Vector2(-900f, 37f) })
        {
            var back = MapLayout.ToWorld(MapLayout.ToCanvas(world, center, zoom, Canvas * 0.5f), center, zoom, Canvas);
            Assert.True(Vector2.Distance(world, back) < 1e-2f, $"{world} came back as {back} at zoom {zoom}");
        }
    }

    [Fact]
    public void NorthIsUpAndEastIsRight()
    {
        var center = new Vector2(10f, 10f);
        var mid = Canvas * 0.5f;
        Assert.True(MapLayout.ToCanvas(center - Vector2.UnitY, center, 2f, mid).Y < mid.Y);
        Assert.True(MapLayout.ToCanvas(center + Vector2.UnitX, center, 2f, mid).X > mid.X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFarPinIsHeldInsideTheMinimapAndANearOneIsLeftAlone(bool round)
    {
        var box = MapLayout.Minimap(Canvas);
        const float inset = 8f;
        var half = box.Size.X * 0.5f - inset;
        for (var angle = 0f; angle < MathF.Tau; angle += 0.3f)
        {
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var far = box.Center + direction * 1000f;
            Assert.True(MapLayout.ClampToRim(ref far, box, round, inset));
            var offset = far - box.Center;
            if (round) Assert.True(offset.Length() <= half + 1e-3f);
            else Assert.True(MathF.Abs(offset.X) <= half + 1e-3f && MathF.Abs(offset.Y) <= half + 1e-3f);
            // Held on the rim along the way to the pin, so it still points at it
            Assert.True(Vector2.Dot(Vector2.Normalize(offset), direction) > 0.7f);

            var near = box.Center + direction * (half * 0.5f);
            var before = near;
            Assert.False(MapLayout.ClampToRim(ref near, box, round, inset));
            Assert.Equal(before, near);
        }
    }

    [Fact]
    public void TheMinimapSitsInsideTheCanvasTopRight()
    {
        var box = MapLayout.Minimap(Canvas);
        Assert.True(box.Left > Canvas.X * 0.5f && box.Right <= Canvas.X && box.Top >= 0f);
        Assert.True(box.Bottom < Canvas.Y * 0.5f);
    }

    [Fact]
    public void TheMapScreenButtonsAreHitAndApart()
    {
        var center = MapLayout.CenterButton(Canvas);
        var shape = MapLayout.ShapeButton(Canvas);
        Assert.True(MapLayout.OnButton(center.Center, Canvas));
        Assert.True(MapLayout.OnButton(shape.Center, Canvas));
        Assert.False(MapLayout.OnButton(Canvas * 0.5f, Canvas));
        Assert.True(shape.Right < center.Left || center.Right < shape.Left);
        Assert.True(shape.Left >= 0f && center.Right <= Canvas.X);
    }

    [Fact]
    public void ZoomingKeepsTheWorldPointUnderThePointerAndStaysInRange()
    {
        var view = new MapScreenView { Center = new Vector2(40f, -20f) };
        var pointer = new Vector2(1500f, 300f);
        var anchor = MapLayout.ToWorld(pointer, view.Center, view.Zoom, Canvas);

        view.ZoomAbout(pointer, Canvas, 1.7f);
        Assert.True(Vector2.Distance(anchor, MapLayout.ToWorld(pointer, view.Center, view.Zoom, Canvas)) < 1e-2f);

        for (var i = 0; i < 50; i++) view.ZoomAbout(pointer, Canvas, 2f);
        Assert.Equal(MapConfig.MaxZoom, view.Zoom);
        for (var i = 0; i < 50; i++) view.ZoomAbout(pointer, Canvas, 0.5f);
        Assert.Equal(MapConfig.MinZoom, view.Zoom);
    }

    [Fact]
    public void APanStopsFollowingThePlayerAndCenterResumesIt()
    {
        var view = new MapScreenView();
        view.Follow(new Vector2(5f, 5f));
        Assert.Equal(new Vector2(5f, 5f), view.Center);

        // A press that travels past the threshold pans the map with the pointer
        view.BeginPress(new Vector2(500f, 500f));
        view.TrackPress(new Vector2(500f + MapConfig.DragThreshold * 0.5f, 500f));
        Assert.False(view.Dragging);
        view.TrackPress(new Vector2(600f, 500f));
        Assert.True(view.Dragging);
        Assert.True(view.Center.X < 5f);
        Assert.False(view.Following);

        view.Follow(new Vector2(50f, 50f));
        Assert.NotEqual(new Vector2(50f, 50f), view.Center);

        view.Recenter();
        view.Follow(new Vector2(50f, 50f));
        Assert.Equal(new Vector2(50f, 50f), view.Center);
        Assert.Equal(MapConfig.DefaultZoom, view.Zoom);
    }
}
