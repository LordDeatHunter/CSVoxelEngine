using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VoxelEngine.Content.Definitions;

/// <summary>
/// JSON shape of <c>assets/data/quests/&lt;id&gt;.json</c>: one quest, whose id is its file name. Compiled by
/// <see cref="VoxelEngine.Content.Quests.QuestLoader"/>.
/// </summary>
public sealed class QuestDefinition : IFileNamedDefinition
{
    /// <summary>The quest's id: its file name, unique across the folder. Set by whoever reads or makes the quest, never
    /// read from or written to the file.</summary>
    [JsonIgnore]
    public string? Id { get; set; }

    /// <summary>Display title; default the id.</summary>
    public string? Title { get; set; }

    /// <summary>The flavor text shown in the details panel; default none.</summary>
    public string? Description { get; set; }

    /// <summary>The block or item (a content name, <c>block/</c> or <c>item/</c> prefix allowed) drawn in
    /// the node. Required.</summary>
    public string? Icon { get; set; }

    /// <summary>The node's position in the book, in grid steps (two numbers). Required.</summary>
    public float[]? Pos { get; set; }

    /// <summary><c>hex</c> or <c>circle</c>; default <c>hex</c> for a quest with no parents, else <c>circle</c>.</summary>
    public string? Shape { get; set; }

    /// <summary>The quest ids that must all be complete before this one shows and makes progress; default none (a root).</summary>
    public string[]? Parents { get; set; }

    /// <summary>When the quest shows as a locked preview before it unlocks: <c>unlocked</c> (not before; the default),
    /// <c>always</c>, <c>anyParentShown</c>, <c>allParentsShown</c>, <c>anyParentDone</c> or <c>anyParentDoneAllShown</c>
    /// (see <see cref="VoxelEngine.Content.Quests.QuestReveal"/>).</summary>
    public string? Reveal { get; set; }

    /// <summary>Whether the details panel lists the requirements before the quest is complete; default true.</summary>
    public bool? Readable { get; set; }

    /// <summary>When true, meeting any one requirement completes the quest, instead of all of them; default false.</summary>
    public bool? AnyOf { get; set; }

    /// <summary>What completes the quest, all of them (or any one with <see cref="AnyOf"/>). Required, at least one.</summary>
    public List<QuestRequirementDefinition>? Requirements { get; set; }
}
