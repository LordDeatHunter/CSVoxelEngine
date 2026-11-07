using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Content;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Data;
using VoxelEngine.Items;
using VoxelEngine.UI;
using VoxelEngine.UI.InventoryPanel;
using static VoxelEngine.Rendering.Overlays.UIRenderer;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The [E] inventory panel: its tabs, search field, list rows, creative catalog buttons and
/// preview card, and the hit-tests that read the same <see cref="InventoryPanelLayout"/>, so a
/// click lands on exactly what was drawn. The Craft tab's card is <see cref="CraftCardRenderer"/>.
/// Draws through the <see cref="UIRenderer"/> primitives. Render thread only.
/// </summary>
internal sealed class InventoryPanelRenderer
{
    // Catalog button labels are smaller than the count font, and shrink further to fit "+100".
    private const float CatalogButtonCapHeight = 12f;

    // [E] inventory panel, in pixels: a Skyrim-style list on the left, preview on the right
    // Aliases of InventoryPanelLayout, which owns the panel geometry; they serve the list, its
    // rows and the non-crafting preview only. The crafting card reads CraftCardLayout.
    private const float PanelLeft = InventoryPanelLayout.PanelLeft;
    private const float PanelTop = InventoryPanelLayout.PanelTop;
    private float PanelWidth => InventoryPanelLayout.PanelWidth(_ui.ScreenSize);
    private const float PanelPad = InventoryPanelLayout.PanelPad;
    private const float TabHeight = InventoryPanelLayout.TabHeight;
    private const float RowHeight = InventoryPanelLayout.RowHeight;
    private const float RowIcon = 30f;
    private const float PreviewSize = 190f;
    private const float PreviewRight = 300f;
    private static readonly Rgba RowSelected = Rgba.Gray(0.22f, 0.8f);

    private readonly GL _gl;
    private readonly UIRenderer _ui;

    internal InventoryPanelRenderer(GL gl, UIRenderer ui)
    {
        _gl = gl;
        _ui = ui;
    }

