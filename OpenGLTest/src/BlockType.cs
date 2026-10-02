using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace silkgltriangle;

/// <summary>
/// Everything the engine knows about one block, loaded from
/// <c>assets/data/blocks/&lt;name&gt;.json</c> by <see cref="ContentRegistry"/>. Instances are
/// created once at startup and never mutated afterwards (the few <c>internal set</c> members
/// are filled during the load/bake sequence, before any world thread exists), so meshing
/// threads can read them freely.
///
/// The static side is the hot-path view: plain arrays indexed by block id, so face culling and
/// AO cost an array read instead of a dictionary lookup.
/// </summary>
public sealed record BlockType
{
    /// <summary>Air is not a registered block; id 0 always means "nothing here".</summary>
    public const byte Air = 0;

    /// <summary>Damage a block takes before it breaks; one hit deals the held tool's power.
    /// See <see cref="Mining"/>.</summary>
    public const float DefaultHardness = 5f;
    public const float DefaultFriction = 0.999f;

    /// <summary>Every registered block by id; null where no block owns that id.</summary>
    public static readonly BlockType?[] ById = new BlockType?[256];

    /// <summary>Light emitted by each block id (0-15), indexed like <see cref="ById"/>.</summary>
    public static readonly byte[] Emission = new byte[256];

    /// <summary>Block-light palette slot of each block id's emitted colour (0 = the default
    /// warm colour), indexed like <see cref="ById"/>; see <see cref="BlockLightPalette"/>.</summary>
    public static readonly byte[] LightSlot = new byte[256];

    /// <summary>Whether voxel light travels through each block id; see Chunk's light passes.</summary>
    public static readonly bool[] PassesLightById = new bool[256];

    /// <summary>Every registered block keyed by id, for code that enumerates types.</summary>
    public static IReadOnlyDictionary<byte, BlockType> Types { get; internal set; } =
        new Dictionary<byte, BlockType>();

    // bool[256] mirrors of the per-block flags the mesher asks about by neighbour id
    internal static readonly bool[] SolidById = new bool[256];
    internal static readonly bool[] CollidesByHitboxById = new bool[256];
    internal static readonly bool[] OccludesById = new bool[256];
    internal static readonly bool[] CastsAoById = new bool[256];

    // Stack blocks whose solidity depends on their state byte (snow layers): the state at which
    // they become solid, or 0 when solidity is state-independent like every other block.
    internal static readonly byte[] StackSolidStateById = new byte[256];

    // How fast the player moves through each block while inside it; 0 is a solid collider.
    internal static readonly float[] MovementSpeedById = new float[256];
    internal static readonly float[] FrictionById = new float[256];

    // Natural pebble scatter density multiplier of each block as an exposed surface: 0 means
    // this surface never grows pebbles. See PebbleGenerator.
    internal static readonly float[] PebbleWeightById = new float[256];

    // Whether the block is the reworked trees' wood (the tree_wood renderer), whose thickness
    // and parent direction live in the state byte. Read per voxel by the mesher's connection
    // test and per DDA step by the raycast, so flat like the other flags.
    internal static readonly bool[] TreeWoodById = new bool[256];

    // Whether the binary greedy mesher may merge this block's faces: a plain full cube whose
    // six faces are the untransformed cube faces (see ContentRegistry.BakesGreedy). Anything
    // else goes through the per-quad scalar path.
    internal static readonly bool[] GreedyById = new bool[256];

    // The fluid simulation asks these of every neighbour cell it looks at, so they get the
    // same flat-array treatment as the mesher's flags
    internal static readonly bool[] FluidById = new bool[256];
    internal static readonly bool[] ReplaceableById = new bool[256];

    // Whether a placement takes this block's own cell over instead of filling the neighbour
    // across the hit face (layered snow); only ever fires on a non-solid state.
    internal static readonly bool[] ReplaceableByPlacementById = new bool[256];

    // Per-fluid simulation tuning (see BlockDefinition.FlowDefinition): ticks between updates,
    // sideways spread range, and whether adjacent sources multiply. Water's defaults.
    internal static readonly byte[] FluidTickDivisorById = new byte[256];
    internal static readonly byte[] FluidRangeById = new byte[256];
    internal static readonly bool[] FluidInfiniteById = new bool[256];

