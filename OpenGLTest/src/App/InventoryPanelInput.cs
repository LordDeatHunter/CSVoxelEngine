using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.UI;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Menus;

namespace VoxelEngine.App;

/// <summary>The [E] panel's input: opening and closing it, clicks and the right-click equip, the scrollbar, the search field's keys and scope button, the creative catalog buttons, the hovered row (number-key equips) and the hover tooltip. Every change the server owns is sent through the client session as well. Owned by Program; main thread only.</summary>
internal sealed class InventoryPanelInput
{
    private readonly InputManager inputManager;
    private readonly RenderManager renderManager;
    private readonly InventoryView inventoryView;
    private readonly PauseMenuView pauseMenuView;
    private readonly ClientSession session;
    private readonly TextFieldKeys textKeys;

    internal InventoryPanelInput(InputManager inputManager, RenderManager renderManager, InventoryView inventoryView, PauseMenuView pauseMenuView, ClientSession session)
    {
        this.inputManager = inputManager;
        this.renderManager = renderManager;
        this.inventoryView = inventoryView;
        this.pauseMenuView = pauseMenuView;
        this.session = session;
        textKeys = new TextFieldKeys(inputManager);
        Chest = new ChestInput(inputManager, renderManager, inventoryView, session, this);
        RowDrag = new RowDragInput(renderManager, inventoryView, session, this);
    }

    /// <summary>The chest panel's input: it opens beside this panel and takes deposits from its list.</summary>
    internal ChestInput Chest { get; }

    /// <summary>Dragging a listed row onto a hotbar or equipment slot.</summary>
    internal RowDragInput RowDrag { get; }

    private World? ActiveWorld => Program.ActiveWorld;

    /// <summary>Where a scenario holds the pointer, in canvas units; null leaves it to the mouse. Only the visual
    /// director sets it, so a hover can be filmed without a device cursor.</summary>
    internal Vector2? ScriptedPointer { get; set; }

    // The pointer in window pixels (what every hit-test takes): the scripted one, else the mouse.
    private Vector2? PointerWindow()
    {
        if (ScriptedPointer is { } scripted)
            return scripted * renderManager.Ui.Scale;
        return inputManager?.Mouse is { } mouse ? new Vector2(mouse.Position.X, mouse.Position.Y) : null;
    }

    /// <summary>The pointer in canvas units (the scripted one, else the mouse), for button hover looks; null with no pointer.</summary>
    internal Vector2? UiPointerCanvas() =>
        PointerWindow() is { } window && renderManager != null ? renderManager.Ui.ToCanvas(window) : null;

    /// <summary>The pointer in canvas units (the scripted one, else the mouse), for hover looks; null with no pointer
    /// or no open panel.</summary>
    internal Vector2? PointerCanvas() =>
        inventoryView.IsOpen && !pauseMenuView.IsOpen && PointerWindow() is { } window && renderManager != null
            ? renderManager.Ui.ToCanvas(window)
            : null;

    // Hotbar hotkeys 1-0, hoisted so the per-frame poll allocates nothing.
    internal static readonly Key[] NumberKeys =
        [Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5,
            Key.Number6, Key.Number7, Key.Number8, Key.Number9, Key.Number0];

