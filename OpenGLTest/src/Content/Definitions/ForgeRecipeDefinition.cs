namespace VoxelEngine.Content.Definitions;

/// <summary>JSON shape of <c>assets/data/recipes/forge/&lt;result&gt;.json</c>: what a smithing hammer forges on an anvil
/// from a stack of one solid metal laid on it.</summary>
public sealed class ForgeRecipeDefinition
{
    /// <summary>The metal stacked on the anvil, e.g. <c>item/wrought_iron</c>.</summary>
    public string? Metal { get; set; }

    /// <summary>How many of the metal one forge takes off the anvil; required, 1..64.</summary>
    public int? MetalCount { get; set; }

    /// <summary>The forged result, e.g. <c>item/iron_axe_head</c>.</summary>
    public string? Result { get; set; }

    /// <summary>How many of the result one forge makes; default 1.</summary>
    public int? Count { get; set; }
}