    // Contact hardening: a fluid cell holding HardensTouchingId next to it becomes HardensToId
    // (lava beside water becomes cobblestone). Zero means the fluid never hardens.
    internal static readonly byte[] HardensToById = new byte[256];
    internal static readonly byte[] HardensTouchingById = new byte[256];

    // Gravity checks run per block edit (not per voxel), but the flat shape keeps the
    // support test a single array read like the other per-id questions
    internal static readonly bool[] FallsById = new bool[256];

    /// <summary>String id: the block's file name under <c>assets/data/blocks</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Display name for inventory rows and preview headings.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Byte id assigned at load; what chunk storage holds.</summary>
    public required byte Id { get; init; }

    /// <summary>Sound group played when this block is mined or hit (see <see cref="SfxManager"/>),
    /// resolved from the block's <c>sfx</c> JSON and defaulting to
    /// <see cref="SfxLibrary.DefaultGroup"/>.</summary>
    public string Sfx { get; init; } = SfxLibrary.DefaultGroup;

    /// <summary>Damage this block takes before breaking; one hit deals the held tool's power.</summary>
    public float Hardness { get; init; } = DefaultHardness;

    /// <summary>Whether the <see cref="Tools"/> gate refuses hits from the wrong tool kind or a
    /// tool below the required power; false lets any tool or bare hand mine it.</summary>
    public bool RequiresTool { get; init; }

    /// <summary>Tool tag name to minimum power the held item must provide, or null when the
    /// block sets none. Only read while <see cref="RequiresTool"/> is set; see <see cref="Mining"/>.</summary>
    public IReadOnlyDictionary<string, float>? Tools { get; init; }

    /// <summary>Whether the block collides with bodies.</summary>
    public bool Solid { get; init; } = true;

    /// <summary>
    /// How fast the player moves through this block, 0..1. <c>0</c> (the default) is a solid
    /// collider; a positive value makes the block passable and scales the player's speed while
    /// the body overlaps it (leaves <c>0.75</c>), with <c>1</c> passable at full speed.
    /// </summary>
    public float MovementSpeed { get; init; }
    public float Friction { get; init; } = DefaultFriction;

    /// <summary>
    /// Density multiplier (0..1) for the natural pebble scatter when this block is the exposed
    /// surface under the pebble cell; <c>0</c> means this surface never grows pebbles. See
    /// <see cref="PebbleGenerator"/>.
    /// </summary>
    public float PebbleWeight { get; init; }

    /// <summary>Whether the block hides a neighbouring full cube's face. Defaults to "the baked
    /// model is a full cube and it is not cutout", so it is only known after baking.</summary>
    public bool OccludesNeighbours { get; internal set; }

    /// <summary>Whether faces are also culled against a neighbour of the same block (glass).</summary>
    public bool CullSelf { get; init; }

    /// <summary>Whether the block darkens neighbouring faces via ambient occlusion. Defaults to
    /// <see cref="Solid"/>.</summary>
    public bool CastsAmbientOcclusion { get; internal set; }

    /// <summary>Alpha-tested texture (leaves); forces <see cref="OccludesNeighbours"/> off, or a
    /// face culled behind it becomes an X-ray window through the texture's holes.</summary>
    public bool Cutout { get; init; }

    /// <summary>Drawn blended in the sorted translucent pass together with the fluids;
    /// forces <see cref="OccludesNeighbours"/> off.</summary>
    public bool Translucent { get; init; }

    /// <summary>Whether the vertex shader gives this block the leaf-wind sway.</summary>
    public bool Sway { get; init; }

    /// <summary>
    /// Which faces wear a climate-tinted tile and from which palette (grass, leaves), or null when
    /// every face is drawn as the model says. Set in <c>ContentRegistry.BakeModels</c>, and only
    /// ever non-null on a block that bakes greedy (<see cref="IsGreedy"/>), so both mesh paths may
    /// treat its faces as plain cube faces. <see cref="FaceTiles"/> keep the default coloured
    /// tiles, which is what icons, drops and the held block wear.
    /// </summary>
    public BiomeTintFaces? BiomeTint { get; internal set; }

