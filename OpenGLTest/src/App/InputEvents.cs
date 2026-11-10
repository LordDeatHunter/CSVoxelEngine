using System;
using Silk.NET.Input;
using VoxelEngine.Magic;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.UI;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Menus;

namespace VoxelEngine.App;

/// <summary>The window's mouse and keyboard events, routed to whatever owns them on the current screen: the main menu, the pause and settings menus, the [E] panel, the spell builder, the waystone screen, the chat, or the right-click repeat timer. Program registers them once in OnLoad; Silk.NET raises them on the main thread.</summary>
internal sealed class InputEvents
{
    private readonly InputManager inputManager;
    private readonly RenderManager renderManager;
    private readonly MainMenuController menu;
    private readonly InventoryView inventoryView;
    private readonly ChatView chatView;
    private readonly PauseMenuView pauseMenuView;
    private readonly SettingsMenuView settingsMenuView;
    private readonly InventoryPanelInput panelInput;
    private readonly SpellBuilderInput builderInput;
    private readonly WaystoneInput waystoneInput;
    private readonly MenuInput menuInput;
    private readonly ChatInput chatInput;
    private readonly GameplayInput gameplay;

    internal InputEvents(InputManager inputManager, RenderManager renderManager, MainMenuController menu, InventoryView inventoryView, ChatView chatView, PauseMenuView pauseMenuView, SettingsMenuView settingsMenuView, InventoryPanelInput panelInput, SpellBuilderInput builderInput, WaystoneInput waystoneInput, MenuInput menuInput, ChatInput chatInput, GameplayInput gameplay, FieldPointer fieldPointer)
    {
        this.fieldPointer = fieldPointer;
        this.inputManager = inputManager;
        this.renderManager = renderManager;
        this.menu = menu;
        this.inventoryView = inventoryView;
        this.chatView = chatView;
        this.pauseMenuView = pauseMenuView;
        this.settingsMenuView = settingsMenuView;
        this.panelInput = panelInput;
        this.builderInput = builderInput;
        this.waystoneInput = waystoneInput;
        this.menuInput = menuInput;
        this.chatInput = chatInput;
        this.gameplay = gameplay;
        textKeys = new TextFieldKeys(inputManager);
    }

    private readonly TextFieldKeys textKeys;
    private readonly FieldPointer fieldPointer;

    private AppScreen Screen => Program.Screen;

    private World? ActiveWorld => Program.ActiveWorld;

    /// <summary>A released button: menu and pause clicks, slider-drag saves, the right-click repeat reset and [E] panel row equip, a click on a link in the open chat's log (it suggests the link's command), spell builder clicks and [E] panel clicks.</summary>
    internal void OnMouseUp(MouseButton button)
    {
        if (Screen == AppScreen.Menu)
        {
            if (button == MouseButton.Left && inputManager.Mouse != null)
            {
                var pos = inputManager.Mouse.Position;
                if (menu.View.Page == MainMenuPage.Settings)
                    menuInput.HandleSettingsClick(pos);
                else
                    Program.HandleMenuAction(menu.Click(renderManager.Ui.Menu.MainMenuHitAt(pos, menu.View)));
            }
            if (settingsMenuView.DragRow.HasValue)
                GameSettings.Save();
            settingsMenuView.DragRow = null;
            return;
        }
        if (Screen != AppScreen.Playing) return;

        // The death screen's RESPAWN button; every other click is dead weight while it is up
        if (gameplay.DeathScreenUp(ActiveWorld!))
        {
            if (button == MouseButton.Left && inputManager.Mouse != null
                && renderManager.Ui.Menu.DeathScreenHitsRespawn(inputManager.Mouse.Position))
                gameplay.RequestRespawn(ActiveWorld!);
            return;
        }

        if (pauseMenuView.IsOpen)
        {
            if (button == MouseButton.Left && inputManager.Mouse != null)
            {
                if (pauseMenuView.Page == PauseMenuPage.Settings)
                    menuInput.HandleSettingsClick(inputManager.Mouse.Position);
                else
                    menuInput.HandleMenuClick(inputManager.Mouse.Position);
            }
            if (settingsMenuView.DragRow.HasValue)
                GameSettings.Save();
            settingsMenuView.DragRow = null;
            return;
        }
        switch (button)
        {
            case MouseButton.Right:
                gameplay.ResetPlaceTimer();
                // A right-click on an [E] panel row equips it; the world's use and place stand aside while it is open
                if (inventoryView.IsOpen && !chatView.IsOpen && !waystoneInput.IsOpen && !builderInput.IsOpen
                    && inputManager.Mouse != null)
                    panelInput.HandleInventoryRightClick(inputManager.Mouse.Position);
                break;
            // Only a drawn link is clickable, and the click never reaches the world: mining, use and the middle
            // pick already stand aside while the chat is open
            case MouseButton.Left when chatView.IsOpen && gameplay.HudVisible && inputManager.Mouse != null:
                if (renderManager.Ui.Chat.LinkCommandAt(inputManager.Mouse.Position, chatView) is { } command)
                    chatView.Suggest(command);
                break;
            case MouseButton.Left when waystoneInput.IsOpen && inputManager.Mouse != null:
                waystoneInput.HandleClick(inputManager.Mouse.Position);
                break;
            case MouseButton.Left when gameplay.Forge.IsOpen && inputManager.Mouse != null:
                gameplay.Forge.HandleClick(inputManager.Mouse.Position);
                break;
            case MouseButton.Left when builderInput.IsOpen && inputManager.Mouse != null:
                builderInput.HandleClick(inputManager.Mouse.Position);
                break;
            case MouseButton.Left when inventoryView.IsOpen && inputManager.Mouse != null:
            {
                // Releasing a thumb drag ends it here and nowhere else while the panel is
                // open, so the release never equips or picks whatever is under the cursor.
                if (inventoryView.ThumbDragGrab.HasValue)
                {
                    inventoryView.EndThumbDrag();
                    return;
                }
                // ...and a row dragged off its press drops into the slot under it instead of clicking
                if (panelInput.RowDrag.TryDrop(inputManager.Mouse.Position)) return;
                panelInput.HandleInventoryClick(inputManager.Mouse.Position);
                break;
            }
        }
    }

