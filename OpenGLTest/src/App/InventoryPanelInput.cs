using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Content;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Menus;

namespace VoxelEngine.App;

/// <summary>The [E] panel's input: opening and closing it, clicks, the scrollbar, the search field's keys, the creative catalog buttons, the hovered row (number-key equips) and the hover tooltip. Every change the server owns is sent through the client session as well. Owned by Program; main thread only.</summary>
internal sealed class InventoryPanelInput
{
    private readonly InputManager inputManager;
    private readonly RenderManager renderManager;
    private readonly InventoryView inventoryView;
    private readonly PauseMenuView pauseMenuView;
    private readonly ClientSession session;

    internal InventoryPanelInput(InputManager inputManager, RenderManager renderManager, InventoryView inventoryView, PauseMenuView pauseMenuView, ClientSession session)
    {
        this.inputManager = inputManager;
        this.renderManager = renderManager;
        this.inventoryView = inventoryView;
        this.pauseMenuView = pauseMenuView;
        this.session = session;
    }

    private World? ActiveWorld => Program.ActiveWorld;

    // Hotbar hotkeys 1-0, hoisted so the per-frame poll allocates nothing.
    internal static readonly Key[] NumberKeys =
        [Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5,
            Key.Number6, Key.Number7, Key.Number8, Key.Number9, Key.Number0];

    /// <summary>
    /// The tooltip for whatever the cursor hovers while the [E] panel is open: a panel row's item
    /// (or the tool instance it names) or a hotbar slot's, with the cursor as the anchor. Null
    /// when nothing hovers.
    /// </summary>
    internal (Vector2 Anchor, List<string> Lines)? HoveredTooltip()
    {
        if (!inventoryView.IsOpen || pauseMenuView.IsOpen || inputManager?.Mouse == null) return null;
        var inventory = ActiveWorld?.Inventory;
        if (inventory == null || renderManager == null) return null;
        var position = new Vector2(inputManager.Mouse.Position.X, inputManager.Mouse.Position.Y);

        // A panel row first (the rows sit over the list), then a hotbar slot
        if (HoveredRow(out var rowId, out var rowTool))
        {
            var stack = rowTool != 0 ? inventory.GetToolStack(rowTool) : null;
            return (position, ItemTooltip.Lines(rowTool != 0 ? stack!.DefinitionId : rowId, stack));
        }

        if (renderManager.Ui.Hud.HotbarSlotAt(position) is not { } slot || slot < 0 || inventory.IdAt(slot) == 0)
            return null;
        {
            var tool = inventory.ToolInstanceAt(slot);
            var stack = tool != 0 ? inventory.GetToolStack(tool) : null;
            return (position, ItemTooltip.Lines(inventory.IdAt(slot), stack));
        }
    }

    /// <summary>
    /// The panel row under the cursor, if any: its item id, or the tool instance it names (then
    /// the id is 0). The one reading of "which row is hovered" the number keys and the tooltip
    /// share, so they never disagree.
    /// </summary>
    private bool HoveredRow(out byte id, out int toolInstance)
    {
        id = 0;
        toolInstance = 0;
        var inventory = ActiveWorld?.Inventory;
        if (inventory == null || inputManager?.Mouse == null || renderManager == null) return false;
        if (!inventoryView.Search.IsEmpty) return false;
        if (inventoryView.Tab == InventoryCategory.Crafting) return false;
        if (ActiveWorld!.Camera.IsCreative) return false;

        var rowCount = RowCountFor(inventoryView.Tab);
        var offset = InventoryPanelLayout.ClampedOffset(inventoryView.ScrollOffset, rowCount,
            renderManager.Ui.ScreenSize);
        var row = renderManager.Ui.Panel.PanelRowAt(
            new Vector2(inputManager.Mouse.Position.X, inputManager.Mouse.Position.Y), offset, rowCount);
        if (row == null) return false;

        if (inventoryView.Tab == InventoryCategory.Tools)
        {
            var entries = inventory.ToolEntries();
            if (row.Value >= entries.Count) return false;
            toolInstance = entries[row.Value].InstanceId;
            return true;
        }

        var stacks = inventory.StackEntries(inventoryView.Tab);
        if (row.Value >= stacks.Count) return false;
        id = stacks[row.Value].Id;
        return true;
    }

