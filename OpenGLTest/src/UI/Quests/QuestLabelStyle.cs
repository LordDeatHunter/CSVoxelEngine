using VoxelEngine.Content.Quests;
using VoxelEngine.Data;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.UI.Quests;

/// <summary>How a <see cref="QuestLabel"/> looks in the quest book at zoom 1: its text's cap height in canvas units, the
/// step from one line to the next, and its color. GL-free; <c>QuestBookRenderer</c> draws with it.</summary>
internal static class QuestLabelStyle
{
    /// <summary>The cap height of the label's text at zoom 1.</summary>
    public static float CapHeight(QuestLabelSize size) => size switch
    {
        QuestLabelSize.Small => 10f,
        QuestLabelSize.Large => 22f,
        _ => 14f,
    };

    /// <summary>From one line's center to the next.</summary>
    public static float LineStep(float capHeight) => capHeight * 1.7f;

    public static Rgba ColorOf(QuestLabelColor color) => color switch
    {
        QuestLabelColor.White => Rgba.White,
        QuestLabelColor.Gold => AccentGold,
        QuestLabelColor.Green => ReadyGreen,
        QuestLabelColor.Red => DangerRed,
        QuestLabelColor.Blue => LinkBlue,
        _ => TextDim,
    };
}