    /// <summary>A pressed button: the settings sliders start a drag, the [E] panel scrollbar a thumb drag and a listed
    /// row a row drag.</summary>
    internal void OnMouseDown(MouseButton button)
    {
        switch (Screen)
        {
            case AppScreen.Loading or AppScreen.Saving:
                return;
        }
        if (button == MouseButton.Left) fieldPointer.Press();
        switch (Screen)
        {
            case AppScreen.Playing when inventoryView.IsOpen && !pauseMenuView.IsOpen
                                                             && button == MouseButton.Left && inputManager.Mouse != null:
                panelInput.HandleInventoryScrollbarPress(inputManager.Mouse.Position);
                panelInput.RowDrag.Press(inputManager.Mouse.Position);
                return;
        }

        if (!menuInput.SettingsPageOpen || button != MouseButton.Left || inputManager.Mouse == null)
            return;
        var hit = renderManager.Ui.Menu.SettingsHitAt(inputManager.Mouse.Position, settingsMenuView);
        if (hit.Kind != SettingsHitKind.Slider || SettingsRows.All[hit.Row] is not SliderRow slider) return;
        settingsMenuView.DragRow = hit.Row;
        slider.Set(SettingsLayout.SliderValue(slider, hit.Fraction));
    }

    /// <summary>The wheel: the main menu lists, the hotbar (or, with Left Ctrl held, a held staff's spell slot) and the [E] panel list or craft card; nothing while the spell builder is open.</summary>
    internal void OnScroll(ScrollWheel wheel)
    {
        if (Screen == AppScreen.Menu)
        {
            // wheel.Y > 0 is toward the top, i.e. a smaller offset
            if (inputManager.Mouse != null && menu.View.Page == MainMenuPage.Worlds
                && renderManager.Ui.Menu.IsOverWorldList(inputManager.Mouse.Position))
                menu.Scroll(-wheel.Y * MainMenuLayout.WheelStepRows, renderManager.Ui.ScreenSize);
            else if (inputManager.Mouse != null && menu.View.Page == MainMenuPage.Multiplayer
                && renderManager.Ui.Menu.IsOverServerList(inputManager.Mouse.Position))
                menu.Scroll(-wheel.Y * MainMenuLayout.WheelStepRows, renderManager.Ui.ScreenSize);
            return;
        }
        if (Screen != AppScreen.Playing) return;
        var w = ActiveWorld!;

        if (chatView.IsOpen || gameplay.DeathScreenUp(w)) return;
        // The open spell builder owns the mouse: the wheel never steps the hotbar or the staff slot behind it
        if (builderInput.IsOpen) return;
        // ...and so does the waystone screen, whose list takes the wheel over its panel
        if (inputManager.Mouse != null && waystoneInput.HandleScroll(inputManager.Mouse.Position, wheel.Y)) return;
        // ...and the anvil's recipe list, so the wheel never steps the hammer out of the hand behind it
        if (gameplay.Forge.IsOpen) return;
        switch (inventoryView.IsOpen)
        {
            case false when !pauseMenuView.IsOpen:
                // Left Ctrl + wheel with a staff in hand cycles its spell slot instead of the hotbar;
                // wheel up (positive) steps to the previous slot, as the hotbar scroll does
                if (inputManager.Keyboard?.IsKeyPressed(Key.ControlLeft) == true
                    && HeldStaff.TryGet(w.Inventory, out _, out _))
                {
                    if (wheel.Y != 0f) gameplay.RequestCycleStaff(-Math.Sign(wheel.Y));
                    break;
                }
                w.Inventory.Scroll(wheel.Y);
                // While connected the server owns the selection: mirror the step as an intent, or
                // the next inventory sync would put the slot straight back.
                gameplay.MirrorSelection(w.Inventory.SelectedIndex);
                break;
            case true when !pauseMenuView.IsOpen && inputManager.Mouse != null:
            {
                // The wheel scrolls the list only while hovering it; wheel.Y > 0 is toward the top.
                if (panelInput.Chest.HandleScroll(inputManager.Mouse.Position, wheel.Y)) break;
                var canvas = renderManager.Ui.ScreenSize;
                var p = renderManager.Ui.ToCanvas(inputManager.Mouse.Position);
                if (InventoryPanelLayout.IsOverList(p, canvas))
                {
                    var searching = !inventoryView.Search.IsEmpty;
                    var rowCount = searching
                        ? inventoryView.SearchHits(w.Inventory, w.Camera.IsCreative).Count
                        : panelInput.RowCountFor(inventoryView.Tab);
                    var delta = -wheel.Y * InventoryPanelLayout.WheelStepRows * InventoryPanelLayout.RowHeight;
                    panelInput.SetListScroll(InventoryPanelLayout.ClampedOffset(
                        (searching ? inventoryView.Search.ScrollOffset : inventoryView.ScrollOffset) + delta,
                        rowCount, canvas), searching);
                }
                else if (inventoryView.Tab == InventoryCategory.Crafting
                         && renderManager.Ui.CraftCard.IsOverCraftCard(inputManager.Mouse.Position))
                {
                    inventoryView.CraftScrollOffset = renderManager.Ui.CraftCard.ClampCraftScroll(
                        inventoryView.CraftScrollOffset - wheel.Y * CraftCardLayout.WheelStep);
                }

                break;
            }
        }
    }

