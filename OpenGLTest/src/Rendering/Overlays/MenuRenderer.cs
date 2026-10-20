using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Saves;
using VoxelEngine.UI;
using VoxelEngine.UI.Menus;
using static VoxelEngine.Rendering.Overlays.UIRenderer;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The menus of <see cref="UIRenderer"/>: the pause menu, the settings page both menus share and
/// every main-menu page, with their hit-tests over the same layouts the draws read. Draws through
/// the owner's primitives. Render thread only.
/// </summary>
internal sealed class MenuRenderer
{
    private readonly GL _gl;
    private readonly UIRenderer _ui;

    internal MenuRenderer(GL gl, UIRenderer ui)
    {
        _gl = gl;
        _ui = ui;
    }

    /// <summary>
    /// The ESC pause menu over the still-rendered game: a fullscreen dim standing in for a
    /// blur (this pass has no blur target; the dark translucency keeps the scene visible
    /// but unreadable, like the Godot reference's BlurOverlay in overlay mode), then a
    /// centered title with RESUME / SETTINGS / SAVE &amp; QUIT buttons, or the settings title with a
    /// BACK button. Click targets come from <see cref="PauseMenuLayout"/> so drawing and
    /// hit-testing can never drift apart (the third button is still <see cref="PauseMenuHit.Exit"/>).
    /// </summary>
    internal void DrawPauseMenu(PauseMenuView view, SettingsMenuView settings)
    {
        if (view.Page == PauseMenuPage.Closed) return;

        _ui.DrawRect(_ui.ScreenSize * 0.5f, _ui.ScreenSize, Rgba.Gray(0f, 0.62f));

        var screen = _ui.ScreenSize;
        if (view.Page == PauseMenuPage.Main)
        {
            _ui.DrawTextCentered("PAUSED", screen.X * 0.5f,
                PauseMenuLayout.TitleTop(screen, view.Page), _ui.HeadScale, Rgba.White);
            DrawMenuButton(PauseMenuLayout.MainButtonCenter(screen, 0), "RESUME");
            DrawMenuButton(PauseMenuLayout.MainButtonCenter(screen, 1), "SETTINGS");
            DrawMenuButton(PauseMenuLayout.MainButtonCenter(screen, 2), "SAVE & QUIT");
            return;
        }

        DrawSettingsMenu(settings);
    }

    /// <summary>
    /// The settings page: title, one tab strip (Display/Graphics/Water/Audio/Controls), the
    /// active tab's widget rows, BACK button. The busiest tab holds six rows, so the whole
    /// page fits without scrolling.
    /// </summary>
    internal void DrawSettingsMenu(SettingsMenuView view)
    {
        var screen = _ui.ScreenSize;
        var left = SettingsLayout.PanelLeft(screen);
        _ui.DrawTextCentered("SETTINGS", screen.X * 0.5f, SettingsLayout.PanelTop, _ui.HeadScale, Rgba.White);

        for (var t = 0; t < SettingsRows.Tabs.Length; t++)
        {
            var center = SettingsLayout.TabCenter(t, screen);
            var size = new Vector2(SettingsLayout.TabWidth() - 4f, SettingsLayout.TabHeight);
            var active = SettingsRows.Tabs[t] == view.Tab;
            _ui.DrawRect(center, size, active ? SelectedBackground : SlotBackground);
            if (active)
                _ui.DrawBorder(center, size, SlotBorder, SelectedBorderColor);
            _ui.DrawTextCentered(SettingsRows.Tabs[t], center.X, center.Y - 8f, _ui.CountScale, Rgba.White);
        }

        var rows = SettingsRows.RowsInTab(view.Tab);
        for (var r = 0; r < rows.Length; r++)
        {
            var row = SettingsRows.All[rows[r]];
            var center = SettingsLayout.RowCenter(r, screen);
            _ui.DrawText(_ui.FitText(row.Label, _ui.CountScale, SettingsLayout.LabelWidth - 16f),
                new Vector2(left + 8f, center.Y - 8f), _ui.CountScale, Rgba.White);

            switch (row)
            {
                case ToggleRow t:
                    DrawToggle(SettingsLayout.ToggleCenter(r, screen), t.Get());
                    DrawValueRight(center.Y, SettingsRows.FormatValue(row), screen);
                    break;
                case SliderRow s:
                    DrawSlider(r, screen, s, view.DragRow == rows[r]);
                    DrawValueRight(center.Y, SettingsRows.FormatValue(row), screen);
                    break;
                case OptionsRow:
                    // The GUI Scale row names the effective scale (this frame's Begin scale) while capped.
                    var value = ReferenceEquals(row, SettingsRows.GuiScaleRow)
                        ? UiCanvas.ScaleLabel(GameSettings.RequestedGuiScale, _ui.Scale)
                        : SettingsRows.FormatValue(row);
                    _ui.DrawTextCentered($"< {value} >",
                        SettingsLayout.TrackRect(r, screen).Center.X,
                        center.Y - 8f, _ui.CountScale, Rgba.White);
                    break;
            }
        }

        DrawMenuButton(SettingsLayout.BackCenter(screen), "BACK");
    }

