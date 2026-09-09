namespace silkgltriangle;

/// <summary>
/// JSON shape of <c>assets/data/tags/&lt;name&gt;.json</c>: a named set of blocks that
/// gameplay or meshing treats as a group (e.g. what tapered wood may grow into).
/// </summary>
public sealed class TagDefinition
{
    /// <summary>Member blocks, as bare block names or <c>block/</c>-prefixed ids.</summary>
    public string[]? Blocks { get; set; }
}
