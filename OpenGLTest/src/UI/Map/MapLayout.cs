using System.Numerics;
using VoxelEngine.UI.Widgets;

namespace VoxelEngine.UI.Map;

/// <summary>
/// Where the maps put everything, shared by their renderers and the [M] screen's input. Both maps are north-up: world
/// x runs right and world z runs down (north at the top), so a world point lands at the view's center plus its offset
/// from the view's world center times the zoom (canvas units per block). Also the minimap's box in the top-right
/// corner, the rim a far pin is held to, and the map screen's buttons. GL-free.
/// </summary>
internal static class MapLayout
{
    private const float Margin = 24f;
    private const float ButtonHeight = 40f;
    private const float ButtonGap = 12f;
    private static readonly Vector2 CenterButtonSize = new(130f, ButtonHeight);
    private static readonly Vector2 ShapeButtonSize = new(300f, ButtonHeight);

    /// <summary>The canvas point world XZ <paramref name="world"/> is drawn at.</summary>
    public static Vector2 ToCanvas(Vector2 world, Vector2 center, float zoom, Vector2 viewCenter) =>
        viewCenter + (world - center) * zoom;

    /// <summary>The world XZ under canvas point <paramref name="point"/> of a full-canvas view.</summary>
    public static Vector2 ToWorld(Vector2 point, Vector2 center, float zoom, Vector2 canvas) =>
        center + (point - canvas * 0.5f) / zoom;

    /// <summary>The view center that moves the map by a pointer's travel from <paramref name="center"/> (the map
    /// follows the pointer).</summary>
    public static Vector2 PannedBy(Vector2 center, Vector2 travel, float zoom) => center - travel / zoom;

    /// <summary>The minimap's box under the top-right corner of the canvas.</summary>
    public static UiRect Minimap(Vector2 canvas)
    {
        const float size = MapConfig.MinimapSize;
        return new UiRect(new Vector2(canvas.X - MapConfig.MinimapMargin - size * 0.5f,
            MapConfig.MinimapMargin + size * 0.5f), new Vector2(size));
    }

    /// <summary>
    /// <paramref name="point"/> held inside the minimap's <paramref name="box"/> shrunk by <paramref name="inset"/>: a
    /// round map pulls it back along its ray from the center to the circle, a square one clamps each axis. Returns
    /// whether it had to move (the pin lies beyond the map's edge).
    /// </summary>
    public static bool ClampToRim(ref Vector2 point, UiRect box, bool round, float inset)
    {
        var half = box.Size.X * 0.5f - inset;
        var offset = point - box.Center;
        if (round)
        {
            var length = offset.Length();
            if (length <= half) return false;
            point = box.Center + offset * (half / length);
            return true;
        }
        var clamped = Vector2.Clamp(offset, new Vector2(-half), new Vector2(half));
        if (clamped == offset) return false;
        point = box.Center + clamped;
        return true;
    }

    /// <summary>The CENTER button, top-right of the map screen.</summary>
    public static UiRect CenterButton(Vector2 canvas) =>
        new(new Vector2(canvas.X - Margin - CenterButtonSize.X * 0.5f, Margin + ButtonHeight * 0.5f), CenterButtonSize);

    /// <summary>The minimap shape toggle, left of CENTER.</summary>
    public static UiRect ShapeButton(Vector2 canvas)
    {
        var center = CenterButton(canvas);
        return new UiRect(new Vector2(center.Left - ButtonGap - ShapeButtonSize.X * 0.5f, center.Center.Y),
            ShapeButtonSize);
    }

    /// <summary>The title line's left edge and center height, top-left of the canvas.</summary>
    public static Vector2 Header => new(Margin, Margin + ButtonHeight * 0.5f);

    /// <summary>The controls hint line's left edge and center height, bottom-left of the canvas.</summary>
    public static Vector2 Hint(Vector2 canvas) => new(Margin, canvas.Y - Margin - 8f);

    /// <summary>The pointer readout's right edge and center height, bottom-right of the canvas.</summary>
    public static Vector2 Readout(Vector2 canvas) => new(canvas.X - Margin, canvas.Y - Margin - 8f);

    /// <summary>Whether a press at <paramref name="point"/> lands on a button rather than the map.</summary>
    public static bool OnButton(Vector2 point, Vector2 canvas) =>
        CenterButton(canvas).Contains(point) || ShapeButton(canvas).Contains(point);
}
