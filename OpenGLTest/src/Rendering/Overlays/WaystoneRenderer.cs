using System;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Simulation.Waystones;
using VoxelEngine.UI;
using VoxelEngine.UI.Waystone;
using VoxelEngine.UI.Widgets;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The waystone screen: the discovery popup ("{name} has been discovered", CONFIRM and, for the first discoverer,
/// RENAME), the rename dialog (the name field, RENAME and CANCEL) and the teleport list panel (the current waystone's
/// name, the search field, the scrolled rows with name, realm tag and distance, lost rows dimmed, the current one marked
/// and a footer with the mana requirement in green or red). Every rectangle comes from <see cref="WaystoneLayout"/>,
/// which the clicks read too. Draws through the <see cref="UIRenderer"/> primitives. Render thread only.
/// </summary>
internal sealed class WaystoneRenderer
{
    private const float Margin = 14f;
    // Room for the widest tag ("underground") and a four-digit distance; a longer distance pushes the tag left
    private const float DistanceColumn = 230f;
    private const float RealmColumn = 240f;

    private static readonly Rgba Backdrop = Rgba.Gray(0f, 0.55f);
    private static readonly Rgba UndergroundColor = new(196, 172, 136);
    private static readonly Rgba SkyColor = new(150, 205, 255);

    private readonly GL _gl;
    private readonly UIRenderer _ui;

    internal WaystoneRenderer(GL gl, UIRenderer ui)
    {
        _gl = gl;
        _ui = ui;
    }

    /// <summary>Draws the page <paramref name="view"/> has up; <paramref name="time"/> blinks the caret.</summary>
    internal void Draw(WaystoneView view, float time)
    {
        switch (view.Page)
        {
            case WaystonePage.Discovered:
                DrawPopup(view);
                break;
            case WaystonePage.Rename:
                DrawRename(view, time);
                break;
            case WaystonePage.List:
                DrawList(view, time);
                break;
        }
    }

    private void DrawDialogBox(UiRect box)
    {
        var canvas = _ui.ScreenSize;
        _ui.DrawRect(canvas * 0.5f, canvas, Backdrop);
        _ui.DrawRect(box.Center, box.Size, CardBackground);
        _ui.DrawBorder(box.Center, box.Size, Border.Standard, SlotBorderColor);
        _ui.DrawRect(new Vector2(box.Center.X, box.Top + 3f), new Vector2(box.Size.X - 4f, 2f), AccentGold);
    }

    private void DrawPopup(WaystoneView view)
    {
        var canvas = _ui.ScreenSize;
        var box = WaystoneLayout.PopupBox(canvas);
        DrawDialogBox(box);
        _ui.DrawTextMiddle(_ui.FittedText(view.Name, _ui.HeadScale, box.Size.X - Margin * 4f),
            WaystoneLayout.PopupNameCenter(canvas), _ui.HeadScale, AccentGold);
        _ui.DrawTextMiddle(WaystoneText.DiscoveredLine, WaystoneLayout.PopupLineCenter(canvas), _ui.CountScale, Rgba.White);
        _ui.DrawButton(WaystoneLayout.ConfirmButton(canvas, view.CanRename), "CONFIRM", true, ReadyGreen);
        if (view.CanRename)
            _ui.DrawButton(WaystoneLayout.RenameButton(canvas), "RENAME", true, AccentGold);
    }

    private void DrawRename(WaystoneView view, float time)
    {
        var canvas = _ui.ScreenSize;
        var box = WaystoneLayout.RenameBox(canvas);
        DrawDialogBox(box);
        _ui.DrawTextMiddle(WaystoneText.RenameTitle, WaystoneLayout.RenameTitleCenter(canvas), _ui.HeadScale, Rgba.White);
        var field = WaystoneLayout.RenameField(canvas);
        _ui.DrawInputField(field.Center, field.Size, view.RenameField, focused: true, view.Name, time);
        _ui.DrawTextMiddle(WaystoneText.RenameHint, WaystoneLayout.RenameHintCenter(canvas), _ui.CountScale, TextDim);
        _ui.DrawButton(WaystoneLayout.RenameOk(canvas), "RENAME", view.CanSubmitRename, ReadyGreen);
        _ui.DrawButton(WaystoneLayout.RenameCancel(canvas), "CANCEL", true, SlotBorderColor);
    }

    private void DrawList(WaystoneView view, float time)
    {
        var canvas = _ui.ScreenSize;
        _ui.DrawPanelFrame(WaystoneLayout.Panel(canvas), CardBackground);

        var title = WaystoneLayout.Title(canvas);
        _ui.DrawTextCenteredY(_ui.FittedText(view.Name, _ui.HeadScale, title.Size.X), title.Left, title.Center.Y,
            _ui.HeadScale, AccentGold);

        var search = WaystoneLayout.SearchBox(canvas);
        _ui.DrawInputField(search.Center, search.Size, view.Search.Text, view.SearchFocused, WaystoneText.SearchPlaceholder,
            time);

        var footer = WaystoneLayout.Footer(canvas);
        var (mana, met) = WaystoneText.ManaLine(view.Mana.Free, view.Mana.MoreThanHalf);
        _ui.DrawTextMiddle(_ui.FittedText(mana, _ui.CountScale, footer.Size.X), footer.Center, _ui.CountScale,
            met ? ReadyGreen : DangerRed);

        var list = WaystoneLayout.List(canvas);
        var rows = view.LastRows;
        if (rows.Count == 0)
        {
            var empty = view.Search.IsEmpty ? WaystoneText.NoneFound : WaystoneText.NothingMatches;
            _ui.DrawTextCenteredFitted(empty, list.Center.X, list.Top + 12f, _ui.CountScale, TextDim, list.Size.X);
            return;
        }

        var offset = WaystoneLayout.ClampedOffset(view.ScrollOffset, rows.Count, canvas);
        var (first, end) = ScrollList.VisibleRange(offset, WaystoneLayout.RowHeight, rows.Count, list.Size.Y);

        // The pass's depth clear must happen before the scissor goes on (a clear honors it)
        _ui.EnsureDepthCleared();
        var (sx, sy, sw, sh) = UiCanvas.ScissorRect(new Vector2(list.Left, list.Top) + _ui.OverlayOffset, list.Size,
            _ui.Scale, _ui.WindowSize, _ui.FramebufferSize);
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(sx, sy, (uint)sw, (uint)sh);
        try
        {
            for (var i = first; i < end; i++)
                DrawRow(WaystoneLayout.RowRect(i, canvas, offset), rows[i], view.Hover, list);
        }
        finally
        {
            _gl.Disable(EnableCap.ScissorTest);
        }
    }

    private void DrawRow(UiRect row, in WaystoneRow waystone, Vector2? hover, UiRect list)
    {
        var hovered = waystone.Selectable && hover is { } p && row.Contains(p) && list.Contains(p);
        _ui.DrawRect(row.Center, row.Size, hovered ? HoverBackground : waystone.Current ? SelectedBackground : SlotBackground);
        if (waystone.Current)
            _ui.DrawRect(new Vector2(row.Left + 2f, row.Center.Y), new Vector2(4f, row.Size.Y), AccentGold);

        var nameColor = waystone.Lost ? TextDim : waystone.Current ? AccentGold : Rgba.White;
        var dim = waystone.Lost ? 0.6f : 1f;
        var nameLeft = row.Left + Margin;
        var distance = WaystoneText.Distance(waystone);
        var distanceLeft = row.Right - Margin - _ui.TextWidth(distance, _ui.CountScale);
        var tag = WaystoneRealms.Tag(waystone.Realm);
        var realmLeft = MathF.Min(row.Right - Margin - DistanceColumn - RealmColumn,
            distanceLeft - Margin - _ui.TextWidth(tag, _ui.CountScale));
        var name = waystone.Lost ? waystone.Name + " (" + WaystoneText.Lost + ")" : waystone.Name;
        _ui.DrawTextCenteredY(_ui.FittedText(name, _ui.CountScale, realmLeft - nameLeft - Margin), nameLeft, row.Center.Y,
            _ui.CountScale, nameColor);

        _ui.DrawTextCenteredY(tag, realmLeft, row.Center.Y, _ui.CountScale, UIRenderer.Faded(RealmColor(waystone.Realm), dim));
        _ui.DrawTextCenteredY(distance, distanceLeft, row.Center.Y, _ui.CountScale, waystone.Lost ? TextDim : Rgba.White);
    }

    private static Rgba RealmColor(WaystoneRealm realm) => realm switch
    {
        WaystoneRealm.Underground => UndergroundColor,
        WaystoneRealm.Hell => DangerRed,
        WaystoneRealm.Sky => SkyColor,
        _ => ReadyGreen,
    };
}