    /// <summary>
    /// The number keys while the [E] panel is open: the row under the cursor goes into that hotbar
    /// slot, the same equip a click on the slot performs with the row selected. Only the plain
    /// tabs — a search result and a creative catalog row have their own click actions.
    /// </summary>
    internal void AssignHoveredRowToHotbar()
    {
        var inventory = ActiveWorld?.Inventory;
        if (inventory == null) return;
        if (!HoveredRow(out var id, out var tool)) return;
        if (id == 0 && tool == 0) return;

        for (var i = 0; i < NumberKeys.Length; i++)
        {
            if (inputManager!.Keyboard?.IsKeyPressed(NumberKeys[i]) != true) continue;
            var intent = tool != 0
                ? InventoryIntent.EquipTool(i, tool)
                : InventoryIntent.EquipStackable(i, id);
            if (session.SendInventoryIntent(intent)) return;
            if (tool != 0)
                inventory.EquipTool(i, tool);
            else
                inventory.EquipStackable(i, id);
            return;
        }
    }

    /// <summary>
    /// Opens or closes the [E] panel: the mouse is freed for clicking (and the look frozen)
    /// while it is open, then captured again. Closing also drops any list pick.
    /// </summary>
    internal void SetInventoryOpen(bool open)
    {
        // Only a playing world has a panel; while loading, SuppressLook belongs to the World
        if (ActiveWorld is not { IsPlaying: true }) return;
        inventoryView.IsOpen = open;
        if (!open)
        {
            inventoryView.ClearSelection();
            inventoryView.ClearInput();
            inventoryView.Craft.Clear();
            inventoryView.EndThumbDrag();
        }
        ActiveWorld.Camera.SuppressLook = open;
        inputManager.Mouse?.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
    }

    /// <summary>Number of list rows the given panel tab shows this frame. In creative the
    /// Blocks/Tools/Misc tabs list the whole catalog; the Craft tab always lists every recipe.</summary>
    internal int RowCountFor(InventoryCategory tab)
    {
        if (tab == InventoryCategory.Crafting)
            return ContentRegistry.CraftingRecipes.Count;
        if (ActiveWorld?.Camera.IsCreative == true)
            return CreativeCatalog.Entries(tab, ActiveWorld.Inventory).Count;
        return tab == InventoryCategory.Tools
            ? ActiveWorld!.Inventory.ToolEntries().Count
            : ActiveWorld!.Inventory.StackEntries(tab).Count;
    }

    /// <summary>
    /// A left press on the open panel's scrollbar: the thumb starts a drag (the grab offset
    /// and row count are latched so the per-frame drag never re-sorts the list), the track
    /// above or below it pages by one viewport.
    /// </summary>
    internal void HandleInventoryScrollbarPress(Vector2 position)
    {
        var canvas = renderManager.Ui.ScreenSize;
        var p = renderManager.Ui.ToCanvas(position);
        var searching = !inventoryView.Search.IsEmpty;
        var rowCount = searching
            ? InventorySearch.Search(inventoryView.Search.Query, ActiveWorld!.Inventory, ActiveWorld.Camera.IsCreative).Count
            : RowCountFor(inventoryView.Tab);
        var offset = searching
            ? InventoryPanelLayout.ClampedOffset(inventoryView.Search.ScrollOffset, rowCount, canvas)
            : InventoryPanelLayout.ClampedOffset(inventoryView.ScrollOffset, rowCount, canvas);
        switch (InventoryPanelLayout.ScrollbarHitAt(p, canvas, offset, rowCount))
        {
            case ScrollbarHit.Thumb:
                var thumb = InventoryPanelLayout.ThumbRect(canvas, offset, rowCount);
                inventoryView.ThumbDragGrab = p.Y - (thumb.Center.Y - thumb.Size.Y / 2f);
                inventoryView.ThumbDragRowCount = rowCount;
                break;
            case ScrollbarHit.TrackAbove:
                SetListScroll(InventoryPanelLayout.ClampedOffset(
                    offset - InventoryPanelLayout.ListHeight(canvas), rowCount, canvas), searching);
                break;
            case ScrollbarHit.TrackBelow:
                SetListScroll(InventoryPanelLayout.ClampedOffset(
                    offset + InventoryPanelLayout.ListHeight(canvas), rowCount, canvas), searching);
                break;
        }
    }

