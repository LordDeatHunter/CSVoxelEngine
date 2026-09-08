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

    /// <summary>Hits a block takes before it breaks; one hit lands per World.BlockHitSeconds.</summary>
    public const int DefaultMaxHealth = 5;

    /// <summary>Every registered block by id; null where no block owns that id.</summary>
    public static readonly BlockType?[] ById = new BlockType?[256];

    /// <summary>Light emitted by each block id (0-15), indexed like <see cref="ById"/>.</summary>
    public static readonly byte[] Emission = new byte[256];

    /// <summary>Whether voxel light travels through each block id; see Chunk's light passes.</summary>
    public static readonly bool[] PassesLightById = new bool[256];

    /// <summary>Wood block per taper level: index 0 is the full-cube log, 1..5 step branch down
    /// to twig. Built and validated by the registry from each block's <c>wood.taperLevel</c>.</summary>
    public static byte[] WoodByLevel { get; internal set; } = [];

    /// <summary>Every registered block keyed by id, for code that enumerates types.</summary>
    public static IReadOnlyDictionary<byte, BlockType> Types { get; internal set; } =
        new Dictionary<byte, BlockType>();

    // bool[256] mirrors of the per-block flags the mesher asks about by neighbour id
    internal static readonly bool[] SolidById = new bool[256];
    internal static readonly bool[] OccludesById = new bool[256];
    internal static readonly bool[] CastsAoById = new bool[256];

    // The fluid simulation asks these of every neighbour cell it looks at, so they get the
    // same flat-array treatment as the mesher's flags
    internal static readonly bool[] FluidById = new bool[256];
    internal static readonly bool[] ReplaceableById = new bool[256];

    // Gravity checks run per block edit (not per voxel), but the flat shape keeps the
    // support test a single array read like the other per-id questions
    internal static readonly bool[] FallsById = new bool[256];

    /// <summary>String id: the block's file name under <c>assets/data/blocks</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Byte id assigned at load; what chunk storage holds.</summary>
    public required byte Id { get; init; }

    public int MaxHealth { get; init; } = DefaultMaxHealth;

    /// <summary>Whether the block collides with bodies.</summary>
    public bool Solid { get; init; } = true;

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

    /// <summary>Light this block emits, 0-15.</summary>
    public byte LightEmission { get; init; }

    /// <summary>Whether voxel light travels through it; defaults to "not a full cube".</summary>
    public bool PassesLight { get; internal set; }

    /// <summary>Which geometry emitter turns this block into chunk vertices.</summary>
    public BlockRenderer Renderer { get; init; } = BlockRenderer.Model;

    /// <summary>Wood taper step, or -1 for anything that is not wood.</summary>
    public int WoodTaperLevel { get; init; } = -1;

    /// <summary>Half edge length of the tapered wood box in blocks, or -1 for non-wood.</summary>
    public float HalfWidth { get; init; } = -1f;

    /// <summary>Whether the block gets an inventory slot and can be held.</summary>
    public bool HasItem { get; init; } = true;

    /// <summary>Whether the block pops (with its loot) when the block below is removed.</summary>
    public bool NeedsSupport { get; init; }

    /// <summary>Whether the block is gravity-affected: it topples into a falling entity when
    /// the cell below it stops being solid, and a placed one falls immediately.</summary>
    public bool Falls { get; init; }

    /// <summary>Whether the block is a flowing fluid. Fluid cells carry a level in the chunk's
    /// state byte (see <see cref="FluidState"/>), are replaceable by placements and flows, and
    /// never collide, cast AO, drop loot or take an inventory slot. Named like
    /// <see cref="Solid"/> rather than "IsFluid", which the per-id predicate below owns.</summary>
    public bool Fluid { get; init; }

    /// <summary>What happens when the block is destroyed.</summary>
    public BlockBreakAction OnBreak { get; init; } = BlockBreakAction.Drop;

    /// <summary>Loot rolled on break. Null means "drop yourself";
    /// <see cref="silkgltriangle.LootTable.None"/> means "drop nothing".</summary>
    public LootTable? LootTable { get; init; }

    /// <summary>The block's model, baked to atlas-ready quads at startup.</summary>
    public BakedModel? BakedModel { get; internal set; }

    /// <summary>Tile per face in <see cref="Chunk.CubeFaces"/> order: -Z, +Z, -X, +X, +Y, -Y.
    /// Empty until the models are baked.</summary>
    public Point[] FaceTiles { get; internal set; } = [];

    /// <summary>Tile of the +Y face; shorthand the procedural emitters read.</summary>
    public Point Top { get; internal set; }

    /// <summary>Tile of the -Y face.</summary>
    public Point Bottom { get; internal set; }

    /// <summary>Tile of the side faces (-Z is representative).</summary>
    public Point Side { get; internal set; }

    /// <summary>Whether a block is solid to bodies. The raycast still targets non-solid blocks
    /// (it hits any non-air cell), so this only gates movement and drop physics.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSolid(byte blockId) => SolidById[blockId];

    /// <summary>Whether a block fully hides a neighbouring full cube's face.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Occludes(byte blockId) => OccludesById[blockId];

    /// <summary>Whether a block darkens neighbouring faces via ambient occlusion. Leaves count
    /// as solid here (unlike <see cref="Occludes"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CastsAO(byte blockId) => CastsAoById[blockId];

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

    /// <summary>Whether a block id is gravity-affected; see <see cref="Falls"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFalling(byte blockId) => FallsById[blockId];

    /// <summary>Half-width of a wood block's box in blocks (the log is 0.5), or -1 when the id
    /// is not wood at all.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float WoodHalfWidth(byte blockId) => ById[blockId]?.HalfWidth ?? -1f;

    public record struct Point(int X, int Y);
}
