using System.Numerics;
using VoxelEngine.UI.Widgets;
using Xunit;

namespace VoxelEngine.Tests.UI.Widgets;

/// <summary>The dropdown's click rules: the box toggles the list, an option of the open list is chosen and closes it,
/// a click elsewhere dismisses an open list (spent) and is left alone while closed, and the rows sit flush against the
/// box on the side it opens to.</summary>
public sealed class DropdownStateTests
{
    private static DropdownState Make(bool up = false) => new(["A", "B", "C"])
    {
        Box = UiRect.FromTopLeft(new Vector2(100f, 300f), new Vector2(80f, 20f)),
        OpensUp = up,
    };

    private static readonly Vector2 Far = new(5f, 5f);

    [Fact]
    public void TheBoxTogglesTheListAndAnOptionIsOnlyPickableWhileOpen()
    {
        var dropdown = Make();
        var second = dropdown.OptionRect(1).Center;

        Assert.Equal(DropdownClick.Missed, dropdown.Click(second, out _));
        Assert.Equal(DropdownClick.Toggled, dropdown.Click(dropdown.Box.Center, out _));
        Assert.True(dropdown.IsOpen);

        Assert.Equal(DropdownClick.Chosen, dropdown.Click(second, out var chosen));
        Assert.Equal(1, chosen);
        Assert.False(dropdown.IsOpen);
    }

    [Fact]
    public void AClickOutsideDismissesAnOpenListButIsNotItsWhileClosed()
    {
        var dropdown = Make();
        Assert.Equal(DropdownClick.Missed, dropdown.Click(Far, out _));

        dropdown.Click(dropdown.Box.Center, out _);
        Assert.Equal(DropdownClick.Dismissed, dropdown.Click(Far, out var chosen));
        Assert.Equal(-1, chosen);
        Assert.False(dropdown.IsOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRowsStackFlushFromTheBoxAndTheListCoversExactlyThem(bool up)
    {
        var dropdown = Make(up);
        var list = dropdown.ListRect;
        for (var i = 0; i < dropdown.Options.Count; i++)
        {
            var row = dropdown.OptionRect(i);
            var previous = i == 0 ? dropdown.Box : dropdown.OptionRect(i - 1);
            Assert.Equal(up ? previous.Top : previous.Bottom, up ? row.Bottom : row.Top, 3);
            Assert.True(list.Contains(row.Center));
        }
        Assert.Equal(dropdown.Box.Size.Y * dropdown.Options.Count, list.Size.Y, 3);
        Assert.False(list.Contains(dropdown.Box.Center));
    }
}