    /// <summary>
    /// How reflective the block's surface is, or null for the ordinary diffuse blocks (all but
    /// the ores). The renderer never reads this per block: <see cref="TileMaterials"/> spreads it
    /// across the block's <see cref="FaceTiles"/> at bake time and the shader looks it up by
    /// atlas slice, which is what keeps the feature off the vertex entirely.
    /// </summary>
    public TileMaterial? Material { get; init; }

    /// <summary>Light this block emits, 0-15.</summary>
    public byte LightEmission { get; init; }

    /// <summary><see cref="BlockLightPalette"/> slot of the emitted light's colour; 0 is the default.</summary>
    public byte LightColorSlot { get; init; }

    /// <summary>Whether voxel light travels through it; defaults to "not a full cube".</summary>
    public bool PassesLight { get; internal set; }

    /// <summary>Which geometry emitter turns this block into chunk vertices.</summary>
    public BlockRenderer Renderer { get; init; } = BlockRenderer.Model;

    /// <summary>Block filling the seven non-anchor cells of this multiblock's 2x2x2 volume, or
    /// 0 when the block is not a <see cref="BlockRenderer.Multiblock"/> anchor. Resolved after
    /// every block is loaded, since the part's own file may come later alphabetically.</summary>
    public byte MultiblockPartId { get; internal set; }

    /// <summary>Plain block this multiblock is built from and reverts to (mud bricks for the
    /// kiln), or 0 when the block is not a multiblock anchor.</summary>
    public byte MultiblockSourceId { get; internal set; }

    /// <summary>Whether this multiblock anchor is a kiln: it holds fuel, can be lit and is
    /// swept for structural damage. Only ever set on a <see cref="BlockRenderer.Multiblock"/>
    /// block.</summary>
    public bool Kiln { get; init; }

    /// <summary>
    /// The anchor's baked model pre-rotated to each of the four horizontal facings, indexed by
    /// facing 0..3 (<c>[0]</c> is <see cref="BakedModel"/> itself, the authored north-facing
    /// bake). Null for every block but a <see cref="BlockRenderer.Multiblock"/> anchor; see
    /// <see cref="MultiblockShape.Rotate"/>.
    /// </summary>
    public BakedModel[]? RotatedModels { get; internal set; }

    /// <summary>
    /// How many units of this block stack in one cell, candle-style (pebbles hold up to 3,
    /// branch piles up to 4);
    /// 0 for an ordinary single-state block. The count lives in the chunk's state byte, where
    /// 0 means "untouched worldgen" (the generator's own default) and 1..StackMax is an
    /// explicit player-built pile. Never set on fluids (their state holds levels instead).
    /// </summary>
    public byte StackMax { get; init; }

    /// <summary><see cref="BlockRenderer.TreeWood"/> only: the share of <see cref="Hardness"/>
    /// the thinnest wood keeps, rising linearly to the full hardness at a full cube (see
    /// <see cref="Mining.HardnessOf"/>).</summary>
    public float TipHardness { get; init; } = 0.25f;

    /// <summary><see cref="BlockRenderer.TreeWood"/> only: the loot each thickness band rolls,
    /// ascending and starting at 0 (see <see cref="WoodLootBand.Select"/>). The default drops
    /// the block itself at every thickness.</summary>
    public WoodLootBand[] WoodLootBands { get; init; } = [new(0, null, true)];

    /// <summary>Whether the block gets an inventory slot and can be held.</summary>
    public bool HasItem { get; init; } = true;

    /// <summary>Whether the block pops (with its loot) when the block below is removed.</summary>
    public bool NeedsSupport { get; init; }

    /// <summary>Whether a placement aimed at this block fills its own cell and discards it
    /// (layered snow) instead of filling the cell across the hit face. Only a non-solid state can
    /// be taken over; see <see cref="IsReplaceableByPlacement"/>.</summary>
    public bool ReplaceableByPlacement { get; init; }

    /// <summary>Whether the block is gravity-affected: it topples into a falling entity when
    /// the cell below it stops being solid, and a placed one falls immediately.</summary>
    public bool Falls { get; init; }

    /// <summary>Whether the block is a flowing fluid. Fluid cells carry a level in the chunk's
    /// state byte (see <see cref="FluidState"/>), are replaceable by placements and flows, and
    /// never collide, cast AO, drop loot or take an inventory slot. Named like
    /// <see cref="Solid"/> rather than "IsFluid", which the per-id predicate below owns.</summary>
    public bool Fluid { get; init; }

