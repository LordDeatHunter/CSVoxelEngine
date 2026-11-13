using System.Collections.Generic;

namespace VoxelEngine.Content.Definitions;

/// <summary>
/// JSON shape of <c>assets/data/quests/&lt;graph&gt;.json</c>: one quest graph (a root quest and the
/// quests that branch off it) drawn as one cluster in the quest book. Compiled by
/// <see cref="VoxelEngine.Content.Quests.QuestLoader"/>.
/// </summary>
public sealed class QuestGraphDefinition
{
    /// <summary>The graph's display title; default the file name.</summary>
    public string? Title { get; set; }

    /// <summary>The grid offset (two numbers) added to every quest's <see cref="QuestDefinition.Pos"/>, so a
    /// whole graph moves as a unit; default [0, 0].</summary>
    public float[]? Origin { get; set; }

    /// <summary>The graph's quests. Required, at least one.</summary>
    public List<QuestDefinition>? Quests { get; set; }

    /// <summary>One quest node.</summary>
    public sealed class QuestDefinition
    {
        /// <summary>The quest's id, unique across every graph file. Required.</summary>
        public string? Id { get; set; }

        /// <summary>Display title; default the id.</summary>
        public string? Title { get; set; }

        /// <summary>The flavor text shown in the details panel; default none.</summary>
        public string? Description { get; set; }

        /// <summary>The block or item (a content name, <c>block/</c> or <c>item/</c> prefix allowed) drawn in
        /// the node. Required.</summary>
        public string? Icon { get; set; }

        /// <summary>The node's grid position (two numbers) relative to the graph's origin. Required.</summary>
        public float[]? Pos { get; set; }

        /// <summary><c>hex</c> or <c>circle</c>; default <c>hex</c> for a quest with no parents, else <c>circle</c>.</summary>
        public string? Shape { get; set; }

        /// <summary>The quest ids that must all be complete before this one shows and makes progress; default none (a root).</summary>
        public string[]? Parents { get; set; }

        /// <summary>Whether the details panel lists the requirements before the quest is complete; default true.</summary>
        public bool? Readable { get; set; }

        /// <summary>What completes the quest, all of them. Required, at least one.</summary>
        public List<RequirementDefinition>? Requirements { get; set; }
    }

    /// <summary>One requirement of a quest.</summary>
    public sealed class RequirementDefinition
    {
        /// <summary><c>obtain</c>, <c>craft</c> (an item), <c>break</c>, <c>place</c> (a block) or <c>kill</c> (an entity). Required.</summary>
        public string? Type { get; set; }

        /// <summary>The item (or block item) an <c>obtain</c> or <c>craft</c> requirement names.</summary>
        public string? Item { get; set; }

        /// <summary>The block a <c>break</c> or <c>place</c> requirement names.</summary>
        public string? Block { get; set; }

        /// <summary>The entity file name a <c>kill</c> requirement names.</summary>
        public string? Entity { get; set; }

        /// <summary>How many, at least 1; default 1. For <c>obtain</c>, the stack the inventory must hold at once.</summary>
        public int? Count { get; set; }
    }
}
