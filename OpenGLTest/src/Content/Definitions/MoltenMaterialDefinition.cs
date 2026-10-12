namespace VoxelEngine.Content.Definitions;

/// <summary>JSON shape of <c>assets/data/molten/&lt;name&gt;.json</c>.</summary>
public sealed class MoltenMaterialDefinition
{
    /// <summary>Display name for tooltips and previews; defaults to the file name.</summary>
    public string? Name { get; set; }

    /// <summary>Atlas tile the molten pool is drawn with, e.g. <c>lava_still</c>.</summary>
    public string? Texture { get; set; }

    /// <summary>Tint applied to that tile, exactly three components in 0..255.</summary>
    public float[]? Color { get; set; }
}