    /// <summary>Types one printable character into the focused inventory field.</summary>
    internal void HandleInventoryType(char c)
    {
        if (inventoryView.Focus != InventoryFocus.Search) return;
        if (!inventoryView.Search.Text.TryAppend(c)) return;
        inventoryView.Search.Highlight = -1;
        inventoryView.Search.ScrollOffset = 0f;
    }

    /// <summary>The non-character keys a focused inventory field reacts to. ESC is left to the
    /// polled panel toggle, so one press never does two things.</summary>
    internal void HandleInventoryKey(Key key)
    {
        switch (key)
        {
            case Key.Backspace:
                if (inventoryView.Focus != InventoryFocus.Search) break;
                inventoryView.Search.Text.Backspace();
                inventoryView.Search.Highlight = -1;
                break;
            case Key.Tab:
                inventoryView.Focus = InventoryFocus.Search;
                break;
            case Key.Enter or Key.KeypadEnter:
                ApplyHighlightedSearchHit();
                break;
            case Key.Up:
                MoveSearchHighlight(-1);
                break;
            case Key.Down:
                MoveSearchHighlight(1);
                break;
        }
    }

    private void MoveSearchHighlight(int delta)
    {
        if (inventoryView.Search.IsEmpty) return;
        var count = InventorySearch.Search(inventoryView.Search.Query, ActiveWorld!.Inventory, ActiveWorld.Camera.IsCreative).Count;
        if (count == 0) return;
        inventoryView.Search.Highlight = inventoryView.Search.Highlight < 0
            ? (delta > 0 ? 0 : count - 1)
            : Math.Clamp(inventoryView.Search.Highlight + delta, 0, count - 1);
        EnsureSearchHighlightVisible(count);
    }

    private void EnsureSearchHighlightVisible(int count)
    {
        var screen = renderManager.Ui.ScreenSize;
        var viewport = InventoryPanelLayout.ListHeight(screen);
        var rowTop = inventoryView.Search.Highlight * InventoryPanelLayout.RowHeight;
        var offset = inventoryView.Search.ScrollOffset;
        if (rowTop < offset)
            offset = rowTop;
        else if (rowTop + InventoryPanelLayout.RowHeight > offset + viewport)
            offset = rowTop + InventoryPanelLayout.RowHeight - viewport;
        inventoryView.Search.ScrollOffset = InventoryPanelLayout.ClampedOffset(offset, count, screen);
    }

    private void ApplyHighlightedSearchHit()
    {
        if (inventoryView.Search.IsEmpty) return;
        var hits = InventorySearch.Search(inventoryView.Search.Query, ActiveWorld!.Inventory, ActiveWorld.Camera.IsCreative);
        if (hits.Count == 0) return;
        // The highlight is only clamped by the arrow keys; the list can shrink under it (an owned
        // item crafted away, a server inventory sync), so Enter falls back to the first hit.
        var highlight = inventoryView.Search.Highlight;
        if (highlight < 0 || highlight >= hits.Count)
            highlight = 0;
        ApplySearchHit(hits[highlight]);
    }

    /// <summary>
    /// Acts on one search result: an owned item becomes the panel's pick (a recipe that is open
    /// is fed it), while a recipe opens on the Craft tab and clears the query.
    /// </summary>
    private void ApplySearchHit(SearchHit hit)
    {
        if (hit.IsRecipe)
        {
            inventoryView.Tab = InventoryCategory.Crafting;
            inventoryView.Craft.Select(hit.Recipe);
            inventoryView.CraftScrollOffset = 0f;
            inventoryView.Search.Clear();
            inventoryView.Focus = InventoryFocus.None;
            return;
        }

        if (ActiveWorld!.Camera.IsCreative)
        {
            GrantFromCreative(hit.Id, 1);
            return;
        }

        inventoryView.SelectedId = hit.ToolInstance > 0 ? (byte)0 : hit.Id;
        inventoryView.SelectedToolInstance = hit.ToolInstance;
        // The hotbar equip and the preview read the pick through the current tab, so a tool
        // picked from the Blocks tab's search has to land on the Tools tab to be equippable.
        inventoryView.Tab = hit.Category;
        if (hit.ToolInstance == 0 && inventoryView.Craft.Recipe != null)
            inventoryView.Craft.FeedFromInventory(hit.Id, ActiveWorld.Inventory);
    }

