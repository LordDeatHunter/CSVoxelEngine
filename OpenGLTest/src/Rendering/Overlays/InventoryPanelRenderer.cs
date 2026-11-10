using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Data;
using VoxelEngine.Items;
using VoxelEngine.UI;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Widgets;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The [E] inventory panel: its tabs, search field and scope button, list rows, creative catalog
/// buttons and preview card, and the hit-tests that read the same <see cref="InventoryPanelLayout"/>, so a
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
    private static readonly Rgba RowSelected = Rgba.Gray(0.22f, 0.8f);

    private readonly GL _gl;
    private readonly UIRenderer _ui;

    // This frame's pointer (canvas units), read from the view when the panel starts drawing.
    private Vector2? _hover;

    internal InventoryPanelRenderer(GL gl, UIRenderer ui)
    {
        _gl = gl;
        _ui = ui;
    }

    /// <summary>
    /// The [E] panel: tabs and a name-sorted list on the left, a rotating preview with the
    /// picked name on the right. The hotbar stays visible underneath so picks can be aimed
    /// at slots. <paramref name="time"/> spins the preview. With <paramref name="chestOpen"/> the chest card
    /// takes the right side, so no preview, equipment or crafting card is drawn under it.
    /// </summary>
    internal void DrawInventoryPanel(Inventory inventory, InventoryView view, float time,
        float headingDegrees, bool creative, bool chestOpen)
    {
        var top = PanelTop;
        var height = InventoryPanelLayout.PanelHeight(_ui.ScreenSize);
        var panel = new Vector2(PanelLeft + PanelWidth * 0.5f, top + height * 0.5f);
        _hover = view.HoverCanvas;
        _ui.DrawPanelFrame(new UiRect(panel, new Vector2(PanelWidth, height)), PanelBackground);

        var tabs = InventoryPanelLayout.Tabs;
        var tabWidth = InventoryPanelLayout.TabWidth(_ui.ScreenSize);
        for (var t = 0; t < tabs.Length; t++)
        {
            var (center, size) = InventoryPanelLayout.TabRect(t, _ui.ScreenSize);
            var active = view.Tab == tabs[t];
            var hovered = !active && _hover is { } p && new UiRect(center, size).Contains(p);
            _ui.DrawRect(center, size, active ? SelectedBackground : hovered ? HoverBackground : SlotBackground);
            _ui.DrawTextCenteredFitted(PanelTabLabels.Title(tabs[t]),
                center.X, top + PanelPad + 6f, _ui.CountScale, Rgba.White, tabWidth - 8f);
            _ui.DrawTextCenteredFitted(PanelTabLabels.Subtitle(inventory, tabs[t], creative),
                center.X, top + PanelPad + 30f, _ui.CountScale, Rgba.White, tabWidth - 8f);
        }

        // The active tab's underline slides between tabs (InventoryView.TabIndicator is eased), so a switch reads as motion.
        var indicatorCenterX = InventoryPanelLayout.TabCenter(0, _ui.ScreenSize).X + view.TabIndicator * tabWidth;
        _ui.DrawRect(new Vector2(indicatorCenterX, top + PanelPad + TabHeight - 1f),
            new Vector2(tabWidth - InventoryPanelLayout.TabGap, 2f), AccentGold);

        var (searchCenter, searchSize) = InventoryPanelLayout.SearchBoxRect(_ui.ScreenSize);
        _ui.DrawInputField(searchCenter, searchSize, view.Search.Text,
            view.Focus == InventoryFocus.Search, SearchPlaceholder(view.SearchScope), time);
        DrawScopeButton(view.SearchScope);

        // A query replaces the tab's list with search results; the list region below doubles as
        // the field's dropdown, and InventoryPanelInput.HandleInventoryClick reads the same
        // InventoryView.SearchHits call, so the drawn rows and the click always agree.
        IReadOnlyList<SearchHit>? hits = view.Search.IsEmpty
            ? null
            : view.SearchHits(inventory, creative);

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
        else if (view.Tab is InventoryCategory.Tools or InventoryCategory.Gear)
            rowCount = (tools = view.Tab == InventoryCategory.Gear ? inventory.GearEntries() : inventory.ToolEntries()).Count;
        else if (view.Tab == InventoryCategory.Crafting)
            rowCount = (recipes = inventory.ListedRecipes(creative)).Count;
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
                // In creative an item hit is a catalog row, with the same grant buttons as the tab's list
                for (var i = first; i < end; i++)
                {
                    var highlighted = i == view.Search.Highlight;
                    if (creative && !hits[i].IsRecipe)
                        DrawCatalogRow(i, offset, rowCount, inventory, hits[i].Id, hits[i].Name, hits[i].Count,
                            highlighted, headingDegrees, time);
                    else
                        DrawSearchRow(i, offset, inventory, hits[i], highlighted, headingDegrees, time);
                }
            }
            else if (catalog != null)
            {
                for (var i = first; i < end; i++)
                    DrawCatalogRow(i, offset, rowCount, inventory, catalog[i].Id, catalog[i].Name, catalog[i].Count,
                        false, headingDegrees, time);
            }
            else if (tools != null)
            {
                for (var i = first; i < end; i++)
                {
                    var selected = view.SelectedToolInstance == tools[i].InstanceId;
                    var mark = EquippedMark.Of(inventory, tools[i].ItemId, tools[i].InstanceId);
                    DrawEntryRow(i, offset, tools[i].ItemId, tools[i].Name, mark.CountLabel("1"), selected, false,
                        headingDegrees, time, RowRarity(inventory, tools[i].ItemId, tools[i].InstanceId), mark.Accent,
                        inventory.ToolDurabilityFraction(tools[i].InstanceId), hotbarSlot: mark.HotbarSlot);
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
                        !ready, headingDegrees, time, RowRarity(inventory, recipes[i].ResultId, 0),
                        ready ? ReadyGreen : null);
                }
            }
            else if (rows != null)
            {
                for (var i = first; i < end; i++)
                {
                    var mark = EquippedMark.Of(inventory, rows[i].Id, 0);
                    DrawEntryRow(i, offset, rows[i].Id, rows[i].Name, rows[i].Count.ToString(),
                        view.SelectedId == rows[i].Id, false, headingDegrees, time, RowRarity(inventory, rows[i].Id, 0),
                        mark.Accent, hotbarSlot: mark.HotbarSlot);
                }
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

        if (!chestOpen)
            DrawPreview(inventory, view, time, headingDegrees);
    }

    /// <summary>
    /// A row being dragged (<see cref="RowDrag"/>): its item rides the pointer over everything drawn so far, the
    /// rows, the cards and the hotbar it is aimed at.
    /// </summary>
    internal void DrawDraggedRow(ushort id, Vector2 pointer, float headingDegrees, float timeSeconds)
    {
        _ui.ClearDepthForOverlay();
        _ui.DrawIdIcon(pointer, RowIcon * 1.4f, id, headingDegrees, timeSeconds);
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

    /// <summary>Whether a window-pixel point is over the search scope's cycle button.</summary>
    internal bool ScopeButtonAt(Vector2 windowPoint) =>
        InventoryPanelLayout.ScopeButtonAt(_ui.ToCanvas(windowPoint), _ui.ScreenSize);

    private static string SearchPlaceholder(SearchScope scope) => scope switch
    {
        SearchScope.Items => "Search items",
        SearchScope.Recipes => "Search recipes",
        _ => "Search items and recipes",
    };

    // The search scope's cycle button beside the field: its label names what the search lists, and a narrowed
    // scope wears the accent border so it is never mistaken for the default.
    private void DrawScopeButton(SearchScope scope)
    {
        var (center, size) = InventoryPanelLayout.ScopeButtonRect(_ui.ScreenSize);
        var hovered = _hover is { } p && new UiRect(center, size).Contains(p);
        _ui.DrawRect(center, size, hovered ? ButtonHover : ChipBackground);
        _ui.DrawBorder(center, size, Border.Standard, scope == SearchScope.All ? SlotBorderColor : AccentGold);
        var label = scope switch
        {
            SearchScope.Items => "Items",
            SearchScope.Recipes => "Recipes",
            _ => "All",
        };
        _ui.DrawTextMiddle(_ui.FitText(label, _ui.CountScale, size.X - 8f), center, _ui.CountScale, Rgba.White);
    }

    // The rarity a row shows: a tool or gear row's own rolled stack, else its item's.
    private static Rarity RowRarity(Inventory inventory, ushort id, int toolInstance) =>
        Rarities.Of(id, toolInstance != 0 ? inventory.GetToolStack(toolInstance) : null);

    private void DrawEntryRow(int row, float offset, ushort id, string name, string count, bool selected,
        bool dim, float headingDegrees, float timeSeconds, Rarity rarity, Rgba? accent = null, float? durability = null,
        float countInset = 0f, int hotbarSlot = -1)
    {
        var centerY = InventoryPanelLayout.RowCenterY(row, offset);
        var center = new Vector2(PanelLeft + PanelWidth * 0.5f, centerY);
        var rowSize = new Vector2(PanelWidth - PanelPad * 2f, RowHeight - 4f);
        var hovered = !selected && _hover is { } pointer && new UiRect(center, rowSize).Contains(pointer)
            && pointer.Y >= InventoryPanelLayout.ListTop
            && pointer.Y < InventoryPanelLayout.ListTop + InventoryPanelLayout.ListHeight(_ui.ScreenSize);
        _ui.DrawRect(center, rowSize, selected ? RowSelected : hovered ? HoverBackground : SlotBackground);
        var style = RarityStyle.Of(rarity);
        _ui.Chrome.DrawRow(center, rowSize, style, timeSeconds, row, dim ? RarityStyleConfig.DimOpacity : 1f);

        // The accent bar drawn over the chrome stays the row's status (equipped, ready, a recipe hit); the rarity
        // shows through the chrome and the name color (Uncommon's green equals the ready green).
        var barColor = accent ?? (selected ? SelectedBorderColor : SlotBorderColor);
        _ui.DrawRect(new Vector2(PanelLeft + PanelPad + 2f, centerY),
            new Vector2(4f, RowHeight - 4f), dim ? TextDim : barColor);

        var iconCenter = new Vector2(PanelLeft + PanelPad + RowIcon * 0.5f + 8f, centerY);
        _ui.DrawIdIcon(iconCenter, RowIcon, id, headingDegrees, timeSeconds);
        if (hotbarSlot >= 0)
            DrawHotbarChip(iconCenter, hotbarSlot);

        _ui.DrawTextCenteredY(_ui.FitText(name, _ui.CountScale, RowNameAvail(count, countInset)), iconCenter.X + RowIcon * 0.5f + 8f, centerY,
            _ui.CountScale, dim && !selected ? TextDim : style.Color);

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

    // An owned item hit wears the same equipped marks as the tab's own rows; a recipe hit stays gold
    private void DrawSearchRow(int row, float offset, Inventory inventory, SearchHit hit, bool selected,
        float headingDegrees, float timeSeconds)
    {
        if (hit.IsRecipe)
        {
            DrawEntryRow(row, offset, hit.Id, hit.Name, $"recipe x{hit.Count}", selected, false,
                headingDegrees, timeSeconds, RowRarity(inventory, hit.Id, 0), AccentGold);
            return;
        }

        var mark = EquippedMark.Of(inventory, hit.Id, hit.ToolInstance);
        DrawEntryRow(row, offset, hit.Id, hit.Name, mark.CountLabel(hit.ToolInstance != 0 ? "tool" : hit.Count.ToString()),
            selected, false, headingDegrees, timeSeconds, RowRarity(inventory, hit.Id, hit.ToolInstance), mark.Accent,
            hotbarSlot: mark.HotbarSlot);
    }

    private void DrawCatalogRow(int row, float offset, int rowCount, Inventory inventory, ushort id, string name, int count,
        bool selected, float headingDegrees, float timeSeconds)
    {
        var mark = EquippedMark.Of(inventory, id, 0);
        DrawEntryRow(row, offset, id, name, mark.CountLabel(count.ToString()), selected, false,
            headingDegrees, timeSeconds, RowRarity(inventory, id, 0), mark.Accent, null,
            InventoryPanelLayout.CatalogButtonGroupWidth + 12f, mark.HotbarSlot);
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
        _ui.DrawRect(center, size, _ui.IsHovered(center, size) ? ButtonHover : ChipBackground);
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

        if (view.Tab == InventoryCategory.Gear)
        {
            _ui.EquipmentCard.Draw(inventory, view.SelectedToolInstance, view.HoverCanvas, time, headingDegrees);
            return;
        }

        ushort id = 0;
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

        var (center, cardSize) = InventoryPanelLayout.DetailCardRect(_ui.ScreenSize);
        _ui.DrawPanelFrame(new UiRect(center, cardSize), PanelBackground);

        // Everything is placed from the card's top edge, so the content always stays inside the frame.
        var cardTop = center.Y - cardSize.Y * 0.5f;
        var inner = cardSize.X - UiTheme.Space.Md * 2f;
        var spin = time * 0.9f;
        var iconCenter = new Vector2(center.X, cardTop + UiTheme.Space.Lg + PreviewSize * 0.35f);
        if (ItemRegistry.IsItem(id))
            _ui.DrawItemIcon(iconCenter, PreviewSize * 0.7f, id, spin);
        else
            _ui.DrawBlockIconSpinning(iconCenter, PreviewSize * 0.7f, id, spin);

        var textTop = cardTop + UiTheme.Space.Lg * 2f + PreviewSize * 0.7f;
        _ui.DrawTextCenteredFitted(name, center.X, textTop, _ui.HeadScale, Rgba.White, inner);
        _ui.DrawTextCenteredFitted(stock, center.X, textTop + 30f, _ui.CountScale, TextDim, inner);
        _ui.DrawTextCenteredFitted("Click a slot to equip", center.X, textTop + 52f, _ui.CountScale, TextDim, inner);
    }

    // The hotbar key ("1".."9", "0") of the slot showing a row's item, on a chip over the icon's top-left corner
    private void DrawHotbarChip(Vector2 iconCenter, int slot)
    {
        var center = iconCenter - new Vector2(RowIcon * 0.5f - 4f);
        var size = new Vector2(19f);
        _ui.DrawRect(center, size, ChipBackground);
        _ui.DrawBorder(center, size, Border.Thin, ReadyGreen);
        _ui.DrawTextMiddle(EquippedMark.HotbarKey(slot), center, _ui.CapHeightScale(11f), Rgba.White);
    }

    private float RowNameAvail(string count, float countInset)
    {
        var nameX = PanelLeft + PanelPad + RowIcon + 16f;
        var countW = _ui.TextWidth(count, _ui.CountScale);
        return PanelLeft + PanelWidth - PanelPad - 8f - countInset - countW - nameX - 8f;
    }
}
