using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.UI.Widgets;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// Draws a <see cref="DropdownState"/>: the box with the chosen option and a <c>v</c> marker, and while open the
/// list of options on an opaque card (the hovered row lit, the chosen one in gold). Draw an open dropdown after
/// everything it may cover. Render thread only.
/// </summary>
internal static class DropdownRenderer
{
    private const float TextPad = 8f;
    private static readonly Rgba ListFill = Rgba.Gray(0.06f);

    /// <param name="selected">The chosen option, or -1 for none.</param>
    /// <param name="pointer">The pointer, for the hover look, or null.</param>
    /// <param name="scale">The text scale of the labels.</param>
    internal static void Draw(UIRenderer ui, DropdownState dropdown, int selected, Vector2? pointer, float scale,
        bool enabled = true)
    {
        var box = dropdown.Box;
        var hovered = enabled && pointer is { } p && box.Contains(p);
        ui.DrawRect(box.Center, box.Size, hovered || dropdown.IsOpen ? HoverBackground : SlotBackground);
        ui.DrawBorder(box.Center, box.Size, Border.Standard, dropdown.IsOpen ? SelectedBorderColor : SlotBorderColor);
        var label = selected >= 0 && selected < dropdown.Options.Count ? dropdown.Options[selected] : "";
        var arrowWidth = ui.TextWidth("v", scale);
        // A label wider than the room left of the marker shrinks to fit rather than running into it
        ui.DrawTextCenteredY(label, box.Left + TextPad, box.Center.Y,
            Fitted(ui, label, scale, box.Size.X - TextPad * 3f - arrowWidth), enabled ? Rgba.White : TextDim);
        ui.DrawTextCenteredY("v", box.Right - TextPad - arrowWidth, box.Center.Y, scale, TextDim);
        if (!dropdown.IsOpen) return;

        var list = dropdown.ListRect;
        ui.DrawRect(list.Center, list.Size, ListFill);
        for (var i = 0; i < dropdown.Options.Count; i++)
        {
            var row = dropdown.OptionRect(i);
            if (pointer is { } q && row.Contains(q)) ui.DrawRect(row.Center, row.Size, ButtonHover);
            ui.DrawTextCenteredY(dropdown.Options[i], row.Left + TextPad, row.Center.Y,
                Fitted(ui, dropdown.Options[i], scale, row.Size.X - TextPad * 2f), i == selected ? AccentGold : Rgba.White);
        }
        ui.DrawBorder(list.Center, list.Size, Border.Standard, SelectedBorderColor);
    }

    // The scale that fits text into room: the given one, or smaller for a label too wide
    private static float Fitted(UIRenderer ui, string text, float scale, float room)
    {
        var width = ui.TextWidth(text, scale);
        return width > room && width > 0f ? scale * room / width : scale;
    }
}