    /// <summary>Ticks between this fluid's simulation updates; 1 is water speed, 2 half of it.</summary>
    public byte FluidTickDivisor { get; init; } = 1;

    /// <summary>Sideways spread in blocks before the skirt dries up; water flows 7.</summary>
    public byte FluidRange { get; init; } = 7;

    /// <summary>Whether adjacent sources of this fluid birth a new source.</summary>
    public bool FluidInfinite { get; init; } = true;

    /// <summary>Block id this fluid becomes on touching <see cref="HardensTouchingId"/>;
    /// 0 when the fluid never hardens.</summary>
    public byte HardensToId { get; internal set; }

    /// <summary>Fluid block id whose contact hardens this one; 0 when it never hardens.</summary>
    public byte HardensTouchingId { get; internal set; }

    /// <summary>What happens when the block is destroyed.</summary>
    public BlockBreakAction OnBreak { get; init; } = BlockBreakAction.Drop;

    /// <summary>
    /// Selection boxes in cell-local 0..1 space, from the block's <c>"hitbox"</c> JSON.
    /// A single full-cube entry when the author wrote nothing. The hover outline draws
    /// all of them; the raycast targets the cell only through one of them.
    /// </summary>
    public BlockHitbox[] Hitboxes { get; init; } = [BlockHitbox.FullCube];

    /// <summary>Whether body collision tests this block's <see cref="Hitboxes"/> rather than the
    /// full cell, the way tree wood tests its drawn shape; see
    /// <see cref="TreeWoodShape.OverlapsShape"/>.</summary>
    public bool CollidesByHitbox { get; init; }

    /// <summary>Loot rolled on break. Null means "drop yourself";
    /// <see cref="silkgltriangle.LootTable.None"/> means "drop nothing".</summary>
    public LootTable? LootTable { get; init; }

    /// <summary>The block's model, baked to atlas-ready quads at startup.</summary>
    public BakedModel? BakedModel { get; internal set; }

    /// <summary>Atlas slice per face in <see cref="Chunk.CubeFaces"/> order: -Z, +Z, -X, +X, +Y, -Y.
    /// Empty until the models are baked.</summary>
    public ushort[] FaceTiles { get; internal set; } = [];

    /// <summary>
    /// Every atlas slice a <see cref="Cutout"/> block samples (its baked quads' tiles plus its
    /// <see cref="FaceTiles"/>) plus every alpha-tested <see cref="BiomeTint"/> tile (an overlay,
    /// or a cutout block's tinted replacement), distinct and ascending: the slices whose mips
    /// <see cref="CutoutMipmap"/> rebuilds so alpha-tested coverage survives minification.
    /// Must be called after <c>ContentRegistry.BakeModels</c>, which is what fills the tiles.
    /// </summary>
    public static IReadOnlyList<ushort> CutoutTiles()
    {
        var tiles = new SortedSet<ushort>();
        foreach (var type in ById)
        {
            if (type is not { Cutout: true }) continue;
            if (type.BakedModel is { } model)
                foreach (var quad in model.Quads) tiles.Add(quad.Tile);
            foreach (var tile in type.FaceTiles) tiles.Add(tile);
        }

        // An overlay is alpha-tested wherever it is drawn, and a cutout block's replacement tile
        // keeps the leaf holes, so both need coverage-preserving mips like a leaf tile.
        foreach (var type in ById)
        {
            if (type?.BiomeTint is not { } bt) continue;
            for (var f = 0; f < 6; f++)
                if (bt.IsTinted(f) && (bt.IsOverlay(f) || type.Cutout))
                    tiles.Add(bt.Tinted[f]);
        }
        return [.. tiles];
    }

    /// <summary>Tile of the +Y face; shorthand the procedural emitters read.</summary>
    public ushort Top { get; internal set; }

    /// <summary>Tile of the -Y face.</summary>
    public ushort Bottom { get; internal set; }

    /// <summary>Tile of the side faces (-Z is representative).</summary>
    public ushort Side { get; internal set; }

    /// <summary>Whether a block is solid to bodies. The raycast still targets non-solid blocks
    /// (it hits any non-air cell), so this only gates movement and drop physics.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSolid(byte blockId) => SolidById[blockId];