    /// <summary>Settings widget under a window-pixel point (top-left origin), if any.</summary>
    internal SettingsHit SettingsHitAt(Vector2 point, SettingsMenuView view)
    {
        point = _ui.ToCanvas(point);
        return SettingsLayout.HitAt(point, view.Tab, _ui.ScreenSize);
    }

    private void DrawValueRight(float centerY, string text, Vector2 screen)
    {
        var w = _ui.TextWidth(text, _ui.CountScale);
        _ui.DrawText(text, new Vector2(SettingsLayout.ValueRight(screen) - w, centerY - 8f),
            _ui.CountScale, TextDim);
    }

    private void DrawToggle(Vector2 center, bool on)
    {
        var size = new Vector2(SettingsLayout.ToggleSize);
        _ui.DrawRect(center, size, SlotBackground);
        _ui.DrawBorder(center, size, SlotBorder, SlotBorderColor);
        if (on)
            _ui.DrawSquare(center, SettingsLayout.ToggleSize - 8f, AccentGold);
    }

    private void DrawSlider(int tabRow, Vector2 screen, SliderRow def, bool active)
    {
        var (center, size) = SettingsLayout.TrackRect(tabRow, screen);
        var fraction = MathUtils.Clamp01((def.Get() - def.Min) / (def.Max - def.Min));
        _ui.DrawRect(center, size, SlotBackground);
        if (fraction > 0f)
        {
            var fillW = size.X * fraction;
            _ui.DrawRect(new Vector2(center.X - size.X * 0.5f + fillW * 0.5f, center.Y),
                new Vector2(fillW, size.Y), active ? Rgba.White : AccentGold);
        }
        _ui.DrawSquare(new Vector2(center.X - size.X * 0.5f + size.X * fraction, center.Y),
            14f, active ? Rgba.White : SelectedBorderColor);
    }

    /// <summary>Menu button or BACK under a window-pixel point (top-left origin), if any.</summary>
    internal PauseMenuHit PauseMenuHitAt(Vector2 point, PauseMenuView view)
    {
        point = _ui.ToCanvas(point);
        return PauseMenuLayout.HitAt(point, _ui.ScreenSize, view.Page);
    }

    private void DrawMenuButton(Vector2 center, string label)
    {
        _ui.DrawRect(center, PauseMenuLayout.ButtonSize, SlotBackground);
        _ui.DrawBorder(center, PauseMenuLayout.ButtonSize, SlotBorder, SelectedBorderColor);
        _ui.DrawTextCentered(label, center.X, center.Y - 9f, _ui.CountScale, Rgba.White);
    }

