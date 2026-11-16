namespace VoxelEngine.Content.Definitions;

/// <summary>JSON shape of <c>assets/data/recipes/forge/&lt;result&gt;.json</c>: what a smithing hammer forges on an anvil
/// from a stack of one solid metal laid on it: the recipe is picked from the anvil's list, then hammered <c>hits</c>
/// times.</summary>
public sealed class ForgeRecipeDefinition
{
    /// <summary>The metal stacked on the anvil, e.g. <c>item/bloom</c>.</summary>
    public string? Metal { get; set; }

    /// <summary>How many of the metal one forge takes off the anvil; required, 1..64.</summary>
    public int? MetalCount { get; set; }

    /// <summary>The forged result, e.g. <c>item/iron_axe_head</c>.</summary>
    public string? Result { get; set; }

    /// <summary>How many of the result one forge makes; default 1.</summary>
    public int? Count { get; set; }

    /// <summary>Hammer hits the forge takes once picked; required, 1..64.</summary>
    public int? Hits { get; set; }

    /// <summary>What the hammering squeezes out besides the result (a bloom's slag); optional.</summary>
    public ByproductDefinition? Byproduct { get; set; }

    /// <summary>The <c>"byproduct"</c> object: <c>{"item": "item/slag", "count": 3}</c>.</summary>
    public sealed class ByproductDefinition
    {
        /// <summary>The byproduct item; required.</summary>
        public string? Item { get; set; }

        /// <summary>How many one forge leaves; default 1.</summary>
        public int? Count { get; set; }
    }
}