    /// <summary>
    /// The tooltip for whatever the cursor hovers while the [E] panel is open: a listed row's item
    /// (a search hit's, a recipe's result, a creative catalog row's, or the tool instance a row
    /// names) or a hotbar slot's, with the cursor as the anchor and the hovered item's rarity, which dresses the box.
    /// Null when nothing hovers.
    /// </summary>
    internal HoverTooltip? HoveredTooltip()
    {
        if (!inventoryView.IsOpen || pauseMenuView.IsOpen || PointerWindow() is not { } position) return null;
        // A dragged row rides the pointer; a tooltip would cover the slot it is aimed at
        if (inventoryView.Drag is { Lifted: true }) return null;
        var world = ActiveWorld;
        if (world == null || renderManager == null) return null;
        var inventory = world.Inventory;

        // A listed row first (the rows sit over the list), then a hotbar slot
        if (EntryAt(position, inventory, world.Camera.IsCreative) is { } entry)
        {
            var stack = entry.ToolInstance != 0 ? inventory.GetToolStack(entry.ToolInstance) : null;
            return ItemTooltip.Hover(position, entry.Id, stack, inventory.Equipment);
        }

        // The Gear tab's equipment card: a worn piece's row names it, like a listed row does
        if (inventoryView.Tab == InventoryCategory.Gear
            && EquipmentCardLayout.SlotAt(renderManager.Ui.ToCanvas(position), renderManager.Ui.ScreenSize) is { } wornSlot
            && inventory.Equipment.StackAt(wornSlot) is { } worn)
            return ItemTooltip.Hover(position, worn.DefinitionId, worn, inventory.Equipment);

        if (renderManager.Ui.Hud.HotbarSlotAt(position) is not { } slot || slot < 0 || inventory.IdAt(slot) == 0)
            return null;
        var tool = inventory.ToolInstanceAt(slot);
        var slotStack = tool != 0 ? inventory.GetToolStack(tool) : null;
        return ItemTooltip.Hover(position, inventory.IdAt(slot), slotStack, inventory.Equipment);
    }

    /// <summary>What a listed row names: its item id, the tool or gear instance it stands for (else 0), and the
    /// recipe it opens (a recipe row or hit, else null).</summary>
    internal readonly record struct RowEntry(byte Id, int ToolInstance, CraftingRecipe? Recipe);

    /// <summary>
    /// The listed row under a window-pixel point and what it names, read from the list the panel shows this frame
    /// (a search hit, a recipe, a creative catalog row or one of the player's own rows); null off the rows. The one
    /// reading of "which row is that" the tooltip, the number keys, the right-click and a row drag share, so they never
    /// disagree.
    /// </summary>
    internal RowEntry? EntryAt(Vector2 window, Inventory inventory, bool creative)
    {
        var list = CurrentList(inventory, creative);
        if (renderManager.Ui.Panel.PanelRowAt(window, list.Offset, list.RowCount) is not { } row) return null;
        if (list.Hits != null)
        {
            if (row >= list.Hits.Count) return null;
            var hit = list.Hits[row];
            return new RowEntry(hit.Id, hit.ToolInstance, hit.Recipe);
        }
        if (inventoryView.Tab == InventoryCategory.Crafting)
        {
            var recipes = inventory.ListedRecipes(creative);
            return row < recipes.Count ? new RowEntry(recipes[row].ResultId, 0, recipes[row]) : null;
        }
        if (creative)
        {
            var entries = CreativeCatalog.Entries(inventoryView.Tab, inventory);
            return row < entries.Count ? new RowEntry(entries[row].Id, 0, null) : null;
        }
        if (inventoryView.Tab is InventoryCategory.Tools or InventoryCategory.Gear)
        {
            var tools = inventoryView.Tab == InventoryCategory.Gear ? inventory.GearEntries() : inventory.ToolEntries();
            return row < tools.Count ? new RowEntry(tools[row].ItemId, tools[row].InstanceId, null) : null;
        }
        var stacks = inventory.StackEntries(inventoryView.Tab);
        return row < stacks.Count ? new RowEntry(stacks[row].Id, 0, null) : null;
    }

