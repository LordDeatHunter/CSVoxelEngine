using System.Collections.Generic;
using VoxelEngine.Content.Definitions;

namespace VoxelEngine.DevStudio;

/// <summary>
/// The quest editor at one point of its history: every quest as edited (deep copies, never touched again) with the id
/// its file has on disk and whether it was deleted, and the selection (every selected quest, or a link).
/// <see cref="Text"/> is the quests as they would be written, which is how <see cref="QuestEditHistory"/> tells an edit
/// from a refused or selection-only change.
/// </summary>
public sealed record QuestEditSnapshot(
    string Text,
    IReadOnlyList<QuestEditSnapshot.Entry> Quests,
    IReadOnlyList<string> SelectedQuests,
    (string Child, string Parent)? SelectedLink)
{
    /// <summary>One quest document: a copy of the quest (its id included), the id its file had on disk then (null for a
    /// quest never saved), and whether it was deleted.</summary>
    public sealed record Entry(QuestDefinition Quest, string? SavedId, bool Deleted);
}
