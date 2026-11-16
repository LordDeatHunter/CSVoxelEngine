using System;
using System.Collections.Generic;

namespace VoxelEngine.Content.Quests;

/// <summary>
/// Every compiled quest and the graph titles they belong to, read-only once compiled. The registry
/// publishes one; tests build their own through <see cref="QuestLoader.Compile"/> and hand it to a
/// <see cref="VoxelEngine.Quests.QuestLog"/> directly.
/// </summary>
public sealed class QuestBook
{
    private readonly Dictionary<string, QuestType> _byId;

    public QuestBook(IReadOnlyList<QuestType> quests, IReadOnlyList<QuestLabel>? labels = null)
    {
        Quests = quests;
        Labels = labels ?? [];
        _byId = new Dictionary<string, QuestType>(StringComparer.Ordinal);
        foreach (var quest in quests) _byId[quest.Id] = quest;
    }

    /// <summary>Every free text label drawn in the book.</summary>
    public IReadOnlyList<QuestLabel> Labels { get; }

    /// <summary>A book with no quests.</summary>
    public static QuestBook Empty { get; } = new([]);

    /// <summary>Every quest, indexed by <see cref="QuestType.Index"/>.</summary>
    public IReadOnlyList<QuestType> Quests { get; }

    /// <summary>The quest with that id, if any.</summary>
    public bool TryGet(string id, out QuestType quest) => _byId.TryGetValue(id, out quest!);
}
