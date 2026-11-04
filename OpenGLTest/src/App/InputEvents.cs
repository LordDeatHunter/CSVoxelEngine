using Silk.NET.Input;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.UI;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Menus;

namespace VoxelEngine.App;

/// <summary>The window's mouse and keyboard events, routed to whatever owns them on the current screen: the main menu, the pause and settings menus, the [E] panel, the chat, or the right-click repeat timer. Program registers them once in OnLoad; Silk.NET raises them on the main thread.</summary>
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
    private readonly MenuInput menuInput;
    private readonly ChatInput chatInput;
    private readonly GameplayInput gameplay;

    internal InputEvents(InputManager inputManager, RenderManager renderManager, MainMenuController menu, InventoryView inventoryView, ChatView chatView, PauseMenuView pauseMenuView, SettingsMenuView settingsMenuView, InventoryPanelInput panelInput, MenuInput menuInput, ChatInput chatInput, GameplayInput gameplay)
    {
        this.inputManager = inputManager;
        this.renderManager = renderManager;
        this.menu = menu;
        this.inventoryView = inventoryView;
        this.chatView = chatView;
        this.pauseMenuView = pauseMenuView;
        this.settingsMenuView = settingsMenuView;
        this.panelInput = panelInput;
        this.menuInput = menuInput;
        this.chatInput = chatInput;
        this.gameplay = gameplay;
    }

    private AppScreen Screen => Program.Screen;

    private World? ActiveWorld => Program.ActiveWorld;

    /// <summary>A released button: menu and pause clicks, slider-drag saves, the right-click repeat reset and [E] panel clicks.</summary>
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
                panelInput.HandleInventoryClick(inputManager.Mouse.Position);
                break;
            }
        }
    }

    /// <summary>A pressed button: the settings sliders start a drag and the [E] panel scrollbar a thumb drag.</summary>
    internal void OnMouseDown(MouseButton button)
    {
        switch (Screen)
        {
            case AppScreen.Loading or AppScreen.Saving:
                return;
            case AppScreen.Playing when inventoryView.IsOpen && !pauseMenuView.IsOpen
                                                             && button == MouseButton.Left && inputManager.Mouse != null:
                panelInput.HandleInventoryScrollbarPress(inputManager.Mouse.Position);
                return;
        }

        if (!menuInput.SettingsPageOpen || button != MouseButton.Left || inputManager.Mouse == null)
            return;
        var hit = renderManager.Ui.Menu.SettingsHitAt(inputManager.Mouse.Position, settingsMenuView);
        if (hit.Kind != SettingsHitKind.Slider || SettingsRows.All[hit.Row] is not SliderRow slider) return;
        settingsMenuView.DragRow = hit.Row;
        slider.Set(SettingsLayout.SliderValue(slider, hit.Fraction));
    }

    /// <summary>The wheel: the main menu lists, the hotbar and the [E] panel list or craft card.</summary>
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
        switch (inventoryView.IsOpen)
        {
            case false when !pauseMenuView.IsOpen:
                w.Inventory.Scroll(wheel.Y);
                // While connected the server owns the selection: mirror the step as an intent, or
                // the next inventory sync would put the slot straight back.
                gameplay.MirrorSelection(w.Inventory.SelectedIndex);
                break;
            case true when !pauseMenuView.IsOpen && inputManager.Mouse != null:
            {
                // The wheel scrolls the list only while hovering it; wheel.Y > 0 is toward the top.
                var canvas = renderManager.Ui.ScreenSize;
                var p = renderManager.Ui.ToCanvas(inputManager.Mouse.Position);
                if (InventoryPanelLayout.IsOverList(p, canvas))
                {
                    var searching = !inventoryView.Search.IsEmpty;
                    var rowCount = searching
                        ? InventorySearch.Search(inventoryView.Search.Query, w.Inventory, w.Camera.IsCreative).Count
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
            else if (inventoryView.IsOpen && !pauseMenuView.IsOpen)
                panelInput.HandleInventoryKey(key);
            else if (key is Key.Enter or Key.KeypadEnter && ActiveWorld is { } world)
                gameplay.RequestRespawn(world);
            return;
        }
        if (Screen != AppScreen.Menu) return;
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