    /// <summary>
    /// The [E] panel: tabs and a name-sorted list on the left, a rotating preview with the
    /// picked name on the right. The hotbar stays visible underneath so picks can be aimed
    /// at slots. <paramref name="time"/> spins the preview.
    /// </summary>
    internal void DrawInventoryPanel(Inventory inventory, InventoryView view, float time,
        float headingDegrees, bool creative)
    {
        var top = PanelTop;
        var height = InventoryPanelLayout.PanelHeight(_ui.ScreenSize);
        var panel = new Vector2(PanelLeft + PanelWidth * 0.5f, top + height * 0.5f);
        _ui.DrawRect(panel, new Vector2(PanelWidth, height), PanelBackground);

        var tabs = InventoryPanelLayout.Tabs;
        var tabWidth = InventoryPanelLayout.TabWidth(_ui.ScreenSize);
        for (var t = 0; t < tabs.Length; t++)
        {
            var center = InventoryPanelLayout.TabCenter(t, _ui.ScreenSize);
            var active = view.Tab == tabs[t];
            _ui.DrawRect(center, new Vector2(tabWidth - 4f, TabHeight),
                active ? SelectedBackground : SlotBackground);
            if (active)
                _ui.DrawBorder(center, new Vector2(tabWidth - 4f, TabHeight), Border.Standard, SelectedBorderColor);
            _ui.DrawTextCenteredFitted(TabTitle(tabs[t]),
                center.X, top + PanelPad + 6f, _ui.CountScale, Rgba.White, tabWidth - 8f);
            _ui.DrawTextCenteredFitted(TabSubtitle(inventory, tabs[t]),
                center.X, top + PanelPad + 30f, _ui.CountScale, Rgba.White, tabWidth - 8f);
        }

        var (searchCenter, searchSize) = InventoryPanelLayout.SearchBoxRect(_ui.ScreenSize);
        _ui.DrawInputField(searchCenter, searchSize, view.Search.Text,
            view.Focus == InventoryFocus.Search, "Search items and recipes", time);

        // A query replaces the tab's list with search results; the list region below doubles as
        // the field's dropdown, and InventoryPanelInput.HandleInventoryClick reads the same InventorySearch
        // call, so the drawn rows and the click always agree.
        IReadOnlyList<SearchHit>? hits = view.Search.IsEmpty
            ? null
            : InventorySearch.Search(view.Search.Query, inventory, creative);

        // One list per frame for the active tab (never re-query StackEntries/ToolEntries for
        // the visible range: that would double the LINQ sort). In creative the Blocks/Tools/Misc
        // tabs list the whole catalog instead of the player's stock.
        IReadOnlyList<CreativeCatalog.CatalogEntry>? catalog = null;
        IReadOnlyList<Inventory.ToolRow>? tools = null;
        IReadOnlyList<CraftingRecipe>? recipes = null;
        IReadOnlyList<Inventory.StackRow>? rows = null;
        int rowCount;
        if (hits != null)
            rowCount = hits.Count;
        else if (creative && view.Tab != InventoryCategory.Crafting)
            rowCount = (catalog = CreativeCatalog.Entries(view.Tab, inventory)).Count;
        else if (view.Tab == InventoryCategory.Tools)
            rowCount = (tools = inventory.ToolEntries()).Count;
        else if (view.Tab == InventoryCategory.Crafting)
            rowCount = (recipes = ContentRegistry.CraftingRecipes).Count;
        else
            rowCount = (rows = inventory.StackEntries(view.Tab)).Count;

        // Clamp the stored offset with this frame's row count, exactly as
        // InventoryPanelInput.HandleInventoryClick does, so the drawn list and the click always agree.
        var offset = hits != null
            ? InventoryPanelLayout.ClampedOffset(view.Search.ScrollOffset, rowCount, _ui.ScreenSize)
            : InventoryPanelLayout.ClampedOffset(view.ScrollOffset, rowCount, _ui.ScreenSize);
        var listHeight = InventoryPanelLayout.ListHeight(_ui.ScreenSize);
        var (first, end) = ScrollList.VisibleRange(offset, RowHeight, rowCount, listHeight);

        // A glClear honors the scissor, so the pass's one full-screen depth clear must happen
        // before the scissor goes on; otherwise the latch would record a clear that covered only
        // the list, and the preview and crafting-card icons would z-fight the world's depth.
        _ui.EnsureDepthCleared();

        // glScissor takes framebuffer pixels with a bottom-left origin, hence the conversion
        // from the canvas rect. Nothing but the row loops may run while it is enabled (the
        // preview, crafting card, pause menu and later passes all assume it off, and Begin
        // disables it as a belt-and-braces), and nothing may return from inside this block.
        var (sx, sy, sw, sh) = UiCanvas.ScissorRect(
            new Vector2(InventoryPanelLayout.PanelLeft, InventoryPanelLayout.ListTop) + _ui.OverlayOffset,
            new Vector2(InventoryPanelLayout.PanelWidth(_ui.ScreenSize), listHeight),
            _ui.Scale, _ui.WindowSize, _ui.FramebufferSize);
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(sx, sy, (uint)sw, (uint)sh);
        try
        {
            if (hits != null)
            {
                for (var i = first; i < end; i++)
                    DrawSearchRow(i, offset, hits[i], i == view.Search.Highlight, headingDegrees, time);
            }
            else if (catalog != null)
            {
                for (var i = first; i < end; i++)
                    DrawCatalogRow(i, offset, rowCount, catalog[i], headingDegrees, time);
            }
            else if (tools != null)
            {
                for (var i = first; i < end; i++)
                {
                    var selected = view.SelectedToolInstance == tools[i].InstanceId;
                    DrawEntryRow(i, offset, tools[i].ItemId, tools[i].Name, "1", selected, false, headingDegrees,
                        time, null, inventory.ToolDurabilityFraction(tools[i].InstanceId));
                }
            }
            else if (recipes != null)
            {
                for (var i = first; i < end; i++)
                {
                    var ready = inventory.CanCraft(recipes[i]);
                    DrawEntryRow(i, offset, recipes[i].ResultId,
                        Inventory.DisplayName(recipes[i].ResultId),
                        $"x{recipes[i].ResultCount}",
                        view.Craft.Recipe != null && view.Craft.Recipe.Name == recipes[i].Name,
                        !ready, headingDegrees, time,
                        ready ? ReadyGreen : null);
                }
            }
            else if (rows != null)
            {
                for (var i = first; i < end; i++)
                    DrawEntryRow(i, offset, rows[i].Id, rows[i].Name, rows[i].Count.ToString(),
                        view.SelectedId == rows[i].Id, false, headingDegrees, time);
            }
        }
        finally
        {
            _gl.Disable(EnableCap.ScissorTest);
        }

        // The scrollbar sits in the panel's right padding and only exists while the list
        // overflows; the thumb is highlighted while it is being dragged.
        if (ScrollList.Overflows(InventoryPanelLayout.ContentHeight(rowCount), listHeight))
        {
            var track = InventoryPanelLayout.TrackRect(_ui.ScreenSize);
            _ui.DrawRect(track.Center, track.Size, SlotBackground);
            var thumb = InventoryPanelLayout.ThumbRect(_ui.ScreenSize, offset, rowCount);
            _ui.DrawRect(thumb.Center, thumb.Size,
                view.ThumbDragGrab.HasValue ? SelectedBorderColor : SlotBorderColor);
        }

        DrawPreview(inventory, view, time, headingDegrees);
    }

