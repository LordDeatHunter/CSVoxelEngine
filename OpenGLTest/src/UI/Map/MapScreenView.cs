using System;
using System.Numerics;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.UI.Map;

/// <summary>
/// The [M] map screen's state: whether it is open, its open/close fade, the world XZ at the canvas center and the zoom
/// (canvas units per block), the pointer, and the press being tracked (a click until it travels
/// <see cref="MapConfig.DragThreshold"/>, then a pan). Until the player pans it, the view follows the player
/// (<see cref="Following"/>); CENTER goes back to that at the default zoom. The view survives closing and reopening
/// within a world. GL-free; main thread only.
/// </summary>
internal sealed class MapScreenView
{
    public bool IsOpen { get; private set; }

    public PanelTransition Transition { get; } = new();

    /// <summary>The world XZ (x east, y south) drawn at the canvas center.</summary>
    public Vector2 Center { get; set; }

    /// <summary>Canvas units per block, within <see cref="MapConfig.MinZoom"/>..<see cref="MapConfig.MaxZoom"/>.</summary>
    public float Zoom { get; private set; } = MapConfig.DefaultZoom;

    /// <summary>Whether the view stays centered on the player: true until a pan, and again after CENTER.</summary>
    public bool Following { get; private set; } = true;

    /// <summary>The pointer in canvas units, or null when it is off the window.</summary>
    public Vector2? Hover { get; set; }

    /// <summary>Where the current press went down, in canvas units; null with no press.</summary>
    public Vector2? PressAt { get; private set; }

    private Vector2 _pressCenter;

    /// <summary>Whether the current press has traveled far enough to be a pan rather than a click.</summary>
    public bool Dragging { get; private set; }

    public void Open()
    {
        IsOpen = true;
        EndPress();
    }

    public void Close()
    {
        IsOpen = false;
        EndPress();
    }

    /// <summary>Closes the screen at once and forgets its view: the next world opens on its player.</summary>
    public void ResetForNewWorld()
    {
        Close();
        Recenter();
        Transition.Reset();
    }

    /// <summary>CENTER: back on the player (who the next frame's follow puts at the center) at the default zoom.</summary>
    public void Recenter()
    {
        Following = true;
        Zoom = MapConfig.DefaultZoom;
    }

    /// <summary>Keeps the view on the player while it follows them.</summary>
    public void Follow(Vector2 player)
    {
        if (Following) Center = player;
    }

    public void BeginPress(Vector2 canvas)
    {
        PressAt = canvas;
        _pressCenter = Center;
        Dragging = false;
    }

    /// <summary>Follows the held pointer: past the threshold the press pans the map by the pointer's travel.</summary>
    public void TrackPress(Vector2 canvas)
    {
        if (PressAt is not { } start) return;
        if (!Dragging && Vector2.Distance(start, canvas) < MapConfig.DragThreshold) return;
        Dragging = true;
        PanTo(MapLayout.PannedBy(_pressCenter, canvas - start, Zoom));
    }

    public void EndPress()
    {
        PressAt = null;
        Dragging = false;
    }

    /// <summary>Moves the view to <paramref name="center"/>, which stops it following the player.</summary>
    public void PanTo(Vector2 center)
    {
        Center = center;
        Following = false;
    }

    /// <summary>Multiplies the zoom by <paramref name="factor"/> (clamped to the zoom range) about canvas point
    /// <paramref name="point"/>: the world point under it stays under it. Zooming about the center keeps following.</summary>
    public void ZoomAbout(Vector2 point, Vector2 canvas, float factor)
    {
        var anchor = MapLayout.ToWorld(point, Center, Zoom, canvas);
        Zoom = Math.Clamp(Zoom * factor, MapConfig.MinZoom, MapConfig.MaxZoom);
        var center = anchor - (point - canvas * 0.5f) / Zoom;
        if (Vector2.DistanceSquared(center, Center) > 1e-6f) PanTo(center);
    }
}
