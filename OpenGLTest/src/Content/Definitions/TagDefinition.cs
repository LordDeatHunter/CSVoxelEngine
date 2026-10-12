namespace VoxelEngine.Content.Definitions;

/// <summary>
/// JSON shape of <c>assets/data/tags/&lt;name&gt;.json</c>: a named set of blocks and/or items
/// that gameplay treats as a group (e.g. the tools a block gates mining on, or the members of
/// a crafting ingredient like "any log").
/// </summary>
public sealed class TagDefinition
{
    /// <summary>Member blocks, as bare block names or <c>block/</c>-prefixed ids.</summary>
    public string[]? Blocks { get; set; }

    /// <summary>Member items, as bare item names or <c>item/</c>-prefixed ids.</summary>
    public string[]? Items { get; set; }
}