    /// <summary>Panel tab under a window-pixel point, if any.</summary>
    internal InventoryCategory? PanelTabAt(Vector2 point)
    {
        point = _ui.ToCanvas(point);
        return InventoryPanelLayout.TabAt(point, _ui.ScreenSize);
    }

    /// <summary>
    /// List row index under a window-pixel point for a tab holding this many rows, scrolled by
    /// <paramref name="scrollOffset"/> canvas pixels, if any. Only the row rectangle hits; the
    /// panel's right padding is reserved for the scrollbar.
    /// </summary>
    internal int? PanelRowAt(Vector2 point, float scrollOffset, int rowCount)
    {
        point = _ui.ToCanvas(point);
        return InventoryPanelLayout.RowAt(point, _ui.ScreenSize, scrollOffset, rowCount);
    }

    /// <summary>Whether a window-pixel point is over the panel's search field box.</summary>
    internal bool SearchBoxAt(Vector2 windowPoint) =>
        InventoryPanelLayout.SearchBoxAt(_ui.ToCanvas(windowPoint), _ui.ScreenSize);

    private static string TabTitle(InventoryCategory tab) => tab switch
    {
        InventoryCategory.Blocks => "Blocks",
        InventoryCategory.Tools => "Tools",
        InventoryCategory.Crafting => "Craft",
        _ => "Misc",
    };

    private static string TabSubtitle(Inventory inventory, InventoryCategory tab) =>
        tab == InventoryCategory.Crafting
            ? $"{inventory.Craftable().Count}/{ContentRegistry.CraftingRecipes.Count}"
            : $"{inventory.UsedOf(tab)}/{Inventory.CapacityOf(tab)}";

    private void DrawEntryRow(int row, float offset, byte id, string name, string count, bool selected,
        bool dim, float headingDegrees, float timeSeconds, Rgba? accent = null, float? durability = null,
        float countInset = 0f)
    {
        var centerY = InventoryPanelLayout.RowCenterY(row, offset);
        var center = new Vector2(PanelLeft + PanelWidth * 0.5f, centerY);
        _ui.DrawRect(center, new Vector2(PanelWidth - PanelPad * 2f, RowHeight - 4f),
            selected ? RowSelected : SlotBackground);

        var barColor = accent ?? (selected ? SelectedBorderColor : SlotBorderColor);
        _ui.DrawRect(new Vector2(PanelLeft + PanelPad + 2f, centerY),
            new Vector2(4f, RowHeight - 4f), dim ? TextDim : barColor);

        var iconCenter = new Vector2(PanelLeft + PanelPad + RowIcon * 0.5f + 8f, centerY);
        _ui.DrawIdIcon(iconCenter, RowIcon, id, headingDegrees, timeSeconds);

        _ui.DrawTextCenteredY(_ui.FitText(name, _ui.CountScale, RowNameAvail(count, countInset)), iconCenter.X + RowIcon * 0.5f + 8f, centerY,
            _ui.CountScale, dim && !selected ? TextDim : Rgba.White);

        if (_ui.Font != null)
        {
            var size = _ui.Font.MeasureSize(count.AsSpan(), _ui.CountScale);
            _ui.DrawTextCenteredY(count,
                PanelLeft + PanelWidth - PanelPad - 8f - countInset - size.X, centerY,
                _ui.CountScale, dim && !selected ? TextDim : Rgba.White);
        }

        if (durability != null)
        {
            var barLeft = iconCenter.X + RowIcon * 0.5f + 8f;
            const float barWidth = 140f;
            _ui.DrawDurabilityBar(new Vector2(barLeft + barWidth * 0.5f, centerY + 11f),
                barWidth, durability.Value);
        }
    }

    private void DrawSearchRow(int row, float offset, SearchHit hit, bool selected,
        float headingDegrees, float timeSeconds)
    {
        var detail = hit.IsRecipe ? $"recipe x{hit.Count}"
            : hit.ToolInstance != 0 ? "tool"
            : hit.Count.ToString();
        DrawEntryRow(row, offset, hit.Id, hit.Name, detail, selected, false,
            headingDegrees, timeSeconds, hit.IsRecipe ? AccentGold : null);
    }

