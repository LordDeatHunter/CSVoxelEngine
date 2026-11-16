using System.Collections.Generic;
using System.IO;
using System.Numerics;
using VoxelEngine.Content;
using VoxelEngine.Content.Definitions;

namespace VoxelEngine.DevStudio;

/// <summary>
/// One quest graph file as the QUESTS tab edits it: its path, the <c>//</c> comment lines under the opening brace
/// (which <see cref="QuestGraphWriter"/> writes back), the mutable <see cref="QuestGraphDefinition"/> the loader reads,
/// and whether it has unsaved edits.
/// </summary>
public sealed class QuestGraphDocument(string path, QuestGraphDefinition definition, IReadOnlyList<string> header)
{
    public string Path { get; } = path;

    /// <summary>The file name without its extension, the graph's title when it has none.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>The comment lines right under the opening brace, trimmed, each starting with <c>//</c>.</summary>
    public IReadOnlyList<string> Header { get; } = header;

    public QuestGraphDefinition Definition { get; } = definition;

    public bool Dirty { get; set; }

    public string Title => string.IsNullOrWhiteSpace(Definition.Title) ? Name : Definition.Title;

    public Vector2 Origin => Definition.Origin is { Length: 2 } o ? new Vector2(o[0], o[1]) : Vector2.Zero;

    public List<QuestGraphDefinition.QuestDefinition> Quests => Definition.Quests ??= [];

    public static QuestGraphDocument Load(string path) =>
        new(path, ContentFiles.ReadJson<QuestGraphDefinition>(path), ReadHeader(File.ReadAllText(path)));

    /// <summary>The <c>//</c> lines that follow the opening brace, up to the first line that is anything else.</summary>
    internal static List<string> ReadHeader(string json)
    {
        var header = new List<string>();
        var lines = json.Split('\n');
        var opened = false;
        foreach (var raw in lines)
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
