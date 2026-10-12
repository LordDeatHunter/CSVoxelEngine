using System;
using System.Numerics;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// GL-free geometry of the hotbar strip centred along the bottom edge, in canvas units (see
/// <see cref="UiCanvas"/>). Shared by the hotbar draw, its hit-test and the inventory panel,
/// which ends above it, so the three never drift apart.
/// </summary>
public static class HotbarLayout
{
    public const float SlotSize = 56f;
    public const float SlotGap = 6f;
    public const float BottomMargin = 14f;

    /// <summary>Width of the whole strip, first slot's left edge to last slot's right edge.</summary>
    public static float RowWidth() =>
        Inventory.SlotCount * SlotSize + (Inventory.SlotCount - 1) * SlotGap;

    public static float FirstCentreX(Vector2 screen) =>
        (screen.X - RowWidth()) * 0.5f + SlotSize * 0.5f;

    public static float CentreY(Vector2 screen) => screen.Y - BottomMargin - SlotSize * 0.5f;

    public static Vector2 SlotCentre(int index, Vector2 screen) =>
        new(FirstCentreX(screen) + index * (SlotSize + SlotGap), CentreY(screen));

    /// <summary>Canvas Y of the slots' top edge.</summary>
    public static float Top(Vector2 screen) => screen.Y - BottomMargin - SlotSize;

    /// <summary>Hotbar slot under a canvas point, if any; the gaps between slots miss.</summary>
    public static int? SlotAt(Vector2 point, Vector2 screen)
    {
        for (var i = 0; i < Inventory.SlotCount; i++)
        {
            var centre = SlotCentre(i, screen);
            if (MathF.Abs(point.X - centre.X) <= SlotSize * 0.5f
                && MathF.Abs(point.Y - centre.Y) <= SlotSize * 0.5f)
                return i;
        }
        return null;
    }
}
