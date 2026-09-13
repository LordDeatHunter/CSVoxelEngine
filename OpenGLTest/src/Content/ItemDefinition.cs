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
    /// Damage one left-click hit removes from a block's hardness; omitted for the bare-hand
    /// baseline (0.5). Independent of <see cref="Tool"/>, which is what gates whether the block
    /// may be mined at all.
    /// </summary>
    public float? BlockDamage { get; set; }

    /// <summary>
    /// Damage one punch deals to an enemy; omitted for the bare-hand baseline (1). A weapon like
    /// the obsidian blade raises this without needing a <see cref="Tool"/> block.
    /// </summary>
    public float? EntityDamage { get; set; }

    /// <summary>
    /// Optional tool block: writing it marks the item as a mining tool and carries the numbers
    /// the mining gate reads. <c>power</c> is the item's <em>tier</em> — the number a block's
    /// <c>tools</c> requirement compares against — not its damage, which is
    /// <see cref="BlockDamage"/>. Which <c>tools</c> tags a block accepts are satisfied by
    /// membership in <c>assets/data/tags/&lt;tag&gt;.json</c> (an item listed in
    /// <c>pickaxe.json</c> is a pickaxe), not by this object.
    /// </summary>
    public ToolDefinition? Tool { get; set; }

    /// <summary>Uses before a durability item breaks; omitted (0) for endless items.</summary>
    public int? Durability { get; set; }

    /// <summary>Seconds between uses of this item; omitted for the global click rate.</summary>
    public float? UseDelay { get; set; }

    /// <summary>
    /// How the item sits in the hand when held, Minecraft-style: rotation as
    /// [pitch, yaw, roll] degrees composed yaw, then pitch, then roll; translation in
    /// blocks of hand space applied after rotation and never scaled; scale multiplies the
    /// whole sprite. Omitted for the tool default: upright, sideways like a pickaxe and
    /// canted toward the viewer so it never goes edge-on.
    /// </summary>
    public DisplayDefinition? Display { get; set; }

    /// <summary>The <c>display</c> object: held-item rotation, translation and scale.</summary>
    public sealed class DisplayDefinition
    {
        /// <summary>[pitch, yaw, roll] degrees; default [-30, 10, 0].</summary>
        public float[]? Rotation { get; set; }

        /// <summary>Blocks in hand space; default [0, 0, 0].</summary>
        public float[]? Translation { get; set; }

        /// <summary>Uniform multiplier; default 1.</summary>
        public float? Scale { get; set; }
    }

    /// <summary>
    /// The <c>tool</c> object: an item's mining numbers. <c>power</c> is its mining tier, the
    /// number a block's <c>tools</c> requirement compares against (default 0.5, the bare-hand
    /// baseline); the damage a hit actually removes is the item's <see cref="BlockDamage"/>.
    /// <c>hitDelayMs</c> is the gap between hits in milliseconds (default 600), so 5000 is one
    /// hit per five seconds.
    /// </summary>
    public sealed class ToolDefinition
    {
        /// <summary>Mining tier compared against a block's <c>tools</c> requirement; default 0.5.</summary>
        public float? Power { get; set; }

        /// <summary>Milliseconds between hits while mining; default 600.</summary>
        public float? HitDelayMs { get; set; }
    }

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
        /// <summary><c>pickup_fluid</c>, <c>place_fluid</c>, <c>place_stack</c> or <c>ignite_campfire</c>.</summary>
        public string? Action { get; set; }

        /// <summary>Item the use swaps this one for, e.g. <c>item/water_bucket</c>.</summary>
        public string? Result { get; set; }

        /// <summary>Fluid block the action picks up or places, e.g. <c>block/water</c>.</summary>
        public string? Fluid { get; set; }

        /// <summary>
        /// Stackable block a <c>place_stack</c> action builds up, e.g. <c>block/pebble</c>
        /// for flint. Right-clicking the block adds one unit, right-clicking anything else
        /// grounds a fresh pile of one. For <c>ignite_campfire</c> it names the firepit
        /// block (a max-count branch pile) the spark can light.
        /// </summary>
        public string? Block { get; set; }

        /// <summary>
        /// Per-use success chance 0..1 for <c>ignite_campfire</c>; omitted means always.
        /// </summary>
        public float? Chance { get; set; }
    }
}
