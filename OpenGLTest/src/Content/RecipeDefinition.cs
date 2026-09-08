using System.Text.Json.Serialization;

namespace silkgltriangle;

/// <summary>JSON shape of <c>assets/data/recipes/&lt;name&gt;.json</c>.</summary>
public sealed class RecipeDefinition
{
    /// <summary>Result id, e.g. <c>item/primitive_pickaxe</c>.</summary>
    public string? Result { get; set; }

    /// <summary>How many of the result one craft makes; default 1.</summary>
    public int? Count { get; set; }

    /// <summary>What one craft consumes.</summary>
    public IngredientDefinition[]? Ingredients { get; set; }

    public sealed class IngredientDefinition
    {
        /// <summary>Ingredient id, e.g. <c>item/flint</c>.</summary>
        public string? Item { get; set; }

        /// <summary>Units consumed per craft; default 1.</summary>
        public int? Count { get; set; }
    }
}
