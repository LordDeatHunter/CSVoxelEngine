using System.Numerics;
using VoxelEngine.Content.Quests;
using VoxelEngine.Saves;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.UI.Quests;

/// <summary>
/// The [P] quest book's screen state: whether it is open, its open/close fade, the quest whose details show,
/// the pointer, and the press being tracked (a click until it travels <see cref="QuestConfig.DragThreshold"/>,
/// then a pan). The pan and zoom themselves are the world's (<see cref="Camera"/>, the local player's
/// <see cref="QuestViewData"/>), so they survive closing and, in singleplayer, saving. GL-free; main thread only.
/// </summary>
internal sealed class QuestBookView
{
    public bool IsOpen { get; private set; }

    public PanelTransition Transition { get; } = new();

    /// <summary>The pan and zoom the book draws with, bound on open to the world's.</summary>
    public QuestViewData Camera { get; private set; } = new();

    /// <summary>The quest whose details panel shows, or null.</summary>
    public QuestType? Selected { get; set; }

    /// <summary>The pointer in canvas units, or null when it is off the window.</summary>
    public Vector2? Hover { get; set; }

    /// <summary>Where the current press went down, in canvas units, and the pan at that moment; null with no press.</summary>
    public Vector2? PressAt { get; private set; }

    private Vector2 _pressPan;

    /// <summary>Whether the current press has traveled far enough to be a pan rather than a click.</summary>
    public bool Dragging { get; private set; }

    public void Open(QuestViewData camera)
    {
        Camera = camera;
        IsOpen = true;
        EndPress();
    }

    public void Close()
    {
        IsOpen = false;
        EndPress();
    }

    /// <summary>Closes the book at once with nothing selected: the next world binds its own view on open.</summary>
    public void ResetForNewWorld()
    {
        Close();
        Selected = null;
        Camera = new QuestViewData();
        Transition.Reset();
    }

    public void BeginPress(Vector2 canvas)
    {
        PressAt = canvas;
        _pressPan = new Vector2(Camera.PanX, Camera.PanY);
        Dragging = false;
    }

    /// <summary>Follows the held pointer: past the threshold the press pans the book by the pointer's travel.</summary>
    public void TrackPress(Vector2 canvas)
    {
        if (PressAt is not { } start) return;
        if (!Dragging && Vector2.Distance(start, canvas) < QuestConfig.DragThreshold) return;
        Dragging = true;
        var pan = QuestBookLayout.PannedBy(_pressPan, canvas - start, Camera.Zoom);
        Camera.PanX = pan.X;
        Camera.PanY = pan.Y;
    }

    public void EndPress()
    {
        PressAt = null;
        Dragging = false;
    }
}
