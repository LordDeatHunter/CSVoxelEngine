using System.Numerics;
using VoxelEngine.UI;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.InventoryPanel;

public sealed class InventoryPanelLayoutTests
{
    private static readonly Vector2 Screen = new(1920f, 1080f);

    /// <summary>A row count whose content is several viewports tall, so the list overflows.</summary>
    private static int OverflowingRows() =>
        (int)(InventoryPanelLayout.ListHeight(Screen) / InventoryPanelLayout.RowHeight) * 3 + 5;

    /// <summary>A row count that fits inside the viewport.</summary>
    private static int FittingRows() =>
        (int)(InventoryPanelLayout.ListHeight(Screen) / InventoryPanelLayout.RowHeight) - 1;

    [Fact]
    public void TabCentresHitTheirTabs()
    {
        for (var t = 0; t < InventoryPanelLayout.Tabs.Length; t++)
            Assert.Equal(InventoryPanelLayout.Tabs[t],
                InventoryPanelLayout.TabAt(InventoryPanelLayout.TabCentre(t, Screen), Screen));

        var above = new Vector2(InventoryPanelLayout.TabCentre(0, Screen).X, InventoryPanelLayout.PanelTop - 1f);
        Assert.Null(InventoryPanelLayout.TabAt(above, Screen));
    }

    [Fact]
    public void RowCentreHitsItsRowAfterScrolling()
    {
        var rowCount = OverflowingRows();
        var offset = InventoryPanelLayout.RowHeight * 2.5f;
        var (first, end) = ScrollList.VisibleRange(offset, InventoryPanelLayout.RowHeight, rowCount,
            InventoryPanelLayout.ListHeight(Screen));

        // Rows whose centre lies inside the viewport are hit at that centre.
        var x = InventoryPanelLayout.RowLeft + 1f;
        var listBottom = InventoryPanelLayout.ListTop + InventoryPanelLayout.ListHeight(Screen);
        for (var r = first; r < end; r++)
        {
            var y = InventoryPanelLayout.RowCentreY(r, offset, Screen);
            if (y < InventoryPanelLayout.ListTop || y >= listBottom) continue;
            Assert.Equal(r, InventoryPanelLayout.RowAt(new Vector2(x, y), Screen, offset, rowCount));
        }

        // Row 0 is scrolled out above the viewport: no y inside the list selects it.
        for (var y = InventoryPanelLayout.ListTop; y < listBottom; y += 1f)
            Assert.NotEqual(0, InventoryPanelLayout.RowAt(new Vector2(x, y), Screen, offset, rowCount));

        // The scrollbar band's left edge belongs to the scrollbar, never to a row.
        var bandY = InventoryPanelLayout.RowCentreY(first + 1, offset, Screen);
        var band = new Vector2(InventoryPanelLayout.ScrollbarHitLeft(Screen), bandY);
        Assert.Null(InventoryPanelLayout.RowAt(band, Screen, offset, rowCount));
        Assert.NotEqual(ScrollbarHit.None, InventoryPanelLayout.ScrollbarHitAt(band, Screen, offset, rowCount));
    }

    [Fact]
    public void ScrollbarHitsResolveByThumbPosition()
    {
        var rowCount = OverflowingRows();
        var maxOffset = ScrollList.MaxOffset(InventoryPanelLayout.ContentHeight(rowCount),
            InventoryPanelLayout.ListHeight(Screen));
        var offset = maxOffset * 0.5f;
        var (centre, size) = InventoryPanelLayout.ThumbRect(Screen, offset, rowCount);
        var (trackCentre, trackSize) = InventoryPanelLayout.TrackRect(Screen);
        var thumbTop = centre.Y - size.Y * 0.5f;
        var thumbBottom = centre.Y + size.Y * 0.5f;
        var trackTop = trackCentre.Y - trackSize.Y * 0.5f;
        var trackBottom = trackCentre.Y + trackSize.Y * 0.5f;

        Assert.Equal(ScrollbarHit.Thumb, InventoryPanelLayout.ScrollbarHitAt(centre, Screen, offset, rowCount));
        Assert.Equal(ScrollbarHit.TrackAbove, InventoryPanelLayout.ScrollbarHitAt(
            new Vector2(centre.X, (trackTop + thumbTop) * 0.5f), Screen, offset, rowCount));
        Assert.Equal(ScrollbarHit.TrackBelow, InventoryPanelLayout.ScrollbarHitAt(
            new Vector2(centre.X, (thumbBottom + trackBottom) * 0.5f), Screen, offset, rowCount));
        Assert.Equal(ScrollbarHit.None, InventoryPanelLayout.ScrollbarHitAt(
            new Vector2(centre.X, trackTop - 5f), Screen, offset, rowCount));
        Assert.Equal(ScrollbarHit.None, InventoryPanelLayout.ScrollbarHitAt(
            new Vector2(centre.X, trackBottom + 5f), Screen, offset, rowCount));

        // A list that fits has no scrollbar anywhere on the track.
        var fits = FittingRows();
        foreach (var y in new[] { trackTop + 1f, trackCentre.Y, trackBottom - 1f })
            Assert.Equal(ScrollbarHit.None,
                InventoryPanelLayout.ScrollbarHitAt(new Vector2(centre.X, y), Screen, 0f, fits));
    }

    [Fact]
    public void ThumbTopInvertsToOffset()
    {
        var rowCount = OverflowingRows();
        var maxOffset = ScrollList.MaxOffset(InventoryPanelLayout.ContentHeight(rowCount),
            InventoryPanelLayout.ListHeight(Screen));
        foreach (var fraction in new[] { 0f, 0.2f, 0.5f, 0.8f, 1f })
        {
            var offset = maxOffset * fraction;
            var (centre, size) = InventoryPanelLayout.ThumbRect(Screen, offset, rowCount);
            Assert.Equal(offset,
                InventoryPanelLayout.OffsetForThumbTop(centre.Y - size.Y * 0.5f, rowCount, Screen), 2);
        }
    }

    [Fact]
    public void ListSitsAboveTheHotbarOnTheReferenceCanvas()
    {
        var listBottom = InventoryPanelLayout.ListTop + InventoryPanelLayout.ListHeight(Screen);
        var panelBottom = InventoryPanelLayout.PanelTop + InventoryPanelLayout.PanelHeight(Screen);
        Assert.True(InventoryPanelLayout.ListHeight(Screen) > 0f);
        Assert.True(listBottom < panelBottom);
        Assert.True(panelBottom <= HotbarLayout.Top(Screen));
    }

    [Fact]
    public void HotbarSlotCentresHitTheirSlots()
    {
        for (var i = 0; i < Inventory.SlotCount; i++)
            Assert.Equal(i, HotbarLayout.SlotAt(HotbarLayout.SlotCentre(i, Screen), Screen));

        var a = HotbarLayout.SlotCentre(0, Screen);
        var b = HotbarLayout.SlotCentre(1, Screen);
        Assert.Null(HotbarLayout.SlotAt((a + b) * 0.5f, Screen));
    }
}