    private void DrawCatalogRow(int row, float offset, int rowCount, CreativeCatalog.CatalogEntry entry,
        float headingDegrees, float timeSeconds)
    {
        DrawEntryRow(row, offset, entry.Id, entry.Name, entry.Count.ToString(), false, false,
            headingDegrees, timeSeconds, null, null, InventoryPanelLayout.CatalogButtonGroupWidth + 12f);
        for (var which = 0; which < InventoryPanelLayout.CatalogButtonCount; which++)
        {
            var center = InventoryPanelLayout.CatalogButtonCenter(row, offset, _ui.ScreenSize, rowCount, which);
            var label = (InventoryPanelLayout.CatalogButton)which switch
            {
                InventoryPanelLayout.CatalogButton.AddOne => "+1",
                InventoryPanelLayout.CatalogButton.AddTen => "+10",
                InventoryPanelLayout.CatalogButton.AddHundred => "+100",
                _ => "X",
            };
            var danger = (InventoryPanelLayout.CatalogButton)which == InventoryPanelLayout.CatalogButton.Clear;
            DrawCatalogButton(center, label, danger);
        }
    }

    private void DrawCatalogButton(Vector2 center, string label, bool danger)
    {
        var size = new Vector2(InventoryPanelLayout.CatalogButtonWidth, InventoryPanelLayout.CatalogButtonHeight);
        _ui.DrawRect(center, size, ChipBackground);
        _ui.DrawBorder(center, size, Border.Standard, danger ? DangerRed : AccentGold);
        if (_ui.Font == null) return;

        // Cap-sized, then shrunk to fit the button's inner width so "+100" never spills into
        // its neighbor; the measured ink size centers the label on both axes.
        var scale = _ui.Font.ScaleForCapHeight(CatalogButtonCapHeight);
        var maxWidth = InventoryPanelLayout.CatalogButtonWidth - 8f;
        var width = _ui.Font.MeasureSize(label.AsSpan(), scale).X;
        if (width > maxWidth)
            scale *= maxWidth / width;
        var textSize = _ui.Font.MeasureSize(label.AsSpan(), scale);
        _ui.DrawText(label,
            new Vector2(center.X - textSize.X * 0.5f, center.Y - textSize.Y * 0.5f),
            scale, danger ? DangerRed : Rgba.White);
    }

    private void DrawPreview(Inventory inventory, InventoryView view, float time, float headingDegrees)
    {
        if (view.Tab == InventoryCategory.Crafting)
        {
            _ui.CraftCard.DrawCraftDetail(inventory, view, time, headingDegrees);
            return;
        }

        byte id = 0;
        string name = "";
        string stock = "";
        if (view.Tab == InventoryCategory.Tools)
        {
            if (view.SelectedToolInstance != 0)
            {
                id = inventory.ToolItemId(view.SelectedToolInstance);
                if (id != 0)
                {
                    name = Inventory.DisplayName(id);
                    stock = "tool in hand";
                }
            }
        }
        else if (view.SelectedId != 0 && inventory.CountFor(view.SelectedId) > 0)
        {
            id = view.SelectedId;
            name = Inventory.DisplayName(id);
            stock = $"x{inventory.CountFor(id)} in stock";
        }
        if (id == 0) return;

        var center = new Vector2(_ui.ScreenSize.X - PreviewRight, _ui.ScreenSize.Y * 0.38f);
        var cardSize = new Vector2(PreviewSize + 40f, PreviewSize + 96f);
        _ui.DrawRect(center, cardSize, PanelBackground);
        _ui.DrawBorder(center, cardSize, Border.Standard, SlotBorderColor);
        _ui.DrawRect(new Vector2(center.X, center.Y - cardSize.Y * 0.5f + 3f),
            new Vector2(cardSize.X - 4f, 2f), AccentGold);

        var spin = time * 0.9f;
        if (ItemRegistry.IsItem(id))
            _ui.DrawItemIcon(center with { Y = center.Y - 12f }, PreviewSize * 0.7f, id, spin);
        else
            _ui.DrawBlockIconSpinning(center with { Y = center.Y - 12f }, PreviewSize * 0.7f, id, spin);

        _ui.DrawTextCenteredFitted(name, center.X, center.Y + PreviewSize * 0.5f + 12f, _ui.HeadScale, Rgba.White,
            cardSize.X - 16f);
        _ui.DrawTextCentered(stock, center.X, center.Y + PreviewSize * 0.5f + 40f, _ui.CountScale, TextDim);
        _ui.DrawTextCentered("Click a hotbar slot to equip", center.X,
            center.Y + PreviewSize * 0.5f + 58f, _ui.CountScale, TextDim);
    }

    private float RowNameAvail(string count, float countInset)
    {
        var nameX = PanelLeft + PanelPad + RowIcon + 16f;
        var countW = _ui.TextWidth(count, _ui.CountScale);
        return PanelLeft + PanelWidth - PanelPad - 8f - countInset - countW - nameX - 8f;
    }
}
