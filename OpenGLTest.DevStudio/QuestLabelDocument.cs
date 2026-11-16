using System.IO;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Quests;

namespace VoxelEngine.DevStudio;

/// <summary>One quest book label file being edited: <c>labels/&lt;name&gt;.json</c> in the quest folder (see
/// <see cref="EditorDocument{T}"/>).</summary>
public sealed class QuestLabelDocument(QuestLabelDefinition label, QuestLabelDefinition? saved, string? savedId)
    : EditorDocument<QuestLabelDefinition>(label, saved, savedId)
{
    public QuestLabelDefinition Label => Value;

    public static QuestLabelDocument Load(string path)
    {
        var label = QuestLoader.ReadLabel(path);
        return new QuestLabelDocument(label, Clone(label), label.Id);
    }

    /// <summary>The file of label <paramref name="name"/> under <paramref name="folder"/> (the quest folder).</summary>
    public static string PathOf(string folder, string name) => Path.Combine(folder, QuestLoader.LabelFolder, name + ".json");
}