    /// <summary>
    /// The number keys while the [E] panel is open: the row under the cursor goes into that hotbar
    /// slot, the same equip a click on the slot performs with the row selected. Only the plain
    /// tabs — a search result and a creative catalog row have their own click actions.
    /// </summary>
    internal void AssignHoveredRowToHotbar()
    {
        var world = ActiveWorld;
        if (world == null || PointerWindow() is not { } pointer) return;
        if (!inventoryView.Search.IsEmpty || inventoryView.Tab == InventoryCategory.Crafting || world.Camera.IsCreative)
            return;
        var inventory = world.Inventory;
        if (EntryAt(pointer, inventory, creative: false) is not { } entry) return;
        var id = entry.Id;
        var tool = entry.ToolInstance;
        // Worn gear is not held in hand, so a number key never puts it on the hotbar
        if (tool != 0 && inventory.GetToolStack(tool)?.Item is GearItem) return;

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
        var opening = open && !inventoryView.IsOpen;
        inventoryView.IsOpen = open;
        if (!open)
        {
            Chest.Close();
            inventoryView.ClearSelection();
            inventoryView.ClearInput();
            inventoryView.Craft.Clear();
            inventoryView.EndThumbDrag();
            inventoryView.Drag = null;
        }
        ActiveWorld.Camera.SuppressLook = open;
        inputManager.Mouse?.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
        // The released cursor would reappear wherever it was last, maybe over a list row: start it mid-screen
        if (opening && inputManager.Mouse is { } mouse)
            mouse.Position = UiCanvas.CanvasToWindow(renderManager.Ui.ScreenSize / 2f, renderManager.Ui.Scale);
    }

    /// <summary>Number of list rows the given panel tab shows this frame. In creative the
    /// Blocks/Tools/Misc tabs list the whole catalog; the Craft tab lists what
    /// <see cref="Inventory.ListedRecipes"/> does.</summary>
    internal int RowCountFor(InventoryCategory tab)
    {
        if (tab == InventoryCategory.Crafting)
            return ActiveWorld!.Inventory.ListedRecipes(ActiveWorld.Camera.IsCreative).Count;
        if (ActiveWorld?.Camera.IsCreative == true)
            return CreativeCatalog.Entries(tab, ActiveWorld.Inventory).Count;
        return tab switch
        {
            InventoryCategory.Tools => ActiveWorld!.Inventory.ToolEntries().Count,
            InventoryCategory.Gear => ActiveWorld!.Inventory.GearEntries().Count,
            _ => ActiveWorld!.Inventory.StackEntries(tab).Count,
        };
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
            ? inventoryView.SearchHits(ActiveWorld!.Inventory, ActiveWorld.Camera.IsCreative).Count
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
        if (inventoryView.Focus == InventoryFocus.ChestSearch)
        {
            Chest.Type(c);
            return;
        }
        if (inventoryView.Focus != InventoryFocus.Search) return;
        if (!inventoryView.Search.Text.TryAppend(c)) return;
        inventoryView.Search.Highlight = -1;
        inventoryView.Search.ScrollOffset = 0f;
    }

