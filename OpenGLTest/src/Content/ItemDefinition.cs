namespace silkgltriangle;

/// <summary>JSON shape of <c>assets/data/items/&lt;name&gt;.json</c>.</summary>
public sealed class ItemDefinition
{
    /// <summary>Display name; defaults to the file name when omitted.</summary>
    public string? Name { get; set; }

    /// <summary>Sprite path relative to the working directory, like every other asset.</summary>
    public string? Texture { get; set; }

    /// <summary>What right-clicking with the item does; omitted for a plain pickup.</summary>
    public UseDefinition? Use { get; set; }

    /// <summary>The <c>use</c> object: the action plus the block and item it works with.</summary>
    public sealed class UseDefinition
    {
        /// <summary><c>pickup_fluid</c> or <c>place_fluid</c>.</summary>
        public string? Action { get; set; }

        /// <summary>Item the use swaps this one for, e.g. <c>item/water_bucket</c>.</summary>
        public string? Result { get; set; }

        /// <summary>Fluid block the action picks up or places, e.g. <c>block/water</c>.</summary>
        public string? Fluid { get; set; }
    }
}
