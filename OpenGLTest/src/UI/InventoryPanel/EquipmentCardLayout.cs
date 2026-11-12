using System;
using System.Numerics;
using VoxelEngine.Config;
using VoxelEngine.UI.Widgets;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// GL-free geometry of the equipment card the [E] panel's Gear tab shows on its right: a header, one row per
/// equipment slot (armor first, then accessories) and the summed stats below. The draw in
/// <c>InventoryPanelRenderer</c> and the click handling in <c>InventoryPanelInput</c> read this one definition,
/// so a click lands on exactly the slot that was drawn. Canvas units (see <see cref="UiCanvas"/>).
/// </summary>
public static class EquipmentCardLayout
{
    public const float Width = 420f;

    /// <summary>Width of the slot label column of a row; the worn piece's icon and name start after it.</summary>
    public const float LabelColumnWidth = 132f;
    public const float HeaderHeight = 30f;
    public const float SlotRowHeight = 28f;
    public const float StatLineHeight = 16f;
    public const float StatsGap = 10f;

    /// <summary>The stat lines the card has room for under the slots.</summary>
    public const int StatLines = 7;

    /// <summary>The card: right of the list panel like the detail card, tall enough for every slot and the stats.</summary>
    public static UiRect Card(Vector2 screen)
    {
        var detail = InventoryPanelLayout.DetailCardRect(screen);
        var frame = PanelFrameLayout.Standard(new UiRect(Vector2.Zero, Vector2.Zero));
        var height = frame.BorderWidth * 2f + frame.AccentHeight + frame.Padding * 2f + HeaderHeight
            + GearConfig.EquipmentSlotCount * SlotRowHeight + StatsGap + StatLines * StatLineHeight;
        var left = detail.Center.X - detail.Size.X * 0.5f;
        left = MathF.Min(left, screen.X - InventoryPanelLayout.PanelRightMargin - Width);
        return UiRect.FromTopLeft(new Vector2(left, InventoryPanelLayout.PanelTop), new Vector2(Width, height));
    }

    /// <summary>The row of equipment slot <paramref name="slot"/>.</summary>
    public static UiRect SlotRow(int slot, Vector2 screen)
    {
        var content = PanelFrameLayout.Standard(Card(screen)).Content;
        return UiRect.FromTopLeft(
            new Vector2(content.Left, content.Top + HeaderHeight + slot * SlotRowHeight),
            new Vector2(content.Size.X, SlotRowHeight - 2f));
    }

    /// <summary>The equipment slot under a canvas point, or null.</summary>
    public static int? SlotAt(Vector2 point, Vector2 screen)
    {
        for (var slot = 0; slot < GearConfig.EquipmentSlotCount; slot++)
            if (SlotRow(slot, screen).Contains(point))
                return slot;
        return null;
    }

    /// <summary>Top-left of the stats block under the slots.</summary>
    public static Vector2 StatsTopLeft(Vector2 screen)
    {
        var content = PanelFrameLayout.Standard(Card(screen)).Content;
        return new Vector2(content.Left,
            content.Top + HeaderHeight + GearConfig.EquipmentSlotCount * SlotRowHeight + StatsGap);
    }
}
