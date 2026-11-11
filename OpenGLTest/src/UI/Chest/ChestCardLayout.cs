using System;
using System.Numerics;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Widgets;

namespace VoxelEngine.UI.Chest;

/// <summary>
/// GL-free geometry of the chest card beside the [E] panel: a title row, a search field and the scrolled list of
/// the chest's rows. The draw (<c>ChestCardRenderer</c>) and the click and wheel handling (<c>ChestInput</c>) read this
/// one definition, so a click lands on exactly the row that was drawn. Canvas units (see <see cref="UiCanvas"/>).
/// </summary>
public static class ChestCardLayout
{
    public const float Width = 420f;
    public const float TitleHeight = 30f;
    public const float SearchHeight = 36f;
    public const float Gap = 8f;
    public const float RowHeight = 42f;
    public const float ButtonHeight = 30f;

    /// <summary>The wheel scrolls this many rows a notch.</summary>
    public const int WheelStepRows = 3;

    /// <summary>The card: right of the list panel and as tall as it.</summary>
    public static UiRect Card(Vector2 screen)
    {
        var detail = InventoryPanelLayout.DetailCardRect(screen);
        var left = detail.Center.X - detail.Size.X * 0.5f;
        left = MathF.Min(left, screen.X - InventoryPanelLayout.PanelRightMargin - Width);
        return UiRect.FromTopLeft(new Vector2(left, InventoryPanelLayout.PanelTop),
            new Vector2(Width, InventoryPanelLayout.PanelHeight(screen)));
    }

    private static UiRect Content(Vector2 screen) => PanelFrameLayout.Standard(Card(screen)).Content;

    /// <summary>The title text's top-left area.</summary>
    public static UiRect Title(Vector2 screen)
    {
        var content = Content(screen);
        return UiRect.FromTopLeft(new Vector2(content.Left, content.Top), new Vector2(content.Size.X, TitleHeight));
    }

    /// <summary>The search field box.</summary>
    public static UiRect SearchBox(Vector2 screen)
    {
        var content = Content(screen);
        return UiRect.FromTopLeft(new Vector2(content.Left, content.Top + TitleHeight + Gap),
            new Vector2(content.Size.X, SearchHeight));
    }

    /// <summary>The LOOT ALL button: left half of the row under the search field.</summary>
    public static UiRect LootAllButton(Vector2 screen)
    {
        var search = SearchBox(screen);
        var width = (search.Size.X - Gap) * 0.5f;
        return UiRect.FromTopLeft(new Vector2(search.Left, search.Bottom + Gap), new Vector2(width, ButtonHeight));
    }

    /// <summary>The QUICK STACK button: right half of the row under the search field.</summary>
    public static UiRect QuickStackButton(Vector2 screen)
    {
        var loot = LootAllButton(screen);
        return UiRect.FromTopLeft(new Vector2(loot.Right + Gap, loot.Top), loot.Size);
    }

    /// <summary>The list viewport: what is left under the buttons.</summary>
    public static UiRect List(Vector2 screen)
    {
        var content = Content(screen);
        var top = content.Top + TitleHeight + Gap + SearchHeight + Gap + ButtonHeight + Gap;
        return UiRect.FromTopLeft(new Vector2(content.Left, top),
            new Vector2(content.Size.X, MathF.Max(0f, content.Bottom - top)));
    }

    /// <summary>Content height of a list of rows.</summary>
    public static float ContentHeight(int rowCount) => rowCount * RowHeight;

    /// <summary>The scroll offset clamped for this many rows.</summary>
    public static float ClampedOffset(float offset, int rowCount, Vector2 screen) =>
        ScrollList.Clamp(offset, ContentHeight(rowCount), List(screen).Size.Y);

    /// <summary>The list row under a canvas point for a list scrolled by <paramref name="offset"/>, or null.</summary>
    public static int? RowAt(Vector2 point, Vector2 screen, float offset, int rowCount)
    {
        var list = List(screen);
        if (point.X < list.Left || point.X >= list.Right) return null;
        return ScrollList.RowAt(point.Y - list.Top, offset, RowHeight, rowCount, list.Size.Y);
    }

    /// <summary>Whether a canvas point is over the whole card (the wheel scrolls the chest list there).</summary>
    public static bool Contains(Vector2 point, Vector2 screen) => Card(screen).Contains(point);

    /// <summary>The row rectangle of list row <paramref name="row"/> scrolled by <paramref name="offset"/>.</summary>
    public static UiRect RowRect(int row, Vector2 screen, float offset)
    {
        var list = List(screen);
        return UiRect.FromTopLeft(new Vector2(list.Left, list.Top + ScrollList.RowTop(row, offset, RowHeight)),
            new Vector2(list.Size.X, RowHeight - 4f));
    }
}
