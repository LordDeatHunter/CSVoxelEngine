namespace VoxelEngine.Content.Quests;

/// <summary>
/// When a quest shows in the book before it unlocks: as a locked preview (title only, its details hidden). A quest
/// still unlocks, and only then makes progress, once every parent is done; this only decides how early it is seen.
/// The JSON names are the camelCase member names.
/// </summary>
public enum QuestReveal
{
    /// <summary>Not before it unlocks (every parent done). The default.</summary>
    Unlocked,

    /// <summary>Always, from the start.</summary>
    Always,

    /// <summary>While at least one parent shows, done or not.</summary>
    AnyParentShown,

    /// <summary>While every parent shows, done or not.</summary>
    AllParentsShown,

    /// <summary>Once any one parent is done.</summary>
    AnyParentDone,

    /// <summary>Once any one parent is done and every other parent shows.</summary>
    AnyParentDoneAllShown,
}