    /// <summary>The non-character keys a focused inventory field reacts to. ESC is left to the
    /// polled panel toggle, so one press never does two things.</summary>
    internal void HandleInventoryKey(Key key)
    {
        // Caret, selection, word and clipboard keys go to the focused field; Up and Down stay the
        // suggestion highlight's
        if (inventoryView.Focus == InventoryFocus.ChestSearch)
        {
            if (key is not (Key.Up or Key.Down)) Chest.HandleKey(key);
        }
        else if (inventoryView.Focus == InventoryFocus.Search
                 && textKeys.TryHandle(inventoryView.Search.Text, key, upDownJump: false, out var changed))
        {
            if (changed)
            {
                inventoryView.Search.Highlight = -1;
                inventoryView.Search.ScrollOffset = 0f;
            }
            return;
        }
        switch (key)
        {
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
        var count = inventoryView.SearchHits(ActiveWorld!.Inventory, ActiveWorld.Camera.IsCreative).Count;
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
        var hits = inventoryView.SearchHits(ActiveWorld!.Inventory, ActiveWorld.Camera.IsCreative);
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
        if (hit.Recipe is { } recipe)
        {
            OpenRecipe(recipe);
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

    // Opens a recipe on the Craft tab, at the top of its card; the query that found it, if any, is cleared
    private void OpenRecipe(CraftingRecipe recipe)
    {
        inventoryView.Tab = InventoryCategory.Crafting;
        inventoryView.Craft.Select(recipe);
        inventoryView.CraftScrollOffset = 0f;
        inventoryView.Search.Clear();
        inventoryView.Focus = InventoryFocus.None;
    }

    /// <summary>Grants a catalog id to the player and picks it; the catalog's +1/+10/+100 buttons and the row body go
    /// through here. The grant never lands on the hotbar by itself: a click on a slot (or a drop) puts it there.</summary>
    internal void GrantFromCreative(byte id, int amount)
    {
        var inventory = ActiveWorld!.Inventory;
        if (!inventory.Add(id, amount, autoEquip: false)) return;
        // Applied here at once; on a server only a player it lets be creative keeps it
        session.SendInventoryIntent(InventoryIntent.CreativeGrant(id, amount));
        var instance = RowEquip.FirstInstanceOf(inventory, id);
        inventoryView.SelectedToolInstance = instance;
        inventoryView.SelectedId = instance != 0 ? (byte)0 : id;
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

    /// <summary>Whether a window-pixel point is on one of a creative catalog row's grant buttons (its own click).</summary>
    internal bool IsOnCatalogButton(Vector2 window, Inventory inventory)
    {
        var list = CurrentList(inventory, creative: true);
        return renderManager.Ui.Panel.PanelRowAt(window, list.Offset, list.RowCount) is { } row
               && InventoryPanelLayout.CatalogButtonAt(renderManager.Ui.ToCanvas(window), row, list.Offset, list.RowCount,
                   renderManager.Ui.ScreenSize) != null;
    }

    internal void SetListScroll(float offset, bool searching)
    {
        if (searching) inventoryView.Search.ScrollOffset = offset;
        else inventoryView.ScrollOffset = offset;
    }

    /// <summary>The list the panel shows this frame: the search hits (null when not searching), their or the
    /// tab's row count and the clamped scroll offset.</summary>
    private readonly record struct ListState(IReadOnlyList<SearchHit>? Hits, int RowCount, float Offset);

    private ListState CurrentList(Inventory inventory, bool creative)
    {
        var searching = !inventoryView.Search.IsEmpty;
        var hits = searching ? inventoryView.SearchHits(inventory, creative) : null;
        var rowCount = hits?.Count ?? RowCountFor(inventoryView.Tab);
        var offset = InventoryPanelLayout.ClampedOffset(
            searching ? inventoryView.Search.ScrollOffset : inventoryView.ScrollOffset,
            rowCount, renderManager.Ui.ScreenSize);
        return new ListState(hits, rowCount, offset);
    }

    /// <summary>Switches the panel to <paramref name="tab"/> as a click on it does: the search clears and the
    /// list pick drops.</summary>
    internal void SelectTab(InventoryCategory tab)
    {
        inventoryView.Tab = tab;
        inventoryView.Search.Clear();
        inventoryView.ClearSelection();
    }

    /// <summary>Types <paramref name="text"/> into the search field as the keyboard would (focus first).</summary>
    internal void TypeSearch(string text)
    {
        inventoryView.Focus = InventoryFocus.Search;
        foreach (var c in text)
            HandleInventoryType(c);
    }

    /// <summary>Clicks the listed row <paramref name="row"/> (0-based) of the plain list or search results, as a
    /// click on it would. The creative catalog rows are buttons at a click position, so they are not reachable
    /// this way.</summary>
    internal void PickRow(int row)
    {
        var world = ActiveWorld ?? throw new InvalidOperationException("No world to pick a row in.");
        var creative = world.Camera.IsCreative;
        var list = CurrentList(world.Inventory, creative);
        if (creative && list.Hits == null && inventoryView.Tab != InventoryCategory.Crafting)
            throw new InvalidOperationException("Picking a row needs a survival player (creative rows are buttons).");
        if (row < 0 || row >= list.RowCount)
            throw new InvalidOperationException($"Row {row + 1} is not listed ({list.RowCount} rows).");
        inventoryView.Focus = InventoryFocus.None;
        ApplyRowClick(row, list, world.Inventory, creative, Vector2.Zero);
    }

    // What a click on a listed row does, by what the list holds: a search hit acts (in creative an item hit is a
    // catalog row, like the tab's), a creative row is a button at the click, and a plain row picks its item, tool
    // instance or recipe. A left click never equips a row; that is the right-click's (HandleInventoryRightClick).
    private void ApplyRowClick(int row, ListState list, Inventory inventory, bool creative, Vector2 position)
    {
        if (TryDepositRow(row, list, inventory, creative)) return;
        if (list.Hits != null)
        {
            if (row >= list.Hits.Count) return;
            var hit = list.Hits[row];
            if (creative && !hit.IsRecipe)
                ApplyCatalogButton(hit.Id, row, list.Offset, list.RowCount, position);
            else
                ApplySearchHit(hit);
        }
        else if (creative && inventoryView.Tab != InventoryCategory.Crafting)
        {
            var entries = CreativeCatalog.Entries(inventoryView.Tab, inventory);
            if (row < entries.Count)
                ApplyCatalogButton(entries[row].Id, row, list.Offset, list.RowCount, position);
        }
        else switch (inventoryView.Tab)
        {
            case InventoryCategory.Tools:
                inventoryView.SelectedToolInstance = inventory.ToolEntries()[row].InstanceId;
                inventoryView.SelectedId = 0;
                break;
            case InventoryCategory.Gear:
                // A click picks the piece (its fitting slots light up), however often; a click on a lit slot or a
                // right-click on the row wears it
                inventoryView.SelectedToolInstance = inventory.GearEntries()[row].InstanceId;
                inventoryView.SelectedId = 0;
                break;
            case InventoryCategory.Crafting:
                OpenRecipe(inventory.ListedRecipes(creative)[row]);
                break;
            default:
            {
                var picked = inventory.StackEntries(inventoryView.Tab)[row].Id;
                inventoryView.SelectedId = picked;
                inventoryView.SelectedToolInstance = 0;
                if (inventoryView.Craft.Recipe != null)
                    inventoryView.Craft.FeedFromInventory(picked, inventory);
                break;
            }
        }
    }

    // With a chest open, a click on the inventory list moves what the row holds into the chest instead of picking it:
    // one unit of a stackable (all of it with shift), or the whole tool or piece of gear. Recipes and the creative
    // catalog keep their own clicks.
    private bool TryDepositRow(int row, ListState list, Inventory inventory, bool creative)
    {
        if (!Chest.IsOpen || creative) return false;
        if (list.Hits != null)
        {
            if (row >= list.Hits.Count || list.Hits[row].IsRecipe) return false;
            var hit = list.Hits[row];
            if (hit.ToolInstance > 0) Chest.DepositInstance(hit.ToolInstance);
            else if (inventory.CountFor(hit.Id) > 0 && !ChestTransfer.IsInstance(hit.Id)) Chest.DepositStack(hit.Id);
            return true;
        }
        switch (inventoryView.Tab)
        {
            case InventoryCategory.Crafting:
                return false;
            case InventoryCategory.Tools:
                if (row < inventory.ToolEntries().Count) Chest.DepositInstance(inventory.ToolEntries()[row].InstanceId);
                return true;
            case InventoryCategory.Gear:
                if (row < inventory.GearEntries().Count) Chest.DepositInstance(inventory.GearEntries()[row].InstanceId);
                return true;
            default:
            {
                var stacks = inventory.StackEntries(inventoryView.Tab);
                if (row < stacks.Count) Chest.DepositStack(stacks[row].Id);
                return true;
            }
        }
    }

    // A click on the equipment card: the picked gear goes into the slot when it fits there, otherwise a worn
    // piece comes off.
    private void ClickEquipmentSlot(Inventory inventory, int slot)
    {
        var picked = inventoryView.SelectedToolInstance;
        if (picked != 0 && inventory.Equipment.TryEquip(slot, picked))
        {
            session.SendInventoryIntent(InventoryIntent.EquipGear(slot, picked));
            return;
        }
        if (inventory.Equipment.Unequip(slot))
            session.SendInventoryIntent(InventoryIntent.UnequipGear(slot));
    }

    /// <summary>
    /// Routes one right-click inside the open panel: the listed row under it is equipped in one click
    /// (<see cref="RowEquip"/>: gear is worn or taken off, a tool or a stackable goes into the selected hotbar
    /// slot), where a left click only picks it; a recipe row or hit opens on the Craft tab, as a left click does.
    /// Applied here at once and, while connected, sent as the intent the server replays against its own copy.
    /// </summary>
    internal void HandleInventoryRightClick(Vector2 position)
    {
        if (ActiveWorld is not { } world) return;
        var inventory = world.Inventory;
        if (renderManager.Ui.Hud.HotbarSlotAt(position) is { } hotbar)
        {
            UnbindHotbarSlot(inventory, hotbar);
            return;
        }
        if (EntryAt(position, inventory, world.Camera.IsCreative) is not { } entry) return;
        if (entry.Recipe is { } recipe)
        {
            OpenRecipe(recipe);
            return;
        }
        if (RowEquip.Apply(inventory, entry.Id, entry.ToolInstance) is { } intent)
            session.SendInventoryIntent(intent);
    }

    // Equipping what a slot already shows clears it, so the unbind reuses the equip intent the server replays.
    private void UnbindHotbarSlot(Inventory inventory, int slot)
    {
        var held = inventory.SlotAt(slot);
        if (held.IsTool)
        {
            inventory.EquipTool(slot, held.ToolInstance);
            session.SendInventoryIntent(InventoryIntent.EquipTool(slot, held.ToolInstance));
            return;
        }
        if (held.Id == 0) return;
        inventory.EquipStackable(slot, held.Id);
        session.SendInventoryIntent(InventoryIntent.EquipStackable(slot, held.Id));
    }

    /// <summary>
    /// Routes one left-click inside the open panel: the field boxes focus, the scope button cycles
    /// what the search lists, tabs switch, rows pick for preview (or act on a search result),
    /// crafting controls feed the open recipe, and hotbar slots equip the pick (clicking what the
    /// slot already shows clears it).
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
        // The scope button beside it keeps the field's focus, so typing carries on after a cycle
        if (renderManager.Ui.Panel.ScopeButtonAt(position))
        {
            inventoryView.CycleSearchScope();
            return;
        }
        inventoryView.Focus = InventoryFocus.None;

        // The chest card (beside the list, while a chest is open) takes its own clicks
        if (Chest.HandleClick(position)) return;

        var tab = renderManager.Ui.Panel.PanelTabAt(position);
        if (tab.HasValue)
        {
            SelectTab(tab.Value);
            return;
        }

        var list = CurrentList(inventory, creative);
        // The scrollbar is handled on press; its release must not fall through to the
        // crafting card or hotbar tests below.
        if (InventoryPanelLayout.ScrollbarHitAt(renderManager.Ui.ToCanvas(position),
                renderManager.Ui.ScreenSize, list.Offset, list.RowCount) != ScrollbarHit.None)
            return;
        var row = renderManager.Ui.Panel.PanelRowAt(position, list.Offset, list.RowCount);
        if (row.HasValue)
        {
            ApplyRowClick(row.Value, list, inventory, creative, position);
            return;
        }

        // An open chest's card covers the equipment and crafting cards, so neither takes a click under it
        if (inventoryView.Tab == InventoryCategory.Gear && !Chest.IsOpen
            && EquipmentCardLayout.SlotAt(renderManager.Ui.ToCanvas(position), renderManager.Ui.ScreenSize) is { } slot)
        {
            ClickEquipmentSlot(inventory, slot);
            return;
        }

        if (inventoryView.Tab == InventoryCategory.Crafting && !Chest.IsOpen)
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
        var picked = inventoryView.SelectedToolInstance;
        if (picked != 0)
        {
            // Worn gear is never held, so a picked piece leaves the slot alone
            if (inventory.GetToolStack(picked)?.Item is null or GearItem) return;
            inventory.EquipTool(hotbar.Value, picked);
            session.SendInventoryIntent(InventoryIntent.EquipTool(hotbar.Value, picked));
        }
        else if (inventoryView.SelectedId != 0)
        {
            inventory.EquipStackable(hotbar.Value, inventoryView.SelectedId);
            session.SendInventoryIntent(InventoryIntent.EquipStackable(hotbar.Value, inventoryView.SelectedId));
        }
    }
}
