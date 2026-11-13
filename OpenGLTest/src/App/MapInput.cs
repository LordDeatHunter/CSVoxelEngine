using System;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.UI;
using VoxelEngine.UI.Map;

namespace VoxelEngine.App;

/// <summary>
/// The [M] map screen's input. M toggles it from a clean play state (no chat, pause menu, death screen or other
/// panel). While it is up the cursor is free, the look frozen, and the mouse is the map's:
/// <list type="bullet">
/// <item>a left press that travels <see cref="MapConfig.DragThreshold"/> pans (and stops the view following the
/// player);</item>
/// <item>a release that never traveled presses the button under the pointer: CENTER back on the player at the
/// default zoom, or the minimap's shape (square or round, saved with the settings);</item>
/// <item>the wheel zooms about the pointer.</item>
/// </list>
/// M, E and Escape close it. Owned by <see cref="GameplayInput"/>; main thread only.
/// </summary>
internal sealed class MapInput
{
    private readonly InputManager inputManager;
    private readonly RenderManager renderManager;
    private bool mWasPressed;

    internal MapInput(InputManager inputManager, RenderManager renderManager)
    {
        this.inputManager = inputManager;
        this.renderManager = renderManager;
    }

    /// <summary>The screen's state, which the UI pass draws.</summary>
    internal MapScreenView View { get; } = new();

    internal bool IsOpen => View.IsOpen;

    internal void Open(World w)
    {
        if (!w.IsPlaying || View.IsOpen) return;
        View.Open();
        Capture(w, true);
    }

    /// <summary>Closes the screen; <paramref name="recapture"/> false leaves the cursor to whatever is taking over.</summary>
    internal void Close(bool recapture = true)
    {
        if (!View.IsOpen) return;
        View.Close();
        if (recapture && Program.ActiveWorld is { } w) Capture(w, false);
    }

    /// <summary>The frame's polling: the M toggle (when <paramref name="mayToggle"/>), and while open the pointer and the pan drag.</summary>
    internal void Update(World w, bool mayToggle)
    {
        var mPressed = inputManager.Keyboard?.IsKeyPressed(Key.M) == true;
        if (mPressed && !mWasPressed && mayToggle)
        {
            if (View.IsOpen) Close();
            else Open(w);
        }
        mWasPressed = mPressed;

        if (!View.IsOpen || inputManager.Mouse == null) return;
        var pointer = renderManager.Ui.ToCanvas(inputManager.Mouse.Position);
        View.Hover = pointer;
        if (inputManager.Mouse.IsButtonPressed(MouseButton.Left)) View.TrackPress(pointer);
        else View.EndPress();
    }

    /// <summary>A left press starts tracking: a click until it travels, then a pan. A press on a button never pans.</summary>
    internal void HandlePress(Vector2 windowPoint)
    {
        var point = renderManager.Ui.ToCanvas(windowPoint);
        if (MapLayout.OnButton(point, renderManager.Ui.ScreenSize)) return;
        View.BeginPress(point);
    }

    /// <summary>A left release: a press that never panned presses the button under the pointer.</summary>
    internal void HandleRelease(Vector2 windowPoint)
    {
        var dragged = View.Dragging;
        View.EndPress();
        if (dragged) return;
        Click(renderManager.Ui.ToCanvas(windowPoint));
    }

    /// <summary>The wheel zooms about the pointer; <paramref name="wheelY"/> above 0 (toward the top) zooms in.</summary>
    internal void HandleScroll(Vector2 windowPoint, float wheelY)
    {
        if (wheelY == 0f) return;
        View.ZoomAbout(renderManager.Ui.ToCanvas(windowPoint), renderManager.Ui.ScreenSize, MathF.Pow(MapConfig.ZoomStep, wheelY));
    }

    /// <summary>Scenario seam: zooms by <paramref name="factor"/> about a canvas point (the canvas center when null).</summary>
    internal void ZoomAt(Vector2? canvasPoint, float factor)
    {
        var canvas = renderManager.Ui.ScreenSize;
        View.ZoomAbout(canvasPoint ?? canvas * 0.5f, canvas, factor);
    }

    /// <summary>Scenario seam: pans as a drag of <paramref name="travel"/> canvas units would.</summary>
    internal void PanBy(Vector2 travel) => View.PanTo(MapLayout.PannedBy(View.Center, travel, View.Zoom));

    /// <summary>CENTER: back on the player at the default zoom.</summary>
    internal void Recenter() => View.Recenter();

    /// <summary>Sets the minimap's shape and saves the settings.</summary>
    internal void SetMinimapRound(bool round)
    {
        if (GameSettings.MinimapRound == round) return;
        GameSettings.MinimapRound = round;
        GameSettings.Save();
    }

    private void Click(Vector2 point)
    {
        var canvas = renderManager.Ui.ScreenSize;
        if (MapLayout.CenterButton(canvas).Contains(point)) Recenter();
        else if (MapLayout.ShapeButton(canvas).Contains(point)) SetMinimapRound(!GameSettings.MinimapRound);
    }

    private void Capture(World w, bool open)
    {
        w.Camera.SuppressLook = open;
        inputManager.Mouse?.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
    }
}
