using System.Text.Json;
using VoxelEngine.Content;
using VoxelEngine.Content.Definitions;

namespace VoxelEngine.DevStudio;

/// <summary>
/// One content file as the QUESTS tab edits it (a quest, a label): the definition being edited (its
/// <see cref="IFileNamedDefinition.Id"/> is its file name once saved), and <see cref="Saved"/>, the copy its file holds
/// under <see cref="SavedId"/> (both null for one never saved). A rename keeps <see cref="SavedId"/> until the save
/// writes the new file and deletes the old one; a deleted one that has a file stays, <see cref="Deleted"/>, until the
/// save deletes the file.
/// </summary>
public class EditorDocument<T>(T value, T? saved, string? savedId) where T : class, IFileNamedDefinition
{
    public T Value { get; private set; } = value;

    public string Id => Value.Id!;

    /// <summary>A copy of the definition as its file holds it, or null when it has no file yet.</summary>
    public T? Saved { get; private set; } = saved;

    /// <summary>The id (file name) it has on disk, or null when it has no file yet.</summary>
    public string? SavedId { get; private set; } = savedId;

    /// <summary>Deleted in the editor, its file still on disk until the next save.</summary>
    public bool Deleted { get; internal set; }

    /// <summary>Whether it differs from its file; <see cref="QuestEditorState"/> keeps it current.</summary>
    public bool Dirty { get; internal set; }

    /// <summary>Records that the file now holds the definition exactly as edited, under its current id.</summary>
    internal void MarkSaved()
    {
        Saved = Clone(Value);
        SavedId = Id;
    }

    /// <summary>Puts the saved copy back as the definition being edited.</summary>
    internal void Revert()
    {
        if (Saved != null) Value = Clone(Saved);
    }

    /// <summary>A deep copy, through the same JSON the files use, keeping the id the JSON leaves out.</summary>
    internal static T Clone(T value)
    {
        var copy = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value), ContentFiles.JsonOptions)!;
        copy.Id = value.Id;
        return copy;
    }
}
