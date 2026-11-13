using System.Numerics;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.InventoryPanel;

public sealed class DragTrailTests
{
    [Fact]
    public void A_still_pointer_leaves_no_sparkles()
    {
        var trail = new DragTrail();
        for (var i = 0; i < 20; i++)
            trail.Advance(i * 0.016f, new Vector2(300f, 200f));
        Assert.Equal(0, trail.Count);
    }

    [Fact]
    public void A_moving_pointer_leaves_sparkles_that_fade_after_the_drop()
    {
        var trail = new DragTrail();
        for (var i = 0; i < 20; i++)
            trail.Advance(i * 0.016f, new Vector2(300f + i * 20f, 200f));
        Assert.True(trail.Count > 0);

        // The drop: no pointer. Every sparkle ages out on its own.
        var time = 20 * 0.016f;
        for (var i = 0; i < 100; i++)
            trail.Advance(time += 0.016f, null);
        Assert.Equal(0, trail.Count);
    }

    [Fact]
    public void A_faster_drag_emits_more_and_the_pool_never_overflows()
    {
        var slow = new DragTrail();
        var fast = new DragTrail();
        slow.Advance(0f, new Vector2(0f, 0f));
        fast.Advance(0f, new Vector2(0f, 0f));
        slow.Advance(0.016f, new Vector2(20f, 0f));
        fast.Advance(0.016f, new Vector2(80f, 0f));
        Assert.True(fast.Count > slow.Count);

        for (var i = 0; i < 50; i++)
            fast.Advance(0.016f * (i + 2), new Vector2(80f + i * 500f, 0f));
        Assert.True(fast.Count <= HudConfig.DragTrailMax);
    }
}
