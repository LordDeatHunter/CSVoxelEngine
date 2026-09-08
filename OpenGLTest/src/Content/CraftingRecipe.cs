namespace silkgltriangle;

// TODO(tags): let an ingredient name a tag (item group) instead of one id, e.g.
// { "tag": "log", "count": 1 }, with tags defined in assets/data/tags/<name>.json
// listing member ids. The crafting tab then needs per-ingredient member choice:
// the player picks 1 of N log types per slot and each craft consumes the picked
// member, so the same recipe can be crafted repeatedly with different members.
// Keep it data-driven: no per-id branches, resolve members through ContentRegistry
// like loot entries do.
//
// For now every ingredient names exactly one block/item id with a count.

/// <summary>One compiled crafting recipe: what it makes and what one craft consumes.</summary>
public sealed class CraftingRecipe
{
    public CraftingRecipe(string name, byte resultId, int resultCount, Ingredient[] ingredients)
    {
        Name = name;
        ResultId = resultId;
        ResultCount = resultCount;
        Ingredients = ingredients;
    }

    /// <summary>Recipe file name, for error messages.</summary>
    public string Name { get; }

    /// <summary>What one craft adds.</summary>
    public byte ResultId { get; }

    /// <summary>How many of the result one craft adds.</summary>
    public int ResultCount { get; }

    /// <summary>What one craft consumes.</summary>
    public Ingredient[] Ingredients { get; }

    /// <summary>One consumed ingredient: an id plus units per craft.</summary>
    public readonly record struct Ingredient(byte Id, int Count);
}
