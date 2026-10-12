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
    /// Damage a charged throw deals to an enemy at full charge; half that at minimal charge.
    /// Omitted for a non-throwable item, 0 for a harmless one (snowball, mudball). Punch
    /// damage (<see cref="EntityDamage"/>) stays independent, so a throwable can still punch
    /// at the baseline while its throw hits softer or harder.
    /// </summary>
    public float? ThrowDamage { get; set; }

    /// <summary>
    /// Attack behavior the left-click drives: <c>punch</c> (default) or <c>slice</c>. See
    /// <see cref="AttackPattern"/>.
    /// </summary>
    public string? Attack { get; set; }

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

    /// <summary>
    /// Marks the item as a fuel a kiln burns. Omitted for everything that is not fuel.
    /// </summary>
    public FuelDefinition? Fuel { get; set; }

    /// <summary>How long one unit of this fuel burns.</summary>
    public sealed class FuelDefinition
    {
        /// <summary>Seconds one unit keeps a kiln lit (coal 60); 1..3600.</summary>
        public float? BurnSeconds { get; set; }
    }

    /// <summary>
    /// Marks the item as a crucible: a vessel whose molten contents ride on its own
    /// <see cref="ItemStack"/> (a <c>CrucibleComponent</c>), which is why a crucible stores into
    /// the Tools pool one instance at a time. Omitted for everything that is not one.
    /// </summary>
    public CrucibleDefinition? Crucible { get; set; }

    /// <summary>The <c>crucible</c> object: how much molten metal one vessel holds.</summary>
    public sealed class CrucibleDefinition
    {
        /// <summary>Millilitres the vessel holds, melted and pending together; 100..10000.</summary>
        public int? CapacityMl { get; set; }
    }

    /// <summary>
    /// Optional <c>block/&lt;name&gt;</c> model under <c>assets/models</c>, baked into
    /// <see cref="Item.BakedModel"/> so the item can be drawn as real 3D geometry while it
    /// sits inside a station (a crucible standing in a kiln's hearth). The sprite stays the
    /// inventory icon; this only adds a mesh.
    /// </summary>
    public string? Model { get; set; }

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
        /// <summary><c>pickup_fluid</c>, <c>place_fluid</c>, <c>place_stack</c>,
        /// <c>ignite_campfire</c> or <c>convert_multiblock</c>.</summary>
        public string? Action { get; set; }

        /// <summary>Item the use swaps this one for, e.g. <c>item/water_bucket</c>. For
        /// <c>convert_multiblock</c> it is the multiblock <em>anchor block</em> the volume
        /// becomes instead, e.g. <c>block/mud_brick_kiln</c>.</summary>
        public string? Result { get; set; }

        /// <summary>Fluid block the action picks up or places, e.g. <c>block/water</c>.</summary>
        public string? Fluid { get; set; }

        /// <summary>
        /// Stackable block a <c>place_stack</c> action builds up, e.g. <c>block/pebble</c>
        /// for flint. Right-clicking the block adds one unit, right-clicking anything else
        /// grounds a fresh pile of one. For <c>ignite_campfire</c> it names the firepit
        /// block (a max-count branch pile) the spark can light, and for
        /// <c>convert_multiblock</c> the <em>source block</em> the 2x2x2 volume must be
        /// built from, e.g. <c>block/mud_bricks</c>.
        /// </summary>
        public string? Block { get; set; }

        /// <summary>
        /// Per-use success chance 0..1 for <c>ignite_campfire</c>; omitted means always.
        /// </summary>
        public float? Chance { get; set; }
        /// <summary>
        /// Seconds the use takes: holding right-click plays a use pose and the use runs only when
        /// the hold completes, so letting go early cancels it and spends nothing. 0 (omitted) is
        /// the old instant use. See <see cref="Item.UseSeconds"/>.
        /// </summary>
        public float? Seconds { get; set; }

        /// <summary>
        /// Tree species an <c>plant_tree</c> use plants (an acorn plants an oak): the file name
        /// under <c>assets/data/trees/</c>, checked when the use runs.
        /// </summary>
        public string? Tree { get; set; }
    }
}
