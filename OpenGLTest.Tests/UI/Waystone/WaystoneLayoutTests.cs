using System.Numerics;
using VoxelEngine.UI;
using VoxelEngine.UI.Waystone;
using Xunit;

namespace VoxelEngine.Tests.UI.Waystone;

/// <summary>
/// A click on the waystone screen lands on what was drawn there: each page's buttons and fields, RENAME only when it is
/// offered, list rows under their scroll and nothing past the last row or outside the box.
/// </summary>
public sealed class WaystoneLayoutTests
{
    private static readonly Vector2 Canvas = UiCanvas.ReferenceSize;

    [Fact]
    public void ThePopupsButtonsAreHitWhereTheyAreDrawnAndRenameOnlyWhenOffered()
    {
        Assert.Equal(WaystoneHitKind.Confirm,
            WaystoneLayout.HitAt(WaystonePage.Discovered, WaystoneLayout.ConfirmButton(Canvas, true).Center, Canvas, true, 0, 0f).Kind);
        Assert.Equal(WaystoneHitKind.Rename,
            WaystoneLayout.HitAt(WaystonePage.Discovered, WaystoneLayout.RenameButton(Canvas).Center, Canvas, true, 0, 0f).Kind);
        Assert.NotEqual(WaystoneHitKind.Rename,
            WaystoneLayout.HitAt(WaystonePage.Discovered, WaystoneLayout.RenameButton(Canvas).Center, Canvas, false, 0, 0f).Kind);
        Assert.Equal(WaystoneHitKind.Confirm,
            WaystoneLayout.HitAt(WaystonePage.Discovered, WaystoneLayout.ConfirmButton(Canvas, false).Center, Canvas, false, 0, 0f).Kind);
        Assert.Equal(WaystoneHitKind.None,
            WaystoneLayout.HitAt(WaystonePage.Discovered, Vector2.Zero, Canvas, true, 0, 0f).Kind);
    }

    [Fact]
    public void TheRenameDialogsFieldAndButtonsAreHitWhereTheyAreDrawn()
    {
        Assert.Equal(WaystoneHitKind.RenameField,
            WaystoneLayout.HitAt(WaystonePage.Rename, WaystoneLayout.RenameField(Canvas).Center, Canvas, true, 0, 0f).Kind);
        Assert.Equal(WaystoneHitKind.RenameOk,
            WaystoneLayout.HitAt(WaystonePage.Rename, WaystoneLayout.RenameOk(Canvas).Center, Canvas, true, 0, 0f).Kind);
        Assert.Equal(WaystoneHitKind.RenameCancel,
            WaystoneLayout.HitAt(WaystonePage.Rename, WaystoneLayout.RenameCancel(Canvas).Center, Canvas, true, 0, 0f).Kind);
        Assert.True(WaystoneLayout.RenameBox(Canvas).Contains(WaystoneLayout.RenameField(Canvas).Center));
    }

    [Fact]
    public void AListClickPicksTheRowUnderItsScrollAndNothingPastTheLastRow()
    {
        const int rows = 40;
        var offset = WaystoneLayout.RowHeight * 5f;

        var hit = WaystoneLayout.HitAt(WaystonePage.List, WaystoneLayout.RowRect(7, Canvas, offset).Center, Canvas, false, rows, offset);
        Assert.Equal(new WaystoneHit(WaystoneHitKind.Row, 7), hit);

        var lastRow = WaystoneLayout.RowRect(1, Canvas, 0f).Center + new Vector2(0f, WaystoneLayout.RowHeight);
        Assert.Equal(WaystoneHitKind.Panel, WaystoneLayout.HitAt(WaystonePage.List, lastRow, Canvas, false, 2, 0f).Kind);
        Assert.Equal(WaystoneHitKind.Search,
            WaystoneLayout.HitAt(WaystonePage.List, WaystoneLayout.SearchBox(Canvas).Center, Canvas, false, rows, 0f).Kind);
        Assert.Equal(WaystoneHitKind.None, WaystoneLayout.HitAt(WaystonePage.List, Vector2.Zero, Canvas, false, rows, 0f).Kind);
    }

    [Fact]
    public void TheListSitsBetweenTheSearchFieldAndTheFooterInsideThePanel()
    {
        var panel = WaystoneLayout.Panel(Canvas);
        var search = WaystoneLayout.SearchBox(Canvas);
        var list = WaystoneLayout.List(Canvas);
        var footer = WaystoneLayout.Footer(Canvas);

        Assert.True(search.Bottom <= list.Top && list.Bottom <= footer.Top);
        Assert.True(panel.Contains(new Vector2(footer.Center.X, footer.Bottom - 1f)));
        Assert.True(list.Size.Y > WaystoneLayout.RowHeight * 5f);
    }
}
