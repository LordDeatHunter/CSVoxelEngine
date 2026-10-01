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

    /// <summary>
    /// Density multiplier (0..1) for the natural pebble scatter (see <see cref="PebbleGenerator"/>)
    /// when this block is the exposed surface under the pebble cell. Rock and gravel keep the
    /// patch's full density, grass a thinner share, and sand nothing. <c>0</c> (the default) means
    /// this surface never grows pebbles.
    /// </summary>
    public float? PebbleWeight { get; set; }

    public bool? Occludes { get; set; }
    public bool? CullSelf { get; set; }
    public bool? CastsAO { get; set; }
    public bool? Cutout { get; set; }
    public bool? Translucent { get; set; }
    public bool? Sway { get; set; }

    /// <summary>
    /// Climate-coloured faces (grass, leaves): which faces wear a grey tile tinted by the
    /// <see cref="silkgltriangle.BiomeTint"/> field, and from which palette. Omitted, every face is
    /// drawn exactly as the model says. Only legal on a block whose model bakes to a plain full
    /// cube; the model's own tiles stay the block's icon, drop and held look.
    /// </summary>
    public BiomeTintDefinition? BiomeTint { get; set; }

    public bool? HasItem { get; set; }
    public bool? NeedsSupport { get; set; }

    /// <summary>
    /// Whether a block placement aimed at this block fills the block's own cell instead of the
    /// cell across the hit face, discarding whatever it held (layered snow: a block placed against
    /// a partial drift replaces it rather than hovering a layer above the ground). Only fires while
    /// the cell is not solid, so a full snow stack behaves like an ordinary block, and the
    /// displaced block drops nothing. Independent of <see cref="NeedsSupport"/>, which is what a
    /// fluid flow and a bucket pour may overwrite.
    /// </summary>
    public bool? ReplaceableByPlacement { get; set; }

    /// <summary>Marks the block as gravity-affected (sand, gravel): it topples into a falling
    /// entity when the cell below it stops being solid. Never combines with
    /// <see cref="Fluid"/> or <see cref="NeedsSupport"/>.</summary>
    public bool? Falls { get; set; }

    /// <summary>Marks the block as a fluid: flows, is replaceable, never collides or drops.
    /// The registry forces the flags that follow from it rather than trusting the file.</summary>
    public bool? Fluid { get; set; }

    public LightDefinition? Light { get; set; }
    public WoodDefinition? Wood { get; set; }

    /// <summary>Reflective surface tuning; omitted means a plain non-metallic block, which is
    /// every block but the ores.</summary>
    public MaterialDefinition? Material { get; set; }

    /// <summary>Fluid tuning; only legal on a block with <see cref="Fluid"/>. Omitted means
    /// water behaviour: every tick, full 7-block range, infinite sources, never hardens.</summary>
    public FlowDefinition? Flow { get; set; }

    /// <summary>"model" | "tapered_wood" | "pebbles" | "branch_pile" | "snow_layers" | "fluid" |
    /// "quern" | "multiblock" | "multiblock_part".</summary>
    public string? Renderer { get; set; }

    /// <summary>The structure an anchor block stands for; required on (and only legal with) the
    /// <c>"multiblock"</c> renderer.</summary>
    public MultiblockDefinition? Multiblock { get; set; }

    /// <summary>Marks a multiblock anchor as a kiln, which is what gives it fuel, ignition and
    /// the integrity sweep of the kiln manager. Only legal on the <c>"multiblock"</c>
    /// renderer.</summary>
    public bool? Kiln { get; set; }

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

    /// <summary>
    /// Whether bodies collide with the declared <c>hitbox</c> boxes instead of the whole cell,
    /// the way tapered wood collides with its drawn shape, so a low slab is stepped onto and
    /// stood on rather than walling its cell off. Requires <c>hitbox</c>, <c>solid: true</c>,
    /// no <c>stackMax</c> and not a fluid. Omitted means false.
    /// </summary>
    public bool? CollidesByHitbox { get; set; }

    /// <summary>Loot table name, or "none" for nothing. Omitted: the table sharing this
    /// block's name if one exists, otherwise the block drops itself.</summary>
    public string? Loot { get; set; }

    /// <summary>
    /// Sound group played while this block is mined (see <see cref="SfxManager"/>), e.g.
    /// <c>"hit_rock"</c> or <c>"hit_wood"</c>. Omitted, or naming a group with no files under
    /// <c>assets/sfx</c>, falls back to <see cref="SfxLibrary.DefaultGroup"/> with a warning.
    /// </summary>
    public string? Sfx { get; set; }

    /// <summary>
    /// How reflective the block's surface is. Omitting the whole object (every block but the
    /// ores) leaves it perfectly diffuse and costs nothing at render time: the fragment shader
    /// branches on metallic being positive, and that branch is uniform across a quad.
    /// The <em>shape</em> of the surface is not here - it comes from the matching tangent-space
    /// normal map in <c>assets/textures/blocks_material/</c>, keyed by the same texture name.
    /// </summary>
    public sealed class MaterialDefinition
    {
        /// <summary>How metallic the surface reads, 0..1. 0 (the default) is an ordinary diffuse
        /// block. Higher values trade diffuse albedo for an albedo-tinted specular, which is what
        /// makes metal read as metal without HDR headroom: a dark body with a bright highlight.</summary>
        public float? Metallic { get; set; }

        /// <summary>Surface roughness, 0..1: lower is a tighter, sharper highlight. Default 1
        /// (fully rough). Obsidian's polished glass sits near 0.06, coal's dull rock near 0.55.</summary>
        public float? Roughness { get; set; }

        /// <summary>Multiplier on the normal map's relief, 0..4. Default 1, which uses the map as
        /// generated; this is the knob for retuning a surface without regenerating its art.</summary>
        public float? NormalStrength { get; set; }
    }

    /// <summary>
    /// The two other blocks a multiblock anchor is tied to: the invisible cells that fill the
    /// rest of its 2x2x2 volume, and the plain block the structure is built from and reverts to.
    /// </summary>
    public sealed class MultiblockDefinition
    {
        /// <summary>Block filling the seven non-anchor cells; must use the
        /// <c>"multiblock_part"</c> renderer.</summary>
        public string? Part { get; set; }

        /// <summary>Plain block the 2x2x2 volume is built from, and what each cell turns back
        /// into when the structure is broken, e.g. <c>"block/mud_bricks"</c>.</summary>
        public string? Source { get; set; }
    }

    /// <summary>
    /// The <c>"biomeTint"</c> object: a palette selector beside the per-face entries, which sit
    /// under their own <c>faces</c> key so a face name can never collide with the palette key.
    /// </summary>
    public sealed class BiomeTintDefinition
    {
        /// <summary>Which climate palette tints the faces: <c>"grass"</c> (the default) or
        /// <c>"foliage"</c> (leaves, a little darker); see <see cref="BiomeTintPalette"/>.</summary>
        public string? Palette { get; set; }

        /// <summary>
        /// Face name to what that face wears. Keys are the Minecraft face names
        /// <c>north</c>/<c>south</c>/<c>west</c>/<c>east</c>/<c>up</c>/<c>down</c> plus the
        /// shorthands <c>side</c> (the four horizontal faces) and <c>all</c>; a face may be named
        /// only once, shorthands included.
        /// </summary>
        public Dictionary<string, BiomeTintFaceDefinition>? Faces { get; set; }
    }

    /// <summary>
    /// One face of a <see cref="BiomeTintDefinition"/>. Exactly one of <see cref="Tile"/> and
    /// <see cref="Overlay"/> is set; <see cref="Base"/> is only legal with an overlay.
    /// </summary>
    public sealed class BiomeTintFaceDefinition
    {
        /// <summary>A tinted tile drawn <em>instead</em> of the model's face (one quad).</summary>
        public string? Tile { get; set; }

        /// <summary>The untinted tile beneath an overlay, replacing the model's face tile
        /// (grass: dirt). Omitted, the model's own face tile stays underneath.</summary>
        public string? Base { get; set; }

        /// <summary>A tinted cutout tile drawn <em>over</em> the face (two coplanar quads: the
        /// untinted base, then this).</summary>
        public string? Overlay { get; set; }
    }

    /// <summary>Voxel light behaviour of a block.</summary>
    public sealed class LightDefinition
    {
        /// <summary>Light this block emits, 0-15.</summary>
        public byte? Emission { get; set; }

        /// <summary>Colour of the emitted light, sRGB [r, g, b] in 0..255; defaults to the warm
        /// torch colour (<see cref="BlockLightPalette.DefaultSrgb"/>). Only legal with emission &gt; 0.</summary>
        public float[]? Color { get; set; }

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
