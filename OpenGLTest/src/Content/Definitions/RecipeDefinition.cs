using System.Text.Json.Serialization;

namespace VoxelEngine.Content.Definitions;

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
        /// <summary>Ingredient id, e.g. <c>item/flint</c>. Mutually exclusive with <see cref="Tag"/>.</summary>
        public string? Item { get; set; }

        /// <summary>
        /// Ingredient tag, e.g. <c>log</c> for "any log" (members come from
        /// <c>assets/data/tags/&lt;name&gt;.json</c>). Mutually exclusive with <see cref="Item"/>.
        /// </summary>
        public string? Tag { get; set; }

        /// <summary>Units consumed per craft; default 1.</summary>
        public int? Count { get; set; }
    }
}
