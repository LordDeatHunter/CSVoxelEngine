using System.Collections.Generic;
using System.Text.Json;
using silkgltriangle.Content;

namespace silkgltriangle;

/// <summary>
/// JSON shape of <c>assets/data/loot/&lt;name&gt;.json</c>: a simplified Minecraft loot table.
/// Compiled into a <see cref="LootTable"/> by <see cref="ContentRegistry"/>, which resolves
/// every entry's item name to a byte id up front so a roll never touches a string.
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
    }

    /// <summary>One weighted outcome of a pool.</summary>
    public sealed class EntryDefinition
    {
        /// <summary>Block or item name, optionally prefixed <c>block/</c> or <c>item/</c>.</summary>
        public string? Item { get; set; }

        /// <summary>Weighted nothing; mutually exclusive with <see cref="Item"/>.</summary>
        public bool? Empty { get; set; }

        public int? Weight { get; set; }

        /// <summary>Int or <c>{"min":a,"max":b}</c>; default 1.</summary>
        public JsonElement Count { get; set; }
    }
}
