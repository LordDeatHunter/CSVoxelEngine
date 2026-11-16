using System.Collections.Generic;
using System.Numerics;

namespace VoxelEngine.Content.Quests;

/// <summary>
/// One compiled quest: its index in <see cref="QuestBook.Quests"/>, its id, text and icon, its absolute
/// grid position in the quest book (the graph's origin already added), the indices of its parents (all
/// of which must be complete before it unlocks) and children, and its requirements (all of which complete it, or any one of them when <see cref="AnyOf"/>).
/// </summary>
public sealed class QuestType
{
    /// <summary>The quest's position in <see cref="QuestBook.Quests"/>.</summary>
    public required int Index { get; init; }

    /// <summary>The quest's id, unique across every graph; saves and the wire name a quest by it.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    /// <summary>The block or item id drawn in the node.</summary>
    public required ushort IconId { get; init; }

    /// <summary>Absolute grid position in the quest book (graph origin plus the quest's own position).</summary>
    public required Vector2 Position { get; init; }

    public required QuestShape Shape { get; init; }

    /// <summary>When the quest shows as a locked preview before it unlocks.</summary>
    public QuestReveal Reveal { get; init; }

    /// <summary>When the quest unlocks: shows its details and makes progress.</summary>
    public QuestUnlock Unlock { get; init; }

    /// <summary>Whether the details panel lists the requirements before the quest is complete.</summary>
    public required bool Readable { get; init; }

    public required IReadOnlyList<int> Parents { get; init; }

    public required IReadOnlyList<int> Children { get; init; }

    public required IReadOnlyList<QuestRequirement> Requirements { get; init; }

    /// <summary>Whether meeting any one requirement completes the quest, instead of all of them.</summary>
    public required bool AnyOf { get; init; }
}
