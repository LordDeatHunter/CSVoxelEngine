namespace VoxelEngine.Saves;

/// <summary>Where the quest book was looking: its pan, in grid units, and its zoom factor.</summary>
public sealed class QuestViewData
{
    public float PanX { get; set; }

    public float PanY { get; set; }

    public float Zoom { get; set; } = 1f;
}
