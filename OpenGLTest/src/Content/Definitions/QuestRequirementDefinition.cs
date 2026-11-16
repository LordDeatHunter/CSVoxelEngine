namespace VoxelEngine.Content.Definitions;

/// <summary>JSON shape of one requirement in a <see cref="QuestDefinition"/>'s <c>requirements</c>.</summary>
public sealed class QuestRequirementDefinition
{
    /// <summary><c>obtain</c>, <c>craft</c> (an item), <c>break</c>, <c>place</c> (a block) or <c>kill</c> (an entity). Required.</summary>
    public string? Type { get; set; }

    /// <summary>The item (or block item) an <c>obtain</c> or <c>craft</c> requirement names.</summary>
    public string? Item { get; set; }

    /// <summary>A tag (<c>assets/data/tags/&lt;name&gt;.json</c>) an <c>obtain</c> or <c>craft</c> requirement names
    /// instead of one item: any member counts, and an obtain sums what is held across them. Exclusive with <see cref="Item"/>.</summary>
    public string? Tag { get; set; }

    /// <summary>The display name of a <see cref="Tag"/> requirement ("Any Copper or Tin Cast"); default the tag name.</summary>
    public string? Label { get; set; }

    /// <summary>The block a <c>break</c> or <c>place</c> requirement names.</summary>
    public string? Block { get; set; }

    /// <summary>The entity file name a <c>kill</c> requirement names.</summary>
    public string? Entity { get; set; }

    /// <summary>How many, at least 1; default 1. For <c>obtain</c>, the stack the inventory must hold at once.</summary>
    public int? Count { get; set; }
}