    /// <summary>
    /// The main menu the game boots to, over an opaque backdrop (no world is rendered behind a
    /// menu frame). Every rectangle comes from <see cref="MainMenuLayout"/> (the main page from
    /// <see cref="PauseMenuLayout"/>), which <see cref="MainMenuHitAt"/> also reads, so drawing and
    /// hit-testing cannot drift apart. The settings page is the pause menu's, shared as is.
    /// <paramref name="time"/> is the window clock, for the text caret's blink.
    /// </summary>
    internal void DrawMainMenu(MainMenuView view, SettingsMenuView settings, float time)
    {
        var screen = _ui.ScreenSize;
        _ui.DrawRect(screen * 0.5f, screen, MenuBackdrop);

        switch (view.Page)
        {
            case MainMenuPage.Main:
                _ui.DrawTextCentered("CSVOXELENGINE", screen.X * 0.5f,
                    MainMenuLayout.TitleTop(screen, MainMenuPage.Main), _ui.HeadScale, Rgba.White);
                DrawMenuButton(MainMenuLayout.MainButtonCenter(screen, 0), "START");
                DrawMenuButton(MainMenuLayout.MainButtonCenter(screen, 1), "MULTIPLAYER");
                DrawMenuButton(MainMenuLayout.MainButtonCenter(screen, 2), "SETTINGS");
                DrawMenuButton(MainMenuLayout.MainButtonCenter(screen, 3), "EXIT");
                break;

            case MainMenuPage.Multiplayer:
                DrawMultiplayerPage(view, screen);
                break;

            case MainMenuPage.AddServer:
                _ui.DrawTextCentered("ADD SERVER", screen.X * 0.5f,
                    MainMenuLayout.TitleTop(screen, MainMenuPage.AddServer), _ui.HeadScale, Rgba.White);
                DrawLabeledField(MainMenuLayout.NameFieldCenter(screen), "NAME", view.ServerName,
                    view.Focus == MenuField.ServerName, WorldNames.DefaultName, time);
                DrawLabeledField(MainMenuLayout.SeedFieldCenter(screen), "ADDRESS", view.Address,
                    view.Focus == MenuField.Address, "192.168.1.10 or host:25566", time);
                DrawNotice(view, screen);
                DrawMenuButton(MainMenuLayout.BottomButtonCenter(screen, 0), "ADD");
                DrawMenuButton(MainMenuLayout.BottomButtonCenter(screen, 1), "BACK");
                break;

            case MainMenuPage.DirectConnect:
                _ui.DrawTextCentered("DIRECT CONNECT", screen.X * 0.5f,
                    MainMenuLayout.TitleTop(screen, MainMenuPage.DirectConnect), _ui.HeadScale, Rgba.White);
                DrawLabeledField(MainMenuLayout.NameFieldCenter(screen), "ADDRESS", view.Address,
                    view.Focus == MenuField.Address, "192.168.1.10 or host:25566", time);
                DrawNotice(view, screen);
                DrawMenuButton(MainMenuLayout.BottomButtonCenter(screen, 0), "CONNECT");
                DrawMenuButton(MainMenuLayout.BottomButtonCenter(screen, 1), "BACK");
                break;

            case MainMenuPage.ConfirmDeleteServer:
                DrawMultiplayerPage(view, screen);
                DrawConfirmDeleteServer(view, screen);
                break;

            case MainMenuPage.Worlds:
                DrawWorldsPage(view, screen);
                break;

            case MainMenuPage.Create:
                _ui.DrawTextCentered("NEW WORLD", screen.X * 0.5f,
                    MainMenuLayout.TitleTop(screen, MainMenuPage.Create), _ui.HeadScale, Rgba.White);
                DrawLabeledField(MainMenuLayout.NameFieldCenter(screen), "NAME", view.Name,
                    view.Focus == MenuField.Name, WorldNames.DefaultName, time);
                DrawLabeledField(MainMenuLayout.SeedFieldCenter(screen), "SEED", view.Seed,
                    view.Focus == MenuField.Seed, "random", time);
                DrawMenuButton(MainMenuLayout.BottomButtonCenter(screen, 0), "CREATE");
                DrawMenuButton(MainMenuLayout.BottomButtonCenter(screen, 1), "BACK");
                break;

            case MainMenuPage.Edit:
                _ui.DrawTextCentered("EDIT WORLD", screen.X * 0.5f,
                    MainMenuLayout.TitleTop(screen, MainMenuPage.Edit), _ui.HeadScale, Rgba.White);
                DrawLabeledField(MainMenuLayout.NameFieldCenter(screen), "NAME", view.Name,
                    view.Focus == MenuField.Name, WorldNames.DefaultName, time);
                DrawMenuButton(MainMenuLayout.BottomButtonCenter(screen, 0), "SAVE");
                DrawMenuButton(MainMenuLayout.BottomButtonCenter(screen, 1), "BACK");
                break;

            case MainMenuPage.ConfirmDelete:
                DrawWorldsPage(view, screen);
                DrawConfirmDelete(view, screen);
                break;

            case MainMenuPage.Settings:
                DrawSettingsMenu(settings);
                break;
        }
    }

