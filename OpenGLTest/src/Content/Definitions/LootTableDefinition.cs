using System.Collections.Generic;
using System.Text.Json;
using VoxelEngine.Content.Loot;

namespace VoxelEngine.Content.Definitions;

/// <summary>
/// JSON shape of <c>assets/data/loot/&lt;name&gt;.json</c>: a simplified Minecraft loot table.
/// Compiled into a <see cref="LootTable"/> by <see cref="ContentRegistry"/>, which resolves
/// every entry's item name to an id up front so a roll never touches a string.
/// </summary>
public sealed class LootTableDefinition
{
    public List<PoolDefinition>? Pools { get; set; }

    /// <summary>One independent group of rolls.</summary>
    public sealed class PoolDefinition
    {
        /// <summary>Int, <c>{"min":1,"max":3}</c>, or <c>{"provider":"pebble_count"}</c>.</summary>
        public JsonElement Rolls { get; set; }

        /// <summary>Probability the whole pool runs at all; default 1.</summary>
        public float? Chance { get; set; }

        public List<EntryDefinition>? Entries { get; set; }

        /// <summary>The pool only rolls at or above this world Y; default no limit.</summary>
        public int? MinY { get; set; }

        /// <summary>The pool only rolls at or below this world Y; default no limit.</summary>
        public int? MaxY { get; set; }
    }

    /// <summary>One weighted outcome of a pool.</summary>
    public sealed class EntryDefinition
    {
        /// <summary>Block or item name, optionally prefixed <c>block/</c> or <c>item/</c>.</summary>
        public string? Item { get; set; }

        /// <summary>Weighted nothing; mutually exclusive with <see cref="Item"/>.</summary>
        public bool? Empty { get; set; }

        /// <summary>Another loot table (a file name) rolled in place of this entry; mutually exclusive with
        /// <see cref="Item"/> and <see cref="Empty"/>.</summary>
        public string? Table { get; set; }

        /// <summary>For a piece of gear: the rarity it rolls, a name (<c>"rare"</c>) or <c>{"min":"common","max":"epic"}</c>,
        /// picked by loot weight. Only legal on a gear item.</summary>
        public JsonElement Rarity { get; set; }

        public int? Weight { get; set; }

        /// <summary>Int or <c>{"min":a,"max":b}</c>; default 1.</summary>
        public JsonElement Count { get; set; }
    }
}
