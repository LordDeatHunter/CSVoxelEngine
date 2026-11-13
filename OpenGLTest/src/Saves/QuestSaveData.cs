using System.Collections.Generic;

namespace VoxelEngine.Saves;

/// <summary>
/// A player's quest progress as <c>player.json</c> stores it (<see cref="PlayerSaveData.Quests"/>) and the
/// server sends it (<see cref="VoxelEngine.Networking.QuestStateCodec"/>): quests by id, never by index,
/// so a renumbered or edited quest set degrades field by field. Every field is optional.
/// </summary>
public sealed class QuestSaveData
{
    /// <summary>The ids of the completed quests.</summary>
    public List<string>? Completed { get; set; }

    /// <summary>Per unfinished quest id, the count each requirement has reached, in requirement order.</summary>
    public Dictionary<string, int[]>? Progress { get; set; }

    /// <summary>The quest book's pan and zoom (singleplayer only; a client keeps its own for the session).</summary>
    public QuestViewData? View { get; set; }
}
