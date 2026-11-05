using System.Numerics;
using VoxelEngine.UI.Hud;
using Xunit;

namespace VoxelEngine.Tests.UI.Hud;

/// <summary>A flying pickup icon starts at the drop, lands exactly on its slot, then goes away.</summary>
public class PickupFlyIconsTests
{
    [Fact]
    public void TheArcStartsAtTheDropAndEndsOnTheSlot()
    {
        var start = new Vector2(100f, 200f);
        var end = new Vector2(600f, 700f);
        Assert.Equal(start, PickupFlyIcons.Arc(start, end, 0f));
        Assert.Equal(end, PickupFlyIcons.Arc(start, end, 1f));
    }

    [Fact]
    public void TheArcBowsUpwardInBetween()
    {
        var start = new Vector2(100f, 500f);
        var end = new Vector2(500f, 500f);
        Assert.True(PickupFlyIcons.Arc(start, end, 0.5f).Y < 500f);
    }

    [Fact]
    public void TheIconShrinksMonotonically()
    {
        var previous = PickupFlyIcons.ScaleAt(0f);
        for (var t = 0.1f; t <= 1f; t += 0.1f)
        {
            var scale = PickupFlyIcons.ScaleAt(t);
            Assert.True(scale <= previous);
            previous = scale;
        }
    }

    [Fact]
    public void AFinishedIconIsRemoved()
    {
        var icons = new PickupFlyIcons();
        icons.Spawn(1, Vector3.Zero, 2);
        icons.Advance(HudConfig.PickupFlySeconds * 0.5f);
        Assert.Equal(1, icons.Count);
        icons.Advance(HudConfig.PickupFlySeconds);
        Assert.Equal(0, icons.Count);
    }

    [Fact]
    public void AFullRingReplacesTheOldestIcon()
    {
        var icons = new PickupFlyIcons();
        icons.Spawn(1, Vector3.Zero, 0);
        icons.Advance(0.05f);
        for (var i = 1; i < PickupFlyIcons.Capacity; i++)
            icons.Spawn(2, Vector3.Zero, 0);
        icons.Spawn(3, Vector3.Zero, 0);
        Assert.Equal(PickupFlyIcons.Capacity, icons.Count);
        for (var i = 0; i < icons.Count; i++)
            Assert.NotEqual(1, icons.At(i).Id);
    }
}
