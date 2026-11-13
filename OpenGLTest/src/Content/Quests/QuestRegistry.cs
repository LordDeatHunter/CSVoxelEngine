namespace VoxelEngine.Content.Quests;

/// <summary>
/// The published <see cref="QuestBook"/>, set once by <see cref="ContentRegistry.Load"/> before the
/// <see cref="World"/> exists, then read-only.
/// </summary>
public static class QuestRegistry
{
    /// <summary>The loaded quests; empty before content loads.</summary>
    public static QuestBook Book { get; private set; } = QuestBook.Empty;

    internal static void Publish(QuestBook book) => Book = book;

    internal static void Reset() => Book = QuestBook.Empty;
}
