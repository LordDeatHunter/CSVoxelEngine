using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.UI.Chest;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Menus;

namespace VoxelEngine.App;

/// <summary>
/// The mouse's side of text fields: a left press on a field focuses it and puts the caret under
/// the pointer (Shift extends the selection), and holding the button drags the selection. Covers
/// the main-menu fields, the [E] search field and the chest search; the chat line has no pointer
/// (the cursor stays captured while it is open). The click-up focus rules stay where they were.
/// Owned by Program; main thread only.
/// </summary>
internal sealed class FieldPointer(InputManager inputManager, RenderManager renderManager, MainMenuController menu,
    InventoryView inventoryView, PauseMenuView pauseMenuView, TextFieldKeys textKeys)
{
    private TextFieldState? _field;
    private Vector2 _center;
    private Vector2 _size;

    private World? ActiveWorld => Program.ActiveWorld;

    /// <summary>A pressed left button: starts a caret placement and drag when it landed on a field.</summary>
    internal void Press()
    {
        _field = null;
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
                Begin(inventoryView.Search.Text, center, size, position);
                return;
            }
        }
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

    private void Begin(TextFieldState field, Vector2 center, Vector2 size, Vector2 windowPosition)
    {
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
