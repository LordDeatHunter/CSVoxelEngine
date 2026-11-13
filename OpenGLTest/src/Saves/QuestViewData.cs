namespace VoxelEngine.Saves;

/// <summary>Where the quest book was looking (its pan, in grid units, and its zoom factor) and which quest is equipped.</summary>
public sealed class QuestViewData
{
    public float PanX { get; set; }

    public float PanY { get; set; }

    public float Zoom { get; set; } = 1f;

    /// <summary>The id of the equipped quest, whose requirements show under the minimap, or null.</summary>
    public string? Equipped { get; set; }
}
