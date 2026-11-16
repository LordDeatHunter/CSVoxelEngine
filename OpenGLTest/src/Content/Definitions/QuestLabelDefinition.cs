using System.Text.Json.Serialization;

namespace VoxelEngine.Content.Definitions;

/// <summary>
/// JSON shape of <c>assets/data/quests/labels/&lt;name&gt;.json</c>: one free text in the quest book, placed anywhere,
/// whose name is its file name. Compiled by <see cref="VoxelEngine.Content.Quests.QuestLoader"/>.
/// </summary>
public sealed class QuestLabelDefinition : IFileNamedDefinition
{
    /// <summary>The label's name: its file name.</summary>
    [JsonIgnore]
    public string? Id { get; set; }

    /// <summary>The text; a line break starts a new line. Required, not blank.</summary>
    public string? Text { get; set; }

    /// <summary>The text's center in the book, in grid steps (two numbers). Required.</summary>
    public float[]? Pos { get; set; }

    /// <summary><c>small</c>, <c>medium</c> or <c>large</c>; default <c>medium</c>.</summary>
    public string? Size { get; set; }

    /// <summary><c>gray</c>, <c>white</c>, <c>gold</c>, <c>green</c>, <c>red</c> or <c>blue</c>; default <c>gray</c>.</summary>
    public string? Color { get; set; }
}