    /// <summary>A typed character: the menu fields, the chat or the [E] search field.</summary>
    internal void OnKeyChar(char c)
    {
        switch (Screen)
        {
            case AppScreen.Menu:
                menu.Type(c);
                break;
            case AppScreen.Playing when chatView.IsOpen && !pauseMenuView.IsOpen:
                chatView.Type(c);
                break;
            case AppScreen.Playing when waystoneInput.IsOpen && !pauseMenuView.IsOpen:
                waystoneInput.Type(c);
                break;
            case AppScreen.Playing when inventoryView.IsOpen && !pauseMenuView.IsOpen:
                panelInput.HandleInventoryType(c);
                break;
        }
    }

    /// <summary>A pressed key: the chat and [E] panel keys while playing, the menu control keys on the main menu.</summary>
    internal void OnKeyDown(Key key)
    {
        if (Screen == AppScreen.Playing)
        {
            if (chatView.IsOpen && !pauseMenuView.IsOpen)
                chatInput.HandleChatKey(key);
            else if (waystoneInput.IsOpen && !pauseMenuView.IsOpen)
                waystoneInput.HandleKey(key);
            else if (inventoryView.IsOpen && !pauseMenuView.IsOpen)
                panelInput.HandleInventoryKey(key);
            else if (key is Key.Enter or Key.KeypadEnter && ActiveWorld is { } world)
                gameplay.RequestRespawn(world);
            return;
        }
        if (Screen != AppScreen.Menu) return;
        // Caret, selection, word and clipboard keys edit the focused field; Up and Down jump to its ends
        if (menu.View.FocusedField is { } field && textKeys.TryHandle(field, key, upDownJump: true, out _))
            return;
        var menuKey = key switch
        {
            Key.Backspace => MenuKey.Backspace,
            Key.Tab => MenuKey.Tab,
            Key.Enter or Key.KeypadEnter => MenuKey.Enter,
            Key.Escape => MenuKey.Escape,
            _ => (MenuKey?)null,
        };
        switch (menuKey)
        {
            case null:
                return;
            // ESC leaves the settings page; a live slider drag is saved like a release would
            case MenuKey.Escape when menu.View.Page == MainMenuPage.Settings:
            {
                if (settingsMenuView.DragRow.HasValue)
                    GameSettings.Save();
                settingsMenuView.DragRow = null;
                break;
            }
        }

        Program.HandleMenuAction(menu.Key(menuKey.Value));
    }
}
