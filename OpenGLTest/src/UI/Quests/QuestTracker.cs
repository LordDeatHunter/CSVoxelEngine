using VoxelEngine.Content.Quests;
using VoxelEngine.Quests;
using VoxelEngine.Saves;

namespace VoxelEngine.UI.Quests;

/// <summary>
/// The equipped quest, the one whose requirements ride under the minimap while playing. One at a time
/// (<see cref="QuestViewData.Equipped"/>), only a visible, open quest whose requirements are readable can be
/// equipped, and finishing it unequips it. GL-free.
/// </summary>
internal static class QuestTracker
{
    /// <summary>Whether <paramref name="quest"/> may be equipped: shown in the book, not complete, requirements readable.</summary>
    public static bool CanEquip(QuestLog log, QuestType quest) =>
        log.IsVisible(quest) && !log.IsComplete(quest) && quest.Readable;

    /// <summary>Whether <paramref name="quest"/> is the equipped one.</summary>
    public static bool IsEquipped(QuestViewData view, QuestType quest) => view.Equipped == quest.Id;

    /// <summary>Equips <paramref name="quest"/> (replacing any other), or unequips it when it already is.</summary>
    public static void Toggle(QuestLog log, QuestViewData view, QuestType quest)
    {
        if (IsEquipped(view, quest)) view.Equipped = null;
        else if (CanEquip(log, quest)) view.Equipped = quest.Id;
    }

    /// <summary>The quest to show under the minimap, or null. A saved id that no longer names an open quest
    /// (finished, renamed away) is cleared here, which is the auto-unequip on completion.</summary>
    public static QuestType? Current(QuestLog log, QuestViewData view)
    {
        if (view.Equipped is not { } id) return null;
        if (log.Book.TryGet(id, out var quest) && CanEquip(log, quest)) return quest;
        view.Equipped = null;
        return null;
    }
}
