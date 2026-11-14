using VoxelEngine.UI.Hud;
using Xunit;

namespace VoxelEngine.Tests.UI.Hud;

/// <summary>The pickup feed: same-item pickups merge, other items stack, rows expire after their last pickup.</summary>
public sealed class PickupFeedTests
{
    [Fact]
    public void SameItemMergesAndKeepsTheLatestTotal()
    {
        var feed = new PickupFeed();
        feed.Record(5, 1, 10);
        feed.Record(5, 1, 11);
        Assert.Equal(1, feed.Count);
        Assert.Equal(2, feed.At(0).Gained);
        Assert.Equal(11, feed.At(0).Total);
    }

    [Fact]
    public void DifferentItemsStackAsSeparateRows()
    {
        var feed = new PickupFeed();
        feed.Record(5, 1, 1);
        feed.Record(6, 1, 1);
        Assert.Equal(2, feed.Count);
    }

    [Fact]
    public void RowExpiresAfterItsLastPickupNotItsFirst()
    {
        var feed = new PickupFeed();
        feed.Record(5, 1, 1);
        feed.Update(HudConfig.PickupFeedSeconds * 0.7f);
        feed.Record(5, 1, 2);
        feed.Update(HudConfig.PickupFeedSeconds * 0.7f);
        Assert.Equal(1, feed.Count);
        Assert.Equal(2, feed.At(0).Gained);
        feed.Update(HudConfig.PickupFeedSeconds * 0.4f);
        Assert.Equal(0, feed.Count);
    }

    [Fact]
    public void RowsNeverExceedTheCap()
    {
        var feed = new PickupFeed();
        for (ushort id = 1; id <= HudConfig.PickupFeedMaxRows + 3; id++) feed.Record(id, 1, 1);
        Assert.Equal(HudConfig.PickupFeedMaxRows, feed.Count);
    }
}