    // The worlds list: title, panel, the scissored rows (name and seed on their own lines, never
    // concatenated since a layout run stops at MaxLayoutChars), the empty-state line, the notice
    // and the CREATE / BACK buttons.
    private void DrawWorldsPage(MainMenuView view, Vector2 screen)
    {
        _ui.DrawTextCentered("WORLDS", screen.X * 0.5f,
            MainMenuLayout.TitleTop(screen, MainMenuPage.Worlds), _ui.HeadScale, Rgba.White);

        var left = MainMenuLayout.PanelLeft(screen);
        var top = MainMenuLayout.ListTop();
        var listHeight = MainMenuLayout.ListHeight(screen);
        var panelCenter = new Vector2(left + MainMenuLayout.PanelWidth * 0.5f, top + listHeight * 0.5f);
        var panelSize = new Vector2(MainMenuLayout.PanelWidth, listHeight);
        _ui.DrawRect(panelCenter, panelSize, SlotBackground);
        _ui.DrawBorder(panelCenter, panelSize, SlotBorder, SlotBorderColor);

        var count = view.Worlds.Count;
        if (count == 0)
        {
            _ui.DrawTextCentered("No worlds yet - create one", panelCenter.X, panelCenter.Y - 8f,
                _ui.CountScale, TextDim);
        }
        else
        {
            // Clamped with this frame's row count, exactly as MainMenuLayout.HitAt clamps it.
            var offset = MainMenuLayout.ClampedOffset(view.ScrollOffset, count, screen);
            var (first, end) = MainMenuLayout.VisibleRows(offset, count, screen);
            var nameLeft = MainMenuLayout.NameLeft(screen);
            var nameWidth = MainMenuLayout.NameMaxWidth();

            // glScissor takes framebuffer pixels with a bottom-left origin, hence the conversion.
            // Only the row loop runs while it is enabled, and nothing returns from inside it.
            var (sx, sy, sw, sh) = UiCanvas.ScissorRect(new Vector2(left, top), panelSize,
                _ui.Scale, _ui.WindowSize, _ui.FramebufferSize);
            _gl.Enable(EnableCap.ScissorTest);
            _gl.Scissor(sx, sy, (uint)sw, (uint)sh);
            try
            {
                for (var i = first; i < end; i++)
                {
                    var world = view.Worlds[i];
                    var rowTop = MainMenuLayout.RowTop(i, offset);
                    if (i > 0)
                        _ui.DrawRect(new Vector2(panelCenter.X, rowTop),
                            new Vector2(MainMenuLayout.PanelWidth - 2f * MainMenuLayout.RowPad, 1.5f),
                            SlotBorderColor);
                    _ui.DrawText(_ui.FittedText(world.Name, _ui.CountScale, nameWidth),
                        new Vector2(nameLeft, rowTop + 8f), _ui.CountScale, Rgba.White);
                    _ui.DrawText(MainMenuLayout.WorldDetailText(world.Seed, world.LastPlayedUnixMs),
                        new Vector2(nameLeft, rowTop + 36f), _ui.CountScale, TextDim);
                    DrawSmallButton(MainMenuLayout.RowButtonCenter(screen, i, offset, 0),
                        MainMenuLayout.RowButtonSize, "PLAY");
                    DrawSmallButton(MainMenuLayout.RowButtonCenter(screen, i, offset, 1),
                        MainMenuLayout.RowButtonSize, "EDIT");
                    DrawSmallButton(MainMenuLayout.RowButtonCenter(screen, i, offset, 2),
                        MainMenuLayout.RowButtonSize, "DELETE", DangerRed);
                }
            }
            finally
            {
                _gl.Disable(EnableCap.ScissorTest);
            }
        }

        if (!string.IsNullOrEmpty(view.Notice))
            _ui.DrawTextCentered(_ui.FittedText(view.Notice, _ui.CountScale, MainMenuLayout.PanelWidth),
                screen.X * 0.5f, MainMenuLayout.NoticeTop(screen), _ui.CountScale, AccentGold);

        DrawMenuButton(MainMenuLayout.BottomButtonCenter(screen, 0), "CREATE");
        DrawMenuButton(MainMenuLayout.BottomButtonCenter(screen, 1), "BACK");
    }

