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

    /// <summary>
    /// Optional spritesheet: the texture holds a grid of frames instead of one sprite.
    /// Frames are row-major (frame 0 is the top-left cell) and picked per draw by
    /// <c>mode</c>: <c>heading</c> selects from the player's compass heading (north = 0),
    /// <c>loop</c> slides through them at <c>fps</c>. Omit the whole object for a plain sprite.
    /// </summary>
    public SpritesheetDefinition? Spritesheet { get; set; }

    /// <summary>The <c>spritesheet</c> object: grid size plus how frames are picked.</summary>
    public sealed class SpritesheetDefinition
    {
        /// <summary>Cells across the texture.</summary>
        public int? Cols { get; set; }

        /// <summary>Cells down the texture.</summary>
        public int? Rows { get; set; }

        /// <summary>Frames used, from the top-left; defaults to the whole grid.</summary>
        public int? Frames { get; set; }

        /// <summary><c>heading</c> or <c>loop</c>.</summary>
        public string? Mode { get; set; }

        /// <summary>Frames per second for <c>loop</c>; defaults to 8.</summary>
        public float? Fps { get; set; }
    }

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