    /// <summary>Grants a catalog id to the player, popping the hotbar slot a fresh pickup lands
    /// in; the catalog's +1/+10/+100 buttons and the row body go through here.</summary>
    private void GrantFromCreative(byte id, int amount)
    {
        var inventory = ActiveWorld!.Inventory;
        if (!inventory.Add(id, amount)) return;
        // Applied here at once; on a server only a player it lets be creative keeps it
        session.SendInventoryIntent(InventoryIntent.CreativeGrant(id, amount));
        if (Inventory.CategoryOf(id) is InventoryCategory.Blocks or InventoryCategory.Misc)
            inventoryView.SelectedId = id;
    }

    /// <summary>
    /// Acts on one creative catalog row click: a button grants its amount or clears the id's
    /// counts, the row body grants one.
    /// </summary>
    private void ApplyCatalogButton(byte id, int row, float offset, int rowCount,
        Vector2 position)
    {
        var button = InventoryPanelLayout.CatalogButtonAt(renderManager.Ui.ToCanvas(position),
            row, offset, rowCount, renderManager.Ui.ScreenSize);
        switch (button)
        {
            case InventoryPanelLayout.CatalogButton.AddOne:
                GrantFromCreative(id, 1);
                break;
            case InventoryPanelLayout.CatalogButton.AddTen:
                GrantFromCreative(id, 10);
                break;
            case InventoryPanelLayout.CatalogButton.AddHundred:
                GrantFromCreative(id, 100);
                break;
            case InventoryPanelLayout.CatalogButton.Clear:
                if (ActiveWorld!.Inventory.RemoveAll(id))
                {
                    session.SendInventoryIntent(InventoryIntent.CreativeClear(id));
                    inventoryView.ClearSelection();
                }
                break;
            default:
                GrantFromCreative(id, 1);
                break;
        }
    }

    internal void SetListScroll(float offset, bool searching)
    {
        if (searching) inventoryView.Search.ScrollOffset = offset;
        else inventoryView.ScrollOffset = offset;
    }

