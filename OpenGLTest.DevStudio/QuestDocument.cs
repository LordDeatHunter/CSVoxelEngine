using System.IO;
using System.Text.Json;
using VoxelEngine.Content;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Quests;

namespace VoxelEngine.DevStudio;

/// <summary>
/// One quest as the QUESTS tab edits it: the quest being edited (its <see cref="QuestDefinition.Id"/> is its file name
/// once saved), and <see cref="Saved"/>, the copy its file holds under <see cref="SavedId"/> (both null for a quest never
/// saved). A rename keeps <see cref="SavedId"/> until the save writes the new file and deletes the old one; a deleted
/// quest that has a file stays here, <see cref="Deleted"/>, until the save deletes the file.
/// </summary>
public sealed class QuestDocument(QuestDefinition quest, QuestDefinition? saved, string? savedId)
{
    public QuestDefinition Quest { get; private set; } = quest;

    public string Id => Quest.Id!;

    /// <summary>A copy of the quest as its file holds it, or null when it has no file yet.</summary>
    public QuestDefinition? Saved { get; private set; } = saved;

    /// <summary>The id (file name) the quest has on disk, or null when it has no file yet.</summary>
    public string? SavedId { get; private set; } = savedId;

    /// <summary>Deleted in the editor, its file still on disk until the next save.</summary>
    public bool Deleted { get; internal set; }

    /// <summary>Whether the quest differs from its file; <see cref="QuestEditorState"/> keeps it current.</summary>
    public bool Dirty { get; internal set; }

    public static QuestDocument Load(string path)
    {
        var quest = QuestLoader.ReadQuest(path);
        return new QuestDocument(quest, Clone(quest), quest.Id);
    }

    /// <summary>Records that the file now holds the quest exactly as edited, under its current id.</summary>
    internal void MarkSaved()
    {
        Saved = Clone(Quest);
        SavedId = Id;
    }

    /// <summary>Puts the saved copy back as the quest being edited.</summary>
    internal void Revert()
    {
        if (Saved != null) Quest = Clone(Saved);
    }

    /// <summary>A deep copy, through the same JSON the files use, keeping the id the JSON leaves out.</summary>
    internal static QuestDefinition Clone(QuestDefinition quest)
    {
        var copy = JsonSerializer.Deserialize<QuestDefinition>(JsonSerializer.Serialize(quest), ContentFiles.JsonOptions)!;
        copy.Id = quest.Id;
        return copy;
    }

    /// <summary>The file of <paramref name="id"/> in <paramref name="folder"/>.</summary>
    public static string PathOf(string folder, string id) => Path.Combine(folder, id + ".json");
}