    // The multiplayer list, shaped exactly like the worlds list: title, panel, the scissored rows
    // (name over address) with CONNECT / DELETE, the empty state, the notice and the three bottom
    // buttons ADD SERVER / DIRECT CONNECT / BACK.
    private void DrawMultiplayerPage(MainMenuView view, Vector2 screen)
    {
        _ui.DrawTextCentered("MULTIPLAYER", screen.X * 0.5f,
            MainMenuLayout.TitleTop(screen, MainMenuPage.Multiplayer), _ui.HeadScale, Rgba.White);

        var left = MainMenuLayout.PanelLeft(screen);
        var top = MainMenuLayout.ListTop();
        var listHeight = MainMenuLayout.ListHeight(screen);
        var panelCenter = new Vector2(left + MainMenuLayout.PanelWidth * 0.5f, top + listHeight * 0.5f);
        var panelSize = new Vector2(MainMenuLayout.PanelWidth, listHeight);
        _ui.DrawRect(panelCenter, panelSize, SlotBackground);
        _ui.DrawBorder(panelCenter, panelSize, SlotBorder, SlotBorderColor);

        var count = view.Servers.Count;
        if (count == 0)
        {
            _ui.DrawTextCentered("No servers yet - add one", panelCenter.X, panelCenter.Y - 8f,
                _ui.CountScale, TextDim);
        }
        else
        {
            var offset = MainMenuLayout.ClampedOffset(view.ServerScrollOffset, count, screen);
            var (first, end) = MainMenuLayout.VisibleRows(offset, count, screen);
            var nameLeft = MainMenuLayout.NameLeft(screen);
            var nameWidth = MainMenuLayout.NameMaxWidth();

            var (sx, sy, sw, sh) = UiCanvas.ScissorRect(new Vector2(left, top), panelSize,
                _ui.Scale, _ui.WindowSize, _ui.FramebufferSize);
            _gl.Enable(EnableCap.ScissorTest);
            _gl.Scissor(sx, sy, (uint)sw, (uint)sh);
            try
            {
                for (var i = first; i < end; i++)
                {
                    var server = view.Servers[i];
                    var rowTop = MainMenuLayout.RowTop(i, offset);
                    if (i > 0)
                        _ui.DrawRect(new Vector2(panelCenter.X, rowTop),
                            new Vector2(MainMenuLayout.PanelWidth - 2f * MainMenuLayout.RowPad, 1.5f),
                            SlotBorderColor);
                    _ui.DrawText(_ui.FittedText(server.Name, _ui.CountScale, nameWidth),
                        new Vector2(nameLeft, rowTop + 8f), _ui.CountScale, Rgba.White);
                    _ui.DrawText(server.Address.ToString(), new Vector2(nameLeft, rowTop + 36f), _ui.CountScale, TextDim);
                    DrawSmallButton(MainMenuLayout.RowButtonCenter(screen, i, offset, 0, 2),
                        MainMenuLayout.RowButtonSize, "CONNECT");
                    DrawSmallButton(MainMenuLayout.RowButtonCenter(screen, i, offset, 1, 2),
                        MainMenuLayout.RowButtonSize, "DELETE", DangerRed);
                }
            }
            finally
            {
                _gl.Disable(EnableCap.ScissorTest);
            }
        }

        DrawNotice(view, screen);

        DrawSmallButton(MainMenuLayout.TripleButtonCenter(screen, 0), MainMenuLayout.TripleButtonSize, "ADD SERVER");
        DrawSmallButton(MainMenuLayout.TripleButtonCenter(screen, 1), MainMenuLayout.TripleButtonSize, "DIRECT CONNECT");
        DrawSmallButton(MainMenuLayout.TripleButtonCenter(screen, 2), MainMenuLayout.TripleButtonSize, "BACK");
    }

    private void DrawNotice(MainMenuView view, Vector2 screen)
    {
        if (!string.IsNullOrEmpty(view.Notice))
            _ui.DrawTextCentered(_ui.FittedText(view.Notice, _ui.CountScale, MainMenuLayout.PanelWidth),
                screen.X * 0.5f, MainMenuLayout.NoticeTop(screen), _ui.CountScale, AccentGold);
    }

