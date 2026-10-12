using System;
using silkgltriangle.Content;

namespace silkgltriangle;

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

    /// <summary>
    /// One consumed ingredient: either a single id (<see cref="IsTag"/> false) or a named tag
    /// whose <see cref="MemberIds"/> the player picks from at craft time (e.g. "any log").
    /// Tags resolve through <see cref="ContentRegistry"/> like loot entries do — no per-id
    /// branches at the call sites.
    /// </summary>
    public readonly record struct Ingredient(byte Id, int Count, string? Tag = null, byte[]? MemberIds = null)
    {
        /// <summary>Whether this ingredient accepts any member of <see cref="Tag"/>.</summary>
        public bool IsTag => Tag != null;

        /// <summary>Every id this ingredient accepts: the tag members, or just <see cref="Id"/>.</summary>
        public byte[] AcceptedIds => MemberIds ?? [Id];

        /// <summary>Whether this id satisfies the ingredient (single id or tag member).</summary>
        public bool Accepts(byte id) => !IsTag ? id == Id : MemberIds != null && Array.IndexOf(MemberIds, id) >= 0;
    }
}
