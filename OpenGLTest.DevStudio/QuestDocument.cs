using System.IO;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Quests;

namespace VoxelEngine.DevStudio;

/// <summary>One quest file being edited: <c>&lt;id&gt;.json</c> in the quest folder (see <see cref="EditorDocument{T}"/>).</summary>
public sealed class QuestDocument(QuestDefinition quest, QuestDefinition? saved, string? savedId)
    : EditorDocument<QuestDefinition>(quest, saved, savedId)
{
    public QuestDefinition Quest => Value;

    public static QuestDocument Load(string path)
    {
        var quest = QuestLoader.ReadQuest(path);
        return new QuestDocument(quest, Clone(quest), quest.Id);
    }

    /// <summary>The file of quest <paramref name="id"/> in <paramref name="folder"/>.</summary>
    public static string PathOf(string folder, string id) => Path.Combine(folder, id + ".json");
}
