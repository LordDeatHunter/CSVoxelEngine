using System.Collections.Generic;

namespace VoxelEngine.Content.Definitions;

/// <summary>JSON shape of <c>assets/data/sets/&lt;name&gt;.json</c>: an armor set's display name and the bonuses
/// that switch on as more of its pieces are worn. Which items belong to it is the other direction: an item's
/// <c>equip.set</c> names the file.</summary>
public sealed class GearSetDefinition
{
    /// <summary>Display name; defaults to the file name.</summary>
    public string? Name { get; set; }

    /// <summary>The bonuses, each active once at least its <see cref="BonusDefinition.Pieces"/> are worn.</summary>
    public BonusDefinition[]? Bonuses { get; set; }

    /// <summary>One set bonus: a piece count and the stat changes it grants (the same vocabulary as gear).</summary>
    public sealed class BonusDefinition
    {
        /// <summary>How many pieces of the set must be worn.</summary>
        public int? Pieces { get; set; }

        /// <summary>The stat changes by stat name.</summary>
        public Dictionary<string, float>? Modifiers { get; set; }
    }
}
