using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Items;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.UI;
using VoxelEngine.UI.Chest;
using VoxelEngine.UI.Widgets;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The chest card beside the [E] panel while a chest is open: its title and fill, a search field and the scrolled list
/// of what it holds (icon, name, count), each row wearing its item's <see cref="RarityStyle"/>: the rarity chrome, a
/// rarity-colored accent bar and name. Geometry is <see cref="ChestCardLayout"/>, which the click
/// handling reads too. Draws through the <see cref="UIRenderer"/> primitives. Render thread only.
/// </summary>
internal sealed class ChestCardRenderer
{
    private const float RowIcon = 28f;

    private readonly GL _gl;
    private readonly UIRenderer _ui;

    internal ChestCardRenderer(GL gl, UIRenderer ui)
    {
        _gl = gl;
        _ui = ui;
    }

    /// <summary>Draws the card; <paramref name="hover"/> is the pointer in canvas units for the row hover look.</summary>
    internal void Draw(ChestView view, bool searchFocused, Vector2? hover, float time, float headingDegrees)
    {
        var screen = _ui.ScreenSize;
        var card = ChestCardLayout.Card(screen);
        _ui.DrawPanelFrame(card, PanelBackground);

        var contents = view.Contents;
        var title = ChestCardLayout.Title(screen);
        // The fill (used/total slots) sits at the right edge in body text; the name gets the rest of the row
        var fill = contents == null ? "" : $"{Used(contents)}/{contents.SlotCount}";
        var fillWidth = fill.Length == 0 ? 0f : _ui.TextWidth(fill, _ui.CountScale) + 12f;
        _ui.DrawTextCenteredY(_ui.FitText(view.Title, _ui.CountScale, title.Size.X - fillWidth), title.Left, title.Center.Y,
            _ui.CountScale, AccentGold);
        if (fill.Length > 0)
            _ui.DrawTextCenteredY(fill, title.Right - fillWidth + 12f, title.Center.Y, _ui.CountScale, TextDim);

        var search = ChestCardLayout.SearchBox(screen);
        _ui.DrawInputField(search.Center, search.Size, view.Search.Text, searchFocused, "Search the chest", time);

        DrawButton(ChestCardLayout.LootAllButton(screen), "LOOT ALL", contents is { IsEmpty: false }, ReadyGreen);
        DrawButton(ChestCardLayout.QuickStackButton(screen), "QUICK STACK", contents != null, AccentGold);

        var list = ChestCardLayout.List(screen);
        if (contents == null)
        {
            _ui.DrawTextCenteredFitted("Opening...", list.Center.X, list.Top + 12f, _ui.CountScale, TextDim, list.Size.X);
            return;
        }

        var rows = view.Rows();
        if (rows.Count == 0)
        {
            var text = contents.IsEmpty ? "The chest is empty" : "Nothing matches";
            _ui.DrawTextCenteredFitted(text, list.Center.X, list.Top + 12f, _ui.CountScale, TextDim, list.Size.X);
            return;
        }

        var offset = ChestCardLayout.ClampedOffset(view.ScrollOffset, rows.Count, screen);
        var (first, end) = ScrollList.VisibleRange(offset, ChestCardLayout.RowHeight, rows.Count, list.Size.Y);

        // The pass's one full-screen depth clear must happen before the scissor goes on (a clear honors it), and
        // nothing but the row loop may run while it is enabled
        _ui.EnsureDepthCleared();
        var (sx, sy, sw, sh) = UiCanvas.ScissorRect(new Vector2(list.Left, list.Top) + _ui.OverlayOffset, list.Size,
            _ui.Scale, _ui.WindowSize, _ui.FramebufferSize);
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(sx, sy, (uint)sw, (uint)sh);
        try
        {
            for (var i = first; i < end; i++)
                DrawRow(ChestCardLayout.RowRect(i, screen, offset), i, contents[rows[i]], hover, list, time, headingDegrees);
        }
        finally
        {
            _gl.Disable(EnableCap.ScissorTest);
        }
    }

    private void DrawButton(UiRect rect, string label, bool enabled, Rgba accent)
    {
        _ui.DrawRect(rect.Center, rect.Size,
            enabled ? _ui.IsHovered(rect.Center, rect.Size) ? ButtonHover : ChipBackground : SlotBackground);
        _ui.DrawBorder(rect.Center, rect.Size, Border.Standard, enabled ? accent : SlotBorderColor);
        _ui.DrawTextMiddle(label, rect.Center, _ui.CountScale, enabled ? Rgba.White : TextDim);
    }

    private void DrawRow(UiRect row, int index, ChestSlot slot, Vector2? hover, UiRect list, float time,
        float headingDegrees)
    {
        var hovered = hover is { } p && row.Contains(p) && list.Contains(p);
        _ui.DrawRect(row.Center, row.Size, hovered ? HoverBackground : SlotBackground);

        var style = RarityStyle.Of(Rarities.Of(slot.Id, slot.Instance));
        _ui.Chrome.DrawRow(row.Center, row.Size, style, time, index);
        _ui.DrawRect(new Vector2(row.Left + 2f, row.Center.Y), new Vector2(4f, row.Size.Y), style.AccentOr(SlotBorderColor));

        var iconCenter = new Vector2(row.Left + RowIcon * 0.5f + 10f, row.Center.Y);
        _ui.DrawIdIcon(iconCenter, RowIcon, slot.Id, headingDegrees, time);

        var count = slot.Instance != null ? "" : slot.Count.ToString();
        var countWidth = _ui.TextWidth(count, _ui.CountScale);
        var nameLeft = iconCenter.X + RowIcon * 0.5f + 8f;
        var nameWidth = row.Right - 8f - countWidth - nameLeft - 8f;
        _ui.DrawTextCenteredY(_ui.FitText(ChestView.NameOf(slot), _ui.CountScale, nameWidth), nameLeft, row.Center.Y,
            _ui.CountScale, style.Color);
        if (count.Length > 0)
            _ui.DrawTextCenteredY(count, row.Right - 8f - countWidth, row.Center.Y, _ui.CountScale, Rgba.White);
    }

    private static int Used(ChestState chest)
    {
        var used = 0;
        for (var i = 0; i < chest.SlotCount; i++)
            if (!chest[i].IsEmpty)
                used++;
        return used;
    }
}
