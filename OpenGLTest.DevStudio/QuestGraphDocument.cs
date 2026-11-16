using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using VoxelEngine.Content;
using VoxelEngine.Content.Definitions;

namespace VoxelEngine.DevStudio;

/// <summary>
/// One quest graph file as the QUESTS tab edits it: its path, the <c>//</c> comment lines under the opening brace
/// (which <see cref="QuestGraphWriter"/> writes back), the mutable <see cref="QuestGraphDefinition"/> being edited,
/// and <see cref="Saved"/>, the graph as it is on disk, which a quest or the whole graph reverts to. Each edited
/// quest remembers the id it has on disk (<see cref="OriginalId"/>), so a renamed quest still finds its saved self.
/// </summary>
public sealed class QuestGraphDocument
{
    private readonly Dictionary<QuestGraphDefinition.QuestDefinition, string> _originalIds =
        new(ReferenceEqualityComparer.Instance);

    /// <param name="saved">Whether <paramref name="definition"/> is what the file holds (false for a graph not
    /// written yet).</param>
    public QuestGraphDocument(string path, QuestGraphDefinition definition, IReadOnlyList<string> header, bool saved)
    {
        Path = path;
        Definition = definition;
        Header = header;
        if (saved) MarkSaved();
    }

    public string Path { get; }

    /// <summary>The file name without its extension, the graph's title when it has none.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>The comment lines right under the opening brace, trimmed, each starting with <c>//</c>.</summary>
    public IReadOnlyList<string> Header { get; }

    public QuestGraphDefinition Definition { get; }

    /// <summary>A copy of the graph as the file holds it, or null when the file has not been written yet.</summary>
    public QuestGraphDefinition? Saved { get; private set; }

    /// <summary>Whether the graph differs from <see cref="Saved"/>; <see cref="QuestEditorState"/> keeps it current.</summary>
    public bool Dirty { get; internal set; }

    public string Title => string.IsNullOrWhiteSpace(Definition.Title) ? Name : Definition.Title;

    public Vector2 Origin => Definition.Origin is { Length: 2 } o ? new Vector2(o[0], o[1]) : Vector2.Zero;

    public List<QuestGraphDefinition.QuestDefinition> Quests => Definition.Quests ??= [];

    public static QuestGraphDocument Load(string path) =>
        new(path, ContentFiles.ReadJson<QuestGraphDefinition>(path), ReadHeader(File.ReadAllText(path)), true);

    /// <summary>The id <paramref name="quest"/> has on disk, or null for a quest never saved.</summary>
    public string? OriginalId(QuestGraphDefinition.QuestDefinition quest) => _originalIds.GetValueOrDefault(quest);

    /// <summary>The saved version of <paramref name="quest"/>, or null for a quest never saved.</summary>
    public QuestGraphDefinition.QuestDefinition? SavedQuest(QuestGraphDefinition.QuestDefinition quest) =>
        OriginalId(quest) is { } id ? Saved?.Quests?.Find(q => q.Id == id) : null;

    /// <summary>Records that the file now holds <paramref name="saved"/>, with <paramref name="quest"/> in it under
    /// its current id.</summary>
    internal void MarkQuestSaved(QuestGraphDefinition saved, QuestGraphDefinition.QuestDefinition quest)
    {
        Saved = saved;
        _originalIds[quest] = quest.Id!;
    }

    /// <summary>Records that the file now holds the graph exactly as edited.</summary>
    internal void MarkSaved()
    {
        Saved = Clone(Definition);
        _originalIds.Clear();
        foreach (var quest in Quests)
            if (quest.Id != null)
                _originalIds[quest] = quest.Id;
    }

    /// <summary>Puts <paramref name="replacement"/> in the place of <paramref name="quest"/>, standing for the saved
    /// quest <paramref name="originalId"/>.</summary>
    internal void Replace(QuestGraphDefinition.QuestDefinition quest, QuestGraphDefinition.QuestDefinition replacement,
        string originalId)
    {
        Quests[Quests.IndexOf(quest)] = replacement;
        _originalIds.Remove(quest);
        _originalIds[replacement] = originalId;
    }

    /// <summary>A deep copy, through the same JSON the files use.</summary>
    internal static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value), ContentFiles.JsonOptions)!;

    /// <summary>The <c>//</c> lines that follow the opening brace, up to the first line that is anything else.</summary>
    internal static List<string> ReadHeader(string json)
    {
        var header = new List<string>();
        var opened = false;
        foreach (var raw in json.Split('\n'))
        {
            var line = raw.Trim();
            if (!opened)
            {
                if (line == "{") opened = true;
                else if (line.Length > 0) break;
                continue;
            }
            if (!line.StartsWith("//")) break;
            header.Add(line);
        }
        return header;
    }
}
