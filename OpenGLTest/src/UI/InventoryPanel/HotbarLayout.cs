using System;
using System.Numerics;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// GL-free geometry of the hotbar strip centered along the bottom edge, in canvas units (see
/// <see cref="UiCanvas"/>). Shared by the hotbar draw, its hit-test and the inventory panel,
/// which ends above it, so the three never drift apart.
/// </summary>
public static class HotbarLayout
{
    public const float SlotSize = 56f;
    public const float SlotGap = 6f;
    public const float BottomMargin = 14f;

    /// <summary>The gap between the offhand slot and the strip's first slot; wider than <see cref="SlotGap"/> so the
    /// offhand reads as its own slot.</summary>
    public const float OffhandGap = 14f;

    /// <summary>Width of the whole strip, first slot's left edge to last slot's right edge.</summary>
    public static float RowWidth() =>
        Inventory.SlotCount * SlotSize + (Inventory.SlotCount - 1) * SlotGap;

    public static float FirstCenterX(Vector2 screen) =>
        (screen.X - RowWidth()) * 0.5f + SlotSize * 0.5f;

    public static float CenterY(Vector2 screen) => screen.Y - BottomMargin - SlotSize * 0.5f;

    /// <summary>Center of a hotbar slot, or of the offhand slot beside the strip's left end for
    /// <see cref="Inventory.OffhandIndex"/>.</summary>
    public static Vector2 SlotCenter(int index, Vector2 screen) => index == Inventory.OffhandIndex
        ? new Vector2(FirstCenterX(screen) - SlotSize - OffhandGap, CenterY(screen))
        : new Vector2(FirstCenterX(screen) + index * (SlotSize + SlotGap), CenterY(screen));

    /// <summary>Canvas Y of the slots' top edge.</summary>
    public static float Top(Vector2 screen) => screen.Y - BottomMargin - SlotSize;

    /// <summary>Hotbar slot under a canvas point, if any (<see cref="Inventory.OffhandIndex"/> for the offhand); the
    /// gaps between slots miss.</summary>
    public static int? SlotAt(Vector2 point, Vector2 screen)
    {
        for (var i = 0; i <= Inventory.OffhandIndex; i++)
        {
            var center = SlotCenter(i, screen);
            if (MathF.Abs(point.X - center.X) <= SlotSize * 0.5f
                && MathF.Abs(point.Y - center.Y) <= SlotSize * 0.5f)
                return i;
        }
        return null;
    }
}