    // The delete confirmation over the dimmed multiplayer page: question, the server's name on its
    // own line, DELETE / CANCEL.
    private void DrawConfirmDeleteServer(MainMenuView view, Vector2 screen)
    {
        _ui.DrawRect(screen * 0.5f, screen, Rgba.Gray(0f, 0.62f));

        var center = MainMenuLayout.ConfirmBoxCenter(screen);
        var size = MainMenuLayout.ConfirmBoxSize;
        _ui.DrawRect(center, size, CardBackground);
        _ui.DrawBorder(center, size, SlotBorder, SelectedBorderColor);

        var top = center.Y - size.Y * 0.5f;
        _ui.DrawTextCentered("Delete this server?", center.X, top + 32f, _ui.HeadScale, Rgba.White);
        if (view.SelectedServer is { } server)
            _ui.DrawTextCentered(_ui.FittedText(server.Name, _ui.CountScale, size.X - 48f),
                center.X, top + 84f, _ui.CountScale, AccentGold);

        DrawSmallButton(MainMenuLayout.ConfirmYesCenter(screen), MainMenuLayout.ConfirmButtonSize,
            "DELETE", DangerRed);
        DrawSmallButton(MainMenuLayout.ConfirmNoCenter(screen), MainMenuLayout.ConfirmButtonSize, "CANCEL");
    }

    // The delete confirmation over the dimmed worlds page: question, the world's name on its own
    // line, DELETE / CANCEL.
    private void DrawConfirmDelete(MainMenuView view, Vector2 screen)
    {
        _ui.DrawRect(screen * 0.5f, screen, Rgba.Gray(0f, 0.62f));

        var center = MainMenuLayout.ConfirmBoxCenter(screen);
        var size = MainMenuLayout.ConfirmBoxSize;
        // CardBackground rather than the translucent SlotBackground, so the rows under the dim
        // do not show through the box.
        _ui.DrawRect(center, size, CardBackground);
        _ui.DrawBorder(center, size, SlotBorder, SelectedBorderColor);

        var top = center.Y - size.Y * 0.5f;
        _ui.DrawTextCentered("Delete this world?", center.X, top + 32f, _ui.HeadScale, Rgba.White);
        if (view.SelectedWorld is { } world)
            _ui.DrawTextCentered(_ui.FittedText(world.Name, _ui.CountScale, size.X - 48f),
                center.X, top + 84f, _ui.CountScale, AccentGold);

        DrawSmallButton(MainMenuLayout.ConfirmYesCenter(screen), MainMenuLayout.ConfirmButtonSize,
            "DELETE", DangerRed);
        DrawSmallButton(MainMenuLayout.ConfirmNoCenter(screen), MainMenuLayout.ConfirmButtonSize, "CANCEL");
    }

    private void DrawLabeledField(Vector2 center, string label, TextFieldState field, bool focused,
        string placeholder, float time)
    {
        _ui.DrawText(label, new Vector2(center.X - MainMenuLayout.FieldSize.X * 0.5f,
            MainMenuLayout.FieldLabelTop(center)), _ui.CountScale, TextDim);
        _ui.DrawInputField(center, MainMenuLayout.FieldSize, field, focused, placeholder, time);
    }

    /// <summary>A <see cref="DrawMenuButton"/> of any size, with an optional border color.</summary>
    private void DrawSmallButton(Vector2 center, Vector2 size, string label, Rgba? border = null)
    {
        _ui.DrawRect(center, size, SlotBackground);
        _ui.DrawBorder(center, size, SlotBorder, border ?? SelectedBorderColor);
        _ui.DrawTextCentered(label, center.X, center.Y - 9f, _ui.CountScale, Rgba.White);
    }

    /// <summary>Main-menu widget under a window-pixel point (top-left origin).</summary>
    internal MainMenuHit MainMenuHitAt(Vector2 windowPoint, MainMenuView view) =>
        MainMenuLayout.HitAt(_ui.ToCanvas(windowPoint), _ui.ScreenSize, view);
    /// <summary>Whether a window-pixel point is over the worlds list (the mouse-wheel target).</summary>
    internal bool IsOverWorldList(Vector2 windowPoint) =>
        MainMenuLayout.IsOverList(_ui.ToCanvas(windowPoint), _ui.ScreenSize);

    /// <summary>Whether a window-pixel point is over the multiplayer server list (the mouse-wheel target).</summary>
    internal bool IsOverServerList(Vector2 windowPoint) =>
        MainMenuLayout.IsOverList(_ui.ToCanvas(windowPoint), _ui.ScreenSize);
}
