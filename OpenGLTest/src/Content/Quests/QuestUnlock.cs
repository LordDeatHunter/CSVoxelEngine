namespace VoxelEngine.Content.Quests;

/// <summary>
/// When a quest unlocks: shows its details and makes progress, so it can be earned. A quest may show earlier as a locked
/// preview (<see cref="QuestReveal"/>). The JSON names are the camelCase member names.
/// </summary>
public enum QuestUnlock
{
    /// <summary>Once every parent is done. The default; a root is always unlocked.</summary>
    AllParentsDone,

    /// <summary>Once any one parent is done.</summary>
    AnyParentDone,

    /// <summary>From the start, whatever its parents.</summary>
    Always,
}