    /// <summary>Whether the body collides with the block's <see cref="Hitboxes"/> instead of the
    /// whole cell (a mold slab). Named <c>...Of</c> because a static method may not share the
    /// instance property's name; mirrors <see cref="MovementSpeedOf"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CollidesByHitboxOf(byte blockId) => CollidesByHitboxById[blockId];

    /// <summary>
    /// Whether a cell collides with bodies, honouring a stack block whose partial stacks are
    /// walked through: a layered-snow cell is solid once its state reaches
    /// <see cref="StackSolidStateById"/>, and every other block answers exactly as
    /// <see cref="IsSolid"/>. Callers that only have an id still use <see cref="IsSolid"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSolidCell(byte blockId, byte state) =>
        StackSolidStateById[blockId] != 0 ? state >= StackSolidStateById[blockId] : SolidById[blockId];

    /// <summary>
    /// Speed the player moves through a block while inside it, 0 for a solid collider. A
    /// positive value also means the block never blocks the body.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float MovementSpeedOf(byte blockId) => MovementSpeedById[blockId];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FrictionOf(byte blockId) => FrictionById[blockId];

    /// <summary>Natural pebble scatter density multiplier of the block as an exposed surface;
    /// <c>0</c> means the surface never grows pebbles. See <see cref="PebbleGenerator"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float PebbleWeightOf(byte blockId) => PebbleWeightById[blockId];

    /// <summary>Whether a block fully hides a neighbouring full cube's face.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Occludes(byte blockId) => OccludesById[blockId];

    /// <summary>Whether a block darkens neighbouring faces via ambient occlusion. This is the
    /// raw JSON flag (<c>castsAO</c>, defaulting to <see cref="Solid"/>); meshing must read
    /// <see cref="OccludesAO"/> instead, so a thin shape can never darken a face it does not
    /// fully block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CastsAO(byte blockId) => CastsAoById[blockId];

    /// <summary>Whether a block actually blocks a neighbouring face's corner/edge for ambient
    /// occlusion: only a fully occupying block (see <see cref="Occludes"/>) counts. Leaves pass
    /// light and thin shapes (tree wood, pebbles, flowers) never fill the corner voxel, so
    /// they leave AO at full brightness even when their JSON flag is set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool OccludesAO(byte blockId) => OccludesById[blockId] && CastsAoById[blockId];

    /// <summary>Whether a block id is tree wood (<see cref="BlockRenderer.TreeWood"/>), whose
    /// state byte is a <see cref="WoodState"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTreeWood(byte blockId) => TreeWoodById[blockId];

    /// <summary>Whether the greedy mesher handles this block (a plain full cube in the
    /// <see cref="BlockRenderer.Model"/> renderer); false sends it down the per-quad path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsGreedy(byte blockId) => GreedyById[blockId];

    /// <summary>Whether a block id is a flowing fluid; see <see cref="IsFluid"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFluid(byte blockId) => FluidById[blockId];

    /// <summary>Whether a fluid or a placement may overwrite the cell without destroying
    /// anything worth keeping: air, plus the fragile <see cref="NeedsSupport"/> blocks, which
    /// pop (rolling their loot) when water washes over them. Fluids themselves are not
    /// replaceable here — a flow into an existing fluid cell is a level recompute, not a
    /// write — but every caller that displaces water tests <see cref="IsFluid(byte)"/> too.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsReplaceable(byte blockId) => ReplaceableById[blockId];

    /// <summary>Whether a placement aimed at this block fills the block's own cell and discards it,
    /// instead of filling the neighbour across the hit face. Opted into with the block's
    /// <c>replaceableByPlacement</c> JSON, and only while the cell is not solid: a partial
    /// layered-snow drift is taken over, a full stack is a real block (see
    /// <see cref="IsSolidCell"/>). Independent of <see cref="IsReplaceable"/>, which is the fluid
    /// flow and bucket pour path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsReplaceableByPlacement(byte blockId, byte state) =>
        ReplaceableByPlacementById[blockId] && !IsSolidCell(blockId, state);

    /// <summary>Whether a block id is gravity-affected; see <see cref="Falls"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFalling(byte blockId) => FallsById[blockId];
}
