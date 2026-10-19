using System;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.UI;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Menus;

namespace VoxelEngine.App;

/// <summary>The pause menu's and the shared settings page's input: opening and paging the pause menu, settings clicks and slider drags on either menu, and applying the window-level settings (VSync, size, frame cap) when they change. Owned by Program; main thread only.</summary>
internal sealed class MenuInput
{
    private readonly WindowManager windowManager;
    private readonly InputManager inputManager;
    private readonly RenderManager renderManager;
    private readonly MainMenuController menu;
    private readonly PauseMenuView pauseMenuView;
    private readonly SettingsMenuView settingsMenuView;
    private readonly InventoryPanelInput panelInput;
    private readonly InventoryView inventoryView;
    private bool appliedVSync;
    private int appliedResolution = -1;
    private int appliedMaxFps = -1;

    internal MenuInput(WindowManager windowManager, InputManager inputManager, RenderManager renderManager, MainMenuController menu, PauseMenuView pauseMenuView, SettingsMenuView settingsMenuView, InventoryPanelInput panelInput, InventoryView inventoryView)
    {
        this.windowManager = windowManager;
        this.inputManager = inputManager;
        this.renderManager = renderManager;
        this.menu = menu;
        this.pauseMenuView = pauseMenuView;
        this.settingsMenuView = settingsMenuView;
        this.panelInput = panelInput;
        this.inventoryView = inventoryView;
    }

    private AppScreen Screen => Program.Screen;
    private World? ActiveWorld => Program.ActiveWorld;

    /// <summary>
    /// The one test for "a settings page is up", shared by the drag start (MouseDown), the
    /// per-frame drag tracking (OnUpdate) and the release/save (MouseUp), so a slider drags and
    /// saves identically on the main menu and the pause menu.
    /// </summary>
    internal bool SettingsPageOpen =>
        (Screen == AppScreen.Menu && menu.View.Page == MainMenuPage.Settings)
        || (Screen == AppScreen.Playing && pauseMenuView.Page == PauseMenuPage.Settings);

    internal void SetPauseMenu(PauseMenuPage page)
    {
        // Only a playing world can be paused; while loading, SuppressLook belongs to the World
        if (ActiveWorld is not { IsPlaying: true }) return;
        if (page != PauseMenuPage.Closed && inventoryView.IsOpen)
            panelInput.SetInventoryOpen(false);
        pauseMenuView.Page = page;
        if (page == PauseMenuPage.Settings)
        {
            settingsMenuView.DragRow = null;
        }
        var open = page != PauseMenuPage.Closed;
        ActiveWorld.Camera.SuppressLook = open;
        inputManager.Mouse?.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
    }

    /// <summary>
    /// Applies the window-level settings when they changed (VSync, size, frame cap); everything
    /// else is read live by the engine. Cached so the window is only touched on change.
    /// </summary>
    internal void SyncWindowSettings()
    {
        if (appliedResolution < 0)
        {
            appliedVSync = windowManager.Window.VSync;
            appliedResolution = GameSettings.ResolutionIndex;
            appliedMaxFps = GameSettings.MaxFpsIndex;
        }
        if (GameSettings.VSync != appliedVSync)
        {
            appliedVSync = GameSettings.VSync;
            windowManager.Window.VSync = appliedVSync;
        }
        if (GameSettings.ResolutionIndex != appliedResolution)
        {
            appliedResolution = GameSettings.ResolutionIndex;
            var (w, h) = GameSettings.Resolutions[
                Math.Clamp(appliedResolution, 0, GameSettings.Resolutions.Length - 1)];
            windowManager.Window.Size = new Vector2D<int>(w, h);
        }

        if (GameSettings.MaxFpsIndex == appliedMaxFps) return;
        appliedMaxFps = GameSettings.MaxFpsIndex;
        var fps = GameSettings.MaxFpsOptions[
            Math.Clamp(appliedMaxFps, 0, GameSettings.MaxFpsOptions.Length - 1)];
        windowManager.Window.UpdatesPerSecond = fps;
        windowManager.Window.FramesPerSecond = fps;
    }

    /// <summary>
    /// A slider drag tracks the cursor every frame until release (which saves), on whichever
    /// menu's settings page is open (<see cref="SettingsPageOpen"/>).
    /// </summary>
    internal void TrackSettingsDrag()
    {
        if (!SettingsPageOpen || !settingsMenuView.DragRow.HasValue
            || SettingsRows.All[settingsMenuView.DragRow.Value] is not SliderRow drag
            || inputManager.Mouse == null)
            return;

        var tabRow = SettingsLayout.TabRowOf(settingsMenuView.Tab, settingsMenuView.DragRow.Value);
        if (tabRow < 0 || !inputManager.Mouse.IsButtonPressed(MouseButton.Left))
        {
            settingsMenuView.DragRow = null;
            GameSettings.Save();
        }
        else
        {
            var canvas = renderManager.Ui.ScreenSize;
            drag.Set(SettingsLayout.SliderValue(drag,
                SettingsLayout.SliderFraction(renderManager.Ui.ToCanvas(inputManager.Mouse.Position).X, tabRow, canvas)));
        }
    }

    internal void HandleSettingsClick(Vector2 position)
    {
        var hit = renderManager.Ui.Menu.SettingsHitAt(position, settingsMenuView);
        switch (hit.Kind)
        {
            case SettingsHitKind.Back:
                if (Screen == AppScreen.Menu)
                    menu.CloseSettings();
                else
                    SetPauseMenu(PauseMenuPage.Main);
                break;
            case SettingsHitKind.Tab:
                settingsMenuView.Tab = SettingsRows.Tabs[Math.Clamp(hit.Row, 0, SettingsRows.Tabs.Length - 1)];
                settingsMenuView.DragRow = null;
                break;
            case SettingsHitKind.Toggle when SettingsRows.All[hit.Row] is ToggleRow toggle:
                toggle.Set(!toggle.Get());
                GameSettings.Save();
                break;
            case SettingsHitKind.Slider when SettingsRows.All[hit.Row] is SliderRow slider:
                slider.Set(SettingsLayout.SliderValue(slider, hit.Fraction));
                GameSettings.Save();
                break;
            case SettingsHitKind.Options when SettingsRows.All[hit.Row] is OptionsRow options:
                options.Set((options.Get() + 1) % options.Options.Length);
                GameSettings.Save();
                break;
        }
    }

    internal void HandleMenuClick(Vector2 position)
    {
        switch (renderManager.Ui.Menu.PauseMenuHitAt(position, pauseMenuView))
        {
            case PauseMenuHit.Resume:
                SetPauseMenu(PauseMenuPage.Closed);
                break;
            case PauseMenuHit.Settings:
                SetPauseMenu(PauseMenuPage.Settings);
                break;
            case PauseMenuHit.Exit:
                // SAVE & QUIT: the saving frame first, the teardown on the next update
                Program.RequestSaveAndQuit();
                break;
            case PauseMenuHit.Back:
                SetPauseMenu(PauseMenuPage.Main);
                break;
        }
    }
}
