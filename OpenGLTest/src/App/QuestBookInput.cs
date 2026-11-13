using System;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.UI.Quests;

namespace VoxelEngine.App;

/// <summary>
/// The [P] quest book's input. P toggles it from a clean play state (no chat, pause menu, death screen or other
/// panel). While it is up the cursor is free, the look frozen, and the mouse is the book's:
/// <list type="bullet">
/// <item>a left press that travels <see cref="QuestConfig.DragThreshold"/> pans;</item>
/// <item>a release that never traveled selects the quest under the pointer, or clears the selection on empty
/// space (a click inside the details panel keeps it);</item>
/// <item>the wheel zooms about the pointer.</item>
/// </list>
/// P, E and Escape close it. The pan and zoom live in the local player's <see cref="Saves.QuestViewData"/>, so
/// they are where they were on the next open. Owned by <see cref="GameplayInput"/>; main thread only.
/// </summary>
internal sealed class QuestBookInput
{
    private readonly InputManager inputManager;
    private readonly RenderManager renderManager;
    private bool pWasPressed;

    internal QuestBookInput(InputManager inputManager, RenderManager renderManager)
    {
        this.inputManager = inputManager;
        this.renderManager = renderManager;
    }

    /// <summary>The book's state, which the UI pass draws.</summary>
    internal QuestBookView View { get; } = new();

    internal bool IsOpen => View.IsOpen;

    internal void Open(World w)
    {
        if (!w.IsPlaying || View.IsOpen) return;
        View.Open(w.LocalPlayer.QuestView);
        Capture(w, true);
    }

    /// <summary>Closes the book; <paramref name="recapture"/> false leaves the cursor to whatever is taking over.</summary>
    internal void Close(bool recapture = true)
    {
        if (!View.IsOpen) return;
        View.Close();
        if (recapture && Program.ActiveWorld is { } w) Capture(w, false);
    }

    /// <summary>The frame's polling: the P toggle (when <paramref name="mayToggle"/>), and while open the pointer and the pan drag.</summary>
    internal void Update(World w, bool mayToggle)
    {
        var pPressed = inputManager.Keyboard?.IsKeyPressed(Key.P) == true;
        if (pPressed && !pWasPressed && mayToggle)
        {
            if (View.IsOpen) Close();
            else Open(w);
        }
        pWasPressed = pPressed;

        if (!View.IsOpen || inputManager.Mouse == null) return;
        var pointer = renderManager.Ui.ToCanvas(inputManager.Mouse.Position);
        View.Hover = pointer;
        if (inputManager.Mouse.IsButtonPressed(MouseButton.Left)) View.TrackPress(pointer);
        else View.EndPress();
    }

    /// <summary>A left press starts tracking: a click until it travels, then a pan.</summary>
    internal void HandlePress(Vector2 windowPoint) => View.BeginPress(renderManager.Ui.ToCanvas(windowPoint));

    /// <summary>A left release: a press that never panned selects the quest under the pointer.</summary>
    internal void HandleRelease(Vector2 windowPoint, World w)
    {
        var dragged = View.Dragging;
        View.EndPress();
        if (dragged) return;
        var canvas = renderManager.Ui.ScreenSize;
        var point = renderManager.Ui.ToCanvas(windowPoint);
        if (View.Selected != null && QuestBookLayout.Details(canvas).Contains(point)) return;
        View.Selected = QuestBookLayout.NodeAt(w.LocalPlayer.Quests, View.Camera, canvas, point);
    }

    /// <summary>The wheel zooms about the pointer; <paramref name="wheelY"/> above 0 (toward the top) zooms in.</summary>
    internal void HandleScroll(Vector2 windowPoint, float wheelY)
    {
        if (wheelY == 0f) return;
        QuestBookLayout.ZoomAbout(View.Camera, renderManager.Ui.ToCanvas(windowPoint), renderManager.Ui.ScreenSize,
            MathF.Pow(QuestConfig.ZoomStep, wheelY));
    }

    /// <summary>Scenario seam: zooms by <paramref name="factor"/> about a canvas point (the canvas center when null).</summary>
    internal void ZoomAt(Vector2? canvasPoint, float factor)
    {
        var canvas = renderManager.Ui.ScreenSize;
        QuestBookLayout.ZoomAbout(View.Camera, canvasPoint ?? canvas * 0.5f, canvas, factor);
    }

    /// <summary>Scenario seam: pans as a drag of <paramref name="travel"/> canvas units would.</summary>
    internal void PanBy(Vector2 travel)
    {
        var pan = QuestBookLayout.PannedBy(new Vector2(View.Camera.PanX, View.Camera.PanY), travel, View.Camera.Zoom);
        View.Camera.PanX = pan.X;
        View.Camera.PanY = pan.Y;
    }

    private void Capture(World w, bool open)
    {
        w.Camera.SuppressLook = open;
        inputManager.Mouse?.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
    }
}
