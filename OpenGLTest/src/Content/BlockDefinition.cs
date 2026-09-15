using System.Collections.Generic;
using System.Text.Json;

namespace silkgltriangle;

/// <summary>
/// JSON shape of <c>assets/data/blocks/&lt;name&gt;.json</c>. Every field is optional except
/// <see cref="Model"/>; nullable value types mark "not written by the author" so
/// <see cref="ContentRegistry"/> can apply defaults that depend on the baked model (a block
/// occludes by default only if its model turned out to be a full cube).
/// </summary>
public sealed class BlockDefinition
{
    /// <summary>Display name; defaults to the file name when omitted.</summary>
    public string? Name { get; set; }

    /// <summary>Model file name (<c>"block/cube_all"</c>) or an inline model object.</summary>
    public JsonElement Model { get; set; }

    /// <summary>Damage needed to break; one hit deals the held tool's power. Default 5.</summary>
    public float? Hardness { get; set; }

    /// <summary>
    /// When true, a hit only damages the block if the held item belongs to one of the
    /// <see cref="Tools"/> tags at or above its required power; anything else deals 0 and makes
    /// no progress. When false (default), any tool or bare hand mines it and <see cref="Tools"/>
    /// is ignored.
    /// </summary>
    public bool? RequiresTool { get; set; }

    /// <summary>
    /// Tag name to minimum tool power: <c>{"pickaxe": 1.0}</c> admits any item listed in the
    /// <c>pickaxe</c> tag whose tool power is 1.0 or more. A <c>0</c> requirement admits any
    /// member of that tag. Ignored unless <see cref="RequiresTool"/> is set.
    /// </summary>
    public Dictionary<string, float>? Tools { get; set; }

    public bool? Solid { get; set; }

    /// <summary>
    /// How fast the player moves through this block, 0..1. <c>0</c> (the default) is a normal
    /// solid collider. A positive value makes the block passable and scales the player's speed
    /// by that fraction while the body overlaps it (leaves <c>0.75</c>); <c>1</c> is passable at
    /// full speed. Never combines with <see cref="Fluid"/>, whose movement is swimming.
    /// </summary>
    public float? MovementSpeed { get; set; }

    /// <summary>
    /// Grip of the block's top face, 0..1. <c>1</c> is instant response, lower slides more
    /// (the ground response time grows with <c>1 - friction</c>). Default 0.99; ice sets
    /// this lower so stops and turns glide. Only read from the solid block under the feet.
    /// </summary>
    public float? Friction { get; set; }

    public bool? Occludes { get; set; }
    public bool? CullSelf { get; set; }
    public bool? CastsAO { get; set; }
    public bool? Cutout { get; set; }
    public bool? Translucent { get; set; }
    public bool? Sway { get; set; }
    public bool? HasItem { get; set; }
    public bool? NeedsSupport { get; set; }

    /// <summary>Marks the block as gravity-affected (sand, gravel): it topples into a falling
    /// entity when the cell below it stops being solid. Never combines with
    /// <see cref="Fluid"/> or <see cref="NeedsSupport"/>.</summary>
    public bool? Falls { get; set; }

    /// <summary>Marks the block as a fluid: flows, is replaceable, never collides or drops.
    /// The registry forces the flags that follow from it rather than trusting the file.</summary>
    public bool? Fluid { get; set; }

    public LightDefinition? Light { get; set; }
    public WoodDefinition? Wood { get; set; }

    /// <summary>Fluid tuning; only legal on a block with <see cref="Fluid"/>. Omitted means
    /// water behaviour: every tick, full 7-block range, infinite sources, never hardens.</summary>
    public FlowDefinition? Flow { get; set; }

    /// <summary>"model" | "tapered_wood" | "pebbles" | "branch_pile" | "fluid" | "quern".</summary>
    public string? Renderer { get; set; }

    /// <summary>
    /// How many units of this block stack in one cell, candle-style (pebble piles hold up to
    /// 3, branch piles up to 4). The count lives in the chunk's state byte: 0 means "untouched worldgen" (the
    /// feature's own default, e.g. the pebble position hash) and 1..max is an explicit
    /// player-built pile. Only legal with a pile renderer (<c>"pebbles"</c> or
    /// <c>"branch_pile"</c>) for now;
    /// never combines with <see cref="Fluid"/> or <see cref="Falls"/> (a toppled pile would
    /// have nowhere to keep its count).
    /// </summary>
    public int? StackMax { get; set; }

    /// <summary>
    /// Stack count at which a stack block turns solid (layered snow is walked through until a full
    /// stack). Only legal on a stack block that is not already solid; 0 or omitted means the block's
    /// solidity never depends on its stack.
    /// </summary>
    public int? StackSolidAt { get; set; }

    /// <summary>"drop" | "prime_tnt".</summary>
    public string? OnBreak { get; set; }

    /// <summary>
    /// Custom selection boxes in cell-local 0..1 space: either a single
    /// <c>{"min": [x,y,z], "max": [x,y,z]}</c> object or an array of them.
    /// Omitted means the full cube. Undefined when the author wrote nothing.
    /// </summary>
    public JsonElement Hitbox { get; set; }

    /// <summary>Loot table name, or "none" for nothing. Omitted: the table sharing this
    /// block's name if one exists, otherwise the block drops itself.</summary>
    public string? Loot { get; set; }

    /// <summary>
    /// Sound group played while this block is mined (see <see cref="SfxManager"/>), e.g.
    /// <c>"hit_rock"</c> or <c>"hit_wood"</c>. Omitted, or naming a group with no files under
    /// <c>assets/sfx</c>, falls back to <see cref="SfxLibrary.DefaultGroup"/> with a warning.
    /// </summary>
    public string? Sfx { get; set; }

    /// <summary>Voxel light behaviour of a block.</summary>
    public sealed class LightDefinition
    {
        /// <summary>Light this block emits, 0-15.</summary>
        public byte? Emission { get; set; }

        /// <summary>Whether voxel light travels through it; defaults to "not a full cube".</summary>
        public bool? Passes { get; set; }
    }

    /// <summary>Wood-family membership: taper step and the half-width its box is drawn at.</summary>
    public sealed class WoodDefinition
    {
        /// <summary>0 is the family's base, each step up narrows toward its tip.</summary>
        public int? TaperLevel { get; set; }

        /// <summary>Half edge length of the tapered box, in blocks.</summary>
        public float? HalfWidth { get; set; }

        /// <summary>
        /// Which wood family this level belongs to, e.g. <c>"tree"</c> (the default) or
        /// <c>"cactus"</c>. Levels are numbered per family, and wood of different families never
        /// welds, so a cactus cannot grow into a trunk.
        /// </summary>
        public string? Family { get; set; }
    }

    /// <summary>Per-fluid simulation tuning, mirroring how water and lava differ.</summary>
    public sealed class FlowDefinition
    {
        /// <summary>Ticks between this fluid's updates; 2 means half water speed. Default 1.</summary>
        public int? TickDivisor { get; set; }

        /// <summary>Sideways spread in blocks before the skirt dries up, 1..7. Default 7.</summary>
        public int? Range { get; set; }

        /// <summary>Whether two adjacent sources birth a new source. Default true.</summary>
        public bool? Infinite { get; set; }

        /// <summary>Block this fluid becomes on touching <see cref="HardensTouching"/>,
        /// e.g. lava's <c>"cobblestone"</c>. Omitted: never hardens.</summary>
        public string? HardensTo { get; set; }

        /// <summary>Fluid whose contact hardens this one, e.g. lava's <c>"block/water"</c>.</summary>
        public string? HardensTouching { get; set; }
    }
}
