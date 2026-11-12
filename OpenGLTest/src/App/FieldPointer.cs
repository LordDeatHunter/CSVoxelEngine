using System;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.UI.Chest;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Menus;
using VoxelEngine.UI.Waystone;

namespace VoxelEngine.App;

/// <summary>
/// The mouse's side of text fields: a left press on a field focuses it and puts the caret under
/// the pointer (Shift extends the selection), and holding the button drags the selection; a right press empties the field. Covers
/// the main-menu fields, the chat line (the cursor is free while the chat is open), the [E] search field, the chest search
/// and the waystone screen's rename and search fields. The click-up focus rules stay where they were.
/// Owned by Program; main thread only.
/// </summary>
internal sealed class FieldPointer(InputManager inputManager, RenderManager renderManager, MainMenuController menu,
    ChatView chatView, InventoryView inventoryView, PauseMenuView pauseMenuView, WaystoneView waystoneView,
    TextFieldKeys textKeys)
{
    private TextFieldState? _field;
    private Vector2 _center;
    private Vector2 _size;
    private bool _clear;

    private World? ActiveWorld => Program.ActiveWorld;

    /// <summary>A pressed left button starts a caret placement and drag when it landed on a field; a pressed right
    /// button (<paramref name="clear"/>) empties the field instead.</summary>
    internal void Press(bool clear = false)
    {
        _field = null;
        _clear = clear;
        if (inputManager.Mouse is not { } mouse) return;
        var position = mouse.Position;
        var ui = renderManager.Ui;
        switch (Program.Screen)
        {
            case AppScreen.Menu:
            {
                var hit = ui.Menu.MainMenuHitAt(position, menu.View);
                if (menu.FocusFieldAt(hit) is not { } menuField) return;
                Begin(menuField, MainMenuLayout.FieldCenter(menu.View.Page, hit.Kind, ui.ScreenSize),
                    MainMenuLayout.FieldSize, position);
                return;
            }
            case AppScreen.Playing when chatView.IsOpen && !pauseMenuView.IsOpen:
            {
                var box = ChatLayout.InputRect(ui.ScreenSize);
                if (!box.Contains(ui.ToCanvas(position))) return;
                Begin(chatView.Input, box.Center, box.Size, position);
                return;
            }
            case AppScreen.Playing when waystoneView.IsOpen && !pauseMenuView.IsOpen:
                PressWaystone(ui.ToCanvas(position), ui.ScreenSize, position);
                return;
            case AppScreen.Playing when inventoryView.IsOpen && !pauseMenuView.IsOpen && ActiveWorld is { } world:
            {
                var screen = ui.ScreenSize;
                var point = ui.ToCanvas(position);
                var chestBox = ChestCardLayout.SearchBox(screen);
                if (world.ChestPanel.IsOpen && chestBox.Contains(point))
                {
                    inventoryView.Focus = InventoryFocus.ChestSearch;
                    Begin(world.ChestPanel.Search.Text, chestBox.Center, chestBox.Size, position);
                    return;
                }
                if (!InventoryPanelLayout.SearchBoxAt(point, screen)) return;
                var (center, size) = InventoryPanelLayout.SearchBoxRect(screen);
                inventoryView.Focus = InventoryFocus.Search;
                Begin(inventoryView.Search.Text, center, size, position, inventoryView.Search.Clear);
                return;
            }
        }
    }

    // The waystone screen's two fields: the rename dialog's name and the list's search
    private void PressWaystone(Vector2 point, Vector2 screen, Vector2 position)
    {
        var field = WaystoneLayout.RenameField(screen);
        if (waystoneView.Page == WaystonePage.Rename && field.Contains(point))
        {
            Begin(waystoneView.RenameField, field.Center, field.Size, position);
            return;
        }
        var box = WaystoneLayout.SearchBox(screen);
        if (waystoneView.Page != WaystonePage.List || !box.Contains(point)) return;
        waystoneView.SearchFocused = true;
        Begin(waystoneView.Search.Text, box.Center, box.Size, position);
    }

    /// <summary>Every frame: while the button stays down after a press on a field, the selection follows the pointer.</summary>
    internal void Update()
    {
        if (_field == null) return;
        if (inputManager.Mouse is not { } mouse || !mouse.IsButtonPressed(MouseButton.Left))
        {
            _field = null;
            return;
        }
        MoveCaret(mouse.Position, extend: true);
    }

    private void Begin(TextFieldState field, Vector2 center, Vector2 size, Vector2 windowPosition, Action? clear = null)
    {
        if (_clear)
        {
            (clear ?? field.Clear)();
            return;
        }
        _field = field;
        _center = center;
        _size = size;
        MoveCaret(windowPosition, textKeys.ShiftHeld);
    }

    private void MoveCaret(Vector2 windowPosition, bool extend)
    {
        var ui = renderManager.Ui;
        _field!.SetCaret(ui.CaretIndexAt(_center, _size, _field, ui.ToCanvas(windowPosition).X), extend);
    }
}