    /// <summary>
    /// Routes one left-click inside the open panel: the field boxes focus, tabs switch, rows
    /// pick for preview (or act on a search result), crafting controls feed the open recipe, and
    /// hotbar slots equip the pick (clicking what the slot already shows clears it).
    /// </summary>
    internal void HandleInventoryClick(Vector2 position)
    {
        var inventory = ActiveWorld!.Inventory;
        var creative = ActiveWorld.Camera.IsCreative;

        // The search box takes the click before anything else; clicking elsewhere drops focus.
        if (renderManager.Ui.Panel.SearchBoxAt(position))
        {
            inventoryView.Focus = InventoryFocus.Search;
            return;
        }
        inventoryView.Focus = InventoryFocus.None;

        var tab = renderManager.Ui.Panel.PanelTabAt(position);
        if (tab.HasValue)
        {
            inventoryView.Tab = tab.Value;
            inventoryView.Search.Clear();
            inventoryView.ClearSelection();
            return;
        }

        var searching = !inventoryView.Search.IsEmpty;
        var hits = searching
            ? InventorySearch.Search(inventoryView.Search.Query, inventory, creative)
            : null;
        var rowCount = hits?.Count ?? RowCountFor(inventoryView.Tab);
        var offset = searching
            ? InventoryPanelLayout.ClampedOffset(inventoryView.Search.ScrollOffset, rowCount, renderManager.Ui.ScreenSize)
            : InventoryPanelLayout.ClampedOffset(inventoryView.ScrollOffset, rowCount, renderManager.Ui.ScreenSize);
        // The scrollbar is handled on press; its release must not fall through to the
        // crafting card or hotbar tests below.
        if (InventoryPanelLayout.ScrollbarHitAt(renderManager.Ui.ToCanvas(position),
                renderManager.Ui.ScreenSize, offset, rowCount) != ScrollbarHit.None)
            return;
        var row = renderManager.Ui.Panel.PanelRowAt(position, offset, rowCount);
        if (row.HasValue)
        {
            if (hits != null)
            {
                if (row.Value < hits.Count)
                    ApplySearchHit(hits[row.Value]);
            }
            else if (creative && inventoryView.Tab != InventoryCategory.Crafting)
            {
                var entries = CreativeCatalog.Entries(inventoryView.Tab, inventory);
                if (row.Value < entries.Count)
                    ApplyCatalogButton(entries[row.Value].Id, row.Value, offset, rowCount, position);
            }
            else switch (inventoryView.Tab)
            {
                case InventoryCategory.Tools:
                    inventoryView.SelectedToolInstance = inventory.ToolEntries()[row.Value].InstanceId;
                    inventoryView.SelectedId = 0;
                    break;
                case InventoryCategory.Crafting:
                    inventoryView.Craft.Select(ContentRegistry.CraftingRecipes[row.Value]);
                    // A newly opened recipe starts at the top of its card.
                    inventoryView.CraftScrollOffset = 0f;
                    break;
                default:
                {
                    var picked = inventory.StackEntries(inventoryView.Tab)[row.Value].Id;
                    inventoryView.SelectedId = picked;
                    inventoryView.SelectedToolInstance = 0;
                    if (inventoryView.Craft.Recipe != null)
                        inventoryView.Craft.FeedFromInventory(picked, inventory);
                    break;
                }
            }
            return;
        }

        if (inventoryView.Tab == InventoryCategory.Crafting)
        {
            var hit = renderManager.Ui.CraftCard.CraftHitAt(position, inventoryView);
            var craft = inventoryView.Craft;
            switch (hit.Kind)
            {
                case CraftHitKind.BatchMinus:
                    craft.SetBatch(craft.Batch - 1);
                    return;
                case CraftHitKind.BatchPlus:
                    craft.SetBatch(craft.Batch + 1);
                    return;
                case CraftHitKind.IngMinus:
                    craft.FeedLess(hit.IngredientIndex);
                    return;
                case CraftHitKind.IngPlus:
                    craft.FeedMore(hit.IngredientIndex, inventory);
                    return;
                case CraftHitKind.MemberChip:
                    craft.SetMember(hit.IngredientIndex, hit.MemberId, inventory);
                    craft.FeedMore(hit.IngredientIndex, inventory);
                    return;
                case CraftHitKind.CraftButton:
                {
                    // The fed members are read before the craft, which spends the reservation
                    var recipe = craft.Recipe;
                    var batch = craft.Batch;
                    var members = new byte[recipe?.Ingredients.Length ?? 0];
                    for (var i = 0; i < members.Length; i++)
                        members[i] = craft.FedAt(i).MemberId;
                    if (craft.Craft(inventory) && recipe != null)
                        session.SendInventoryIntent(InventoryIntent.Craft(recipe.Name, batch, members));
                    return;
                }
                case CraftHitKind.None:
                    break;
            }
        }

        // Equipped here at once and, while connected, sent as an intent: the server applies the
        // same equip to its copy (only for something this player owns), so the next sync keeps it.
        var hotbar = renderManager.Ui.Hud.HotbarSlotAt(position);
        if (!hotbar.HasValue) return;
        if (inventoryView.Tab == InventoryCategory.Tools && inventoryView.SelectedToolInstance != 0)
        {
            inventory.EquipTool(hotbar.Value, inventoryView.SelectedToolInstance);
            session.SendInventoryIntent(InventoryIntent.EquipTool(hotbar.Value, inventoryView.SelectedToolInstance));
        }
        else if (inventoryView.SelectedId != 0)
        {
            inventory.EquipStackable(hotbar.Value, inventoryView.SelectedId);
            session.SendInventoryIntent(InventoryIntent.EquipStackable(hotbar.Value, inventoryView.SelectedId));
        }
    }
}
