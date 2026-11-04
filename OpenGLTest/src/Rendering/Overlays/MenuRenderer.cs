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
                DrawPageTitle(screen, MainMenuPage.Main, "CSVOXELENGINE");
                DrawMenuButton(MainMenuLayout.MainButtonCenter(screen, 0), "START");
                DrawMenuButton(MainMenuLayout.MainButtonCenter(screen, 1), "MULTIPLAYER");
                DrawMenuButton(MainMenuLayout.MainButtonCenter(screen, 2), "SETTINGS");
                DrawMenuButton(MainMenuLayout.MainButtonCenter(screen, 3), "EXIT");
                break;

            case MainMenuPage.Multiplayer:
                DrawMultiplayerPage(view, screen);
                break;

            case MainMenuPage.AddServer:
                DrawPageTitle(screen, MainMenuPage.AddServer, "ADD SERVER");
                DrawLabeledField(MainMenuLayout.NameFieldCenter(screen), "NAME", view.ServerName,
                    view.Focus == MenuField.ServerName, WorldNames.DefaultName, time);
                DrawLabeledField(MainMenuLayout.SeedFieldCenter(screen), "ADDRESS", view.Address,
                    view.Focus == MenuField.Address, "192.168.1.10 or host:25566", time);
                DrawNotice(view, screen);
                DrawBottomPair(screen, "ADD");
                break;

            case MainMenuPage.DirectConnect:
                DrawPageTitle(screen, MainMenuPage.DirectConnect, "DIRECT CONNECT");
                DrawLabeledField(MainMenuLayout.NameFieldCenter(screen), "ADDRESS", view.Address,
                    view.Focus == MenuField.Address, "192.168.1.10 or host:25566", time);
                DrawNotice(view, screen);
                DrawBottomPair(screen, "CONNECT");
                break;

            case MainMenuPage.ConfirmDeleteServer:
                DrawMultiplayerPage(view, screen);
                DrawConfirmDelete(screen, "Delete this server?", view.SelectedServer?.Name);
                break;

            case MainMenuPage.Worlds:
                DrawWorldsPage(view, screen);
                break;

            case MainMenuPage.Create:
                DrawPageTitle(screen, MainMenuPage.Create, "NEW WORLD");
                DrawLabeledField(MainMenuLayout.NameFieldCenter(screen), "NAME", view.Name,
                    view.Focus == MenuField.Name, WorldNames.DefaultName, time);
                DrawLabeledField(MainMenuLayout.SeedFieldCenter(screen), "SEED", view.Seed,
                    view.Focus == MenuField.Seed, "random", time);
                DrawBottomPair(screen, "CREATE");
                break;

            case MainMenuPage.Edit:
                DrawPageTitle(screen, MainMenuPage.Edit, "EDIT WORLD");
                DrawLabeledField(MainMenuLayout.NameFieldCenter(screen), "NAME", view.Name,
                    view.Focus == MenuField.Name, WorldNames.DefaultName, time);
                DrawBottomPair(screen, "SAVE");
                break;

            case MainMenuPage.ConfirmDelete:
                DrawWorldsPage(view, screen);
                DrawConfirmDelete(screen, "Delete this world?", view.SelectedWorld?.Name);
                break;

            case MainMenuPage.Settings:
                DrawSettingsMenu(settings);
                break;
        }
    }

    // The title every page draws centered over its content.
    private void DrawPageTitle(Vector2 screen, MainMenuPage page, string title) =>
        _ui.DrawTextCentered(title, screen.X * 0.5f, MainMenuLayout.TitleTop(screen, page), _ui.HeadScale,
            Rgba.White);

    // The confirm / BACK pair across the bottom of a form or list page.
    private void DrawBottomPair(Vector2 screen, string confirmLabel)
    {
        DrawMenuButton(MainMenuLayout.BottomButtonCenter(screen, 0), confirmLabel);
        DrawMenuButton(MainMenuLayout.BottomButtonCenter(screen, 1), "BACK");
    }

    // Draws one list row's name, detail line and row buttons under the separator DrawListPage draws.
    // Each page's drawer is a static lambda cached in a field, so a menu frame allocates nothing for it.
    private delegate void ListRowDrawer(MenuRenderer renderer, MainMenuView view, Vector2 screen, int row,
        float offset, float rowTop, float nameLeft, float nameWidth);

    // A worlds row: the name over the seed and last-played line, then PLAY / EDIT / DELETE.
    private static readonly ListRowDrawer WorldRow =
        static (renderer, view, screen, i, offset, rowTop, nameLeft, nameWidth) =>
        {
            var world = view.Worlds[i];
            renderer.DrawRowText(world.Name, MainMenuLayout.WorldDetailText(world.Seed, world.LastPlayedUnixMs),
                rowTop, nameLeft, nameWidth);
            renderer.DrawSmallButton(MainMenuLayout.RowButtonCenter(screen, i, offset, 0),
                MainMenuLayout.RowButtonSize, "PLAY");
            renderer.DrawSmallButton(MainMenuLayout.RowButtonCenter(screen, i, offset, 1),
                MainMenuLayout.RowButtonSize, "EDIT");
            renderer.DrawSmallButton(MainMenuLayout.RowButtonCenter(screen, i, offset, 2),
                MainMenuLayout.RowButtonSize, "DELETE", DangerRed);
        };

    // A server row: the name over the address, then CONNECT / DELETE.
    private static readonly ListRowDrawer ServerRow =
        static (renderer, view, screen, i, offset, rowTop, nameLeft, nameWidth) =>
        {
            var server = view.Servers[i];
            renderer.DrawRowText(server.Name, server.Address.ToString(), rowTop, nameLeft, nameWidth);
            renderer.DrawSmallButton(MainMenuLayout.RowButtonCenter(screen, i, offset, 0, 2),
                MainMenuLayout.RowButtonSize, "CONNECT");
            renderer.DrawSmallButton(MainMenuLayout.RowButtonCenter(screen, i, offset, 1, 2),
                MainMenuLayout.RowButtonSize, "DELETE", DangerRed);
        };

    // The worlds list with its CREATE / BACK buttons.
    private void DrawWorldsPage(MainMenuView view, Vector2 screen)
    {
        DrawListPage(view, screen, "WORLDS", MainMenuPage.Worlds, view.Worlds.Count, view.ScrollOffset,
            "No worlds yet - create one", WorldRow);

        DrawBottomPair(screen, "CREATE");
    }

    // The multiplayer list, shaped like the worlds list, with ADD SERVER / DIRECT CONNECT / BACK.
    private void DrawMultiplayerPage(MainMenuView view, Vector2 screen)
    {
        DrawListPage(view, screen, "MULTIPLAYER", MainMenuPage.Multiplayer, view.Servers.Count,
            view.ServerScrollOffset, "No servers yet - add one", ServerRow);
        DrawSmallButton(MainMenuLayout.TripleButtonCenter(screen, 0), MainMenuLayout.TripleButtonSize, "ADD SERVER");
        DrawSmallButton(MainMenuLayout.TripleButtonCenter(screen, 1), MainMenuLayout.TripleButtonSize, "DIRECT CONNECT");
        DrawSmallButton(MainMenuLayout.TripleButtonCenter(screen, 2), MainMenuLayout.TripleButtonSize, "BACK");
    }

    // A scrolled list page (worlds or multiplayer): title, panel, the scissored rows (name and detail
    // on their own lines, never concatenated since a layout run stops at MaxLayoutChars), the
    // empty-state line and the notice. The page draws its bottom buttons after.
    private void DrawListPage(MainMenuView view, Vector2 screen, string title, MainMenuPage page,
        int count, float scrollOffset, string emptyText, ListRowDrawer drawRow)
    {
        DrawPageTitle(screen, page, title);

        var left = MainMenuLayout.PanelLeft(screen);
        var top = MainMenuLayout.ListTop();
        var listHeight = MainMenuLayout.ListHeight(screen);
        var panelCenter = new Vector2(left + MainMenuLayout.PanelWidth * 0.5f, top + listHeight * 0.5f);
        var panelSize = new Vector2(MainMenuLayout.PanelWidth, listHeight);
        _ui.DrawRect(panelCenter, panelSize, SlotBackground);
        _ui.DrawBorder(panelCenter, panelSize, SlotBorder, SlotBorderColor);
        if (count == 0)
        {
            _ui.DrawTextCentered(emptyText, panelCenter.X, panelCenter.Y - 8f, _ui.CountScale, TextDim);
        }
        else
        {
            // Clamped with this frame's row count, exactly as MainMenuLayout.HitAt clamps it.
            var offset = MainMenuLayout.ClampedOffset(scrollOffset, count, screen);
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
                    var rowTop = MainMenuLayout.RowTop(i, offset);
                    if (i > 0)
                        _ui.DrawRect(new Vector2(panelCenter.X, rowTop),
                            new Vector2(MainMenuLayout.PanelWidth - 2f * MainMenuLayout.RowPad, 1.5f),
                            SlotBorderColor);
                    drawRow(this, view, screen, i, offset, rowTop, nameLeft, nameWidth);
                }
            }
            finally
            {
                _gl.Disable(EnableCap.ScissorTest);
            }
        }

        DrawNotice(view, screen);
    }

    // A list row's name, fitted to the name column, over its dim detail line.
    private void DrawRowText(string name, string detail, float rowTop, float nameLeft, float nameWidth)
    {
        _ui.DrawText(_ui.FittedText(name, _ui.CountScale, nameWidth),
            new Vector2(nameLeft, rowTop + 8f), _ui.CountScale, Rgba.White);
        _ui.DrawText(detail, new Vector2(nameLeft, rowTop + 36f), _ui.CountScale, TextDim);
    }

    private void DrawNotice(MainMenuView view, Vector2 screen)
    {
        if (!string.IsNullOrEmpty(view.Notice))
            _ui.DrawTextCentered(_ui.FittedText(view.Notice, _ui.CountScale, MainMenuLayout.PanelWidth),
                screen.X * 0.5f, MainMenuLayout.NoticeTop(screen), _ui.CountScale, AccentGold);
    }

    // The delete confirmation over the dimmed worlds or multiplayer page: the question, the
    // selected world's or server's name on its own line, DELETE / CANCEL.
    private void DrawConfirmDelete(Vector2 screen, string question, string? name)
    {
        _ui.DrawRect(screen * 0.5f, screen, Rgba.Gray(0f, 0.62f));

        var center = MainMenuLayout.ConfirmBoxCenter(screen);
        var size = MainMenuLayout.ConfirmBoxSize;
        // CardBackground rather than the translucent SlotBackground, so the rows under the dim
        // do not show through the box.
        _ui.DrawRect(center, size, CardBackground);
        _ui.DrawBorder(center, size, SlotBorder, SelectedBorderColor);

        var top = center.Y - size.Y * 0.5f;
        _ui.DrawTextCentered(question, center.X, top + 32f, _ui.HeadScale, Rgba.White);
        if (name is not null)
            _ui.DrawTextCentered(_ui.FittedText(name, _ui.CountScale, size.X - 48f),
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
