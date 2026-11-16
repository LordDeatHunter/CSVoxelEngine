using System.Collections.Generic;
using VoxelEngine.Content.Definitions;

namespace VoxelEngine.DevStudio;

/// <summary>
/// The quest editor at one point of its history: every graph as edited (deep copies, never touched again), the
/// on-disk id each quest stood for, and the selection. <see cref="Text"/> is the graphs as they would be written, which
/// is how <see cref="QuestEditHistory"/> tells an edit from a refused or selection-only change.
/// </summary>
public sealed record QuestEditSnapshot(
    string Text,
    IReadOnlyList<QuestEditSnapshot.Graph> Graphs,
    string? SelectedQuest,
    (string Child, string Parent)? SelectedLink,
    int SelectedGraph)
{
    /// <summary>One graph document: its file, header comment, a copy of its definition and, by quest index, the id
    /// each quest has on disk (null for a quest never saved).</summary>
    public sealed record Graph(
        string Path,
        IReadOnlyList<string> Header,
        QuestGraphDefinition Definition,
        IReadOnlyList<string?> OriginalIds);
}
