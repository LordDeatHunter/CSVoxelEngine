namespace VoxelEngine.Content.Definitions;

/// <summary>JSON shape of <c>assets/data/recipes/melt/&lt;input&gt;.json</c>.</summary>
public sealed class MeltRecipeDefinition
{
    /// <summary>Meltable block or item id, e.g. <c>item/malachite_ore</c>.</summary>
    public string? Input { get; set; }

    /// <summary>Molten material one unit of the input yields, by file name, e.g. <c>copper</c>.</summary>
    public string? Fluid { get; set; }

    /// <summary>Milliliters of that material one unit yields.</summary>
    public int? AmountMl { get; set; }
}
