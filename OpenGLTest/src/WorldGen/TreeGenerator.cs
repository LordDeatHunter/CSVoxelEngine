using System;
using System.Linq;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The worldgen front-end over the tree growth engine (<see cref="TreeGrower"/>): decides which
/// columns root a tree and of which <see cref="TreeSpecies"/> (each species' <c>worldgen</c>
/// rules), grows every tree to maturity over the terrain heightmap and stamps the rasterized
/// cells into chunks. The trees of one 64x64 XZ cell grow together (<see cref="TreeBatch"/>),
/// each avoiding the cells of the batch's earlier trees and the SOLO shapes (one tree grown
/// alone, cached per trunk) of the neighbor cells' trees. Like <see cref="WormCarver"/>'s
/// cells, a batch is a pure function of its cell key: any chunk stamps the share of every
/// nearby batch that crosses it, so neighbors agree without shared state. Growth only uses cells above the terrain
/// heightmap, wood only replaces air or leaves and leaves only settle into air, so trees never
/// bury terrain and caves (carved before this runs) never lose rock to them.
/// The solo shapes and cell batches live in <see cref="TreeBatches"/>, the chunk stamp in
/// <see cref="TreeStamper"/> and the far-LOD canopy in <see cref="TreeCanopy"/>; this class keeps
/// the placement and forwards their public entry points.
/// </summary>
public static class TreeGenerator
{
    // One placement stream for every species, so spacing compares like with like across a biome
    // border and the gate order is identical for all of them.
    private const int PlacementSalt = 0x7E5715;

    /// <summary>Poison radius for a single mined or blasted canopy cell: its own column and the
    /// ring the far-LOD cell sampling can straddle.</summary>
    public const int SingleEditPoisonRadius = 1;

    /// <summary>Poison radius for a felled tree: everything any tree can reach from its trunk.</summary>
    public static int FellPoisonRadius => ReachHorizontal;

    /// <summary>Every registered species in ordinal name order (never dictionary order).</summary>
    private static readonly TreeSpecies[] Species;

    /// <summary>The worldgen species of each biome (indexed by the enum value), null where none grows.</summary>
    internal static readonly TreeSpecies?[] SpeciesByBiome;

    /// <summary>The species planted inside an oasis disc, or null when none declares
    /// <c>worldgen.oasis</c>.</summary>
    private static readonly TreeSpecies? OasisSpecies;

    /// <summary>The species planted at a column: an oasis' own palm inside a disc, else the
    /// biome's ordinary tree. The one rule the placement, the shape cache and the batch all read,
    /// so they never disagree about what grows in a column.</summary>
    private static TreeSpecies? SpeciesAt(int x, int z) =>
        OasisSpecies != null && OasisGenerator.IsDisc(x, z)
            ? OasisSpecies
            : SpeciesByBiome[(int)TerrainGenerator.BiomeAt(x, z)];

    internal static readonly bool[] LeafById = new bool[256];

    // Wood and leaves of every species with a canopy: what disturbs the far-LOD canopy. A
    // leafless species (cactus) has no canopy, so its wood is left out, as before.
    private static readonly bool[] CanopyBlockById = new bool[256];

    // The highest worldgen density: a column whose placement hash is at or above it can never
    // root anything, so it is rejected before any noise is read.
    private static readonly float MaxDensity;

    /// <summary>How far horizontally any worldgen tree reaches from its trunk (<see cref="TreeRasterizer.ReachOf"/>).</summary>
    public static readonly int ReachHorizontal;

    /// <summary>How far above its base cell any worldgen tree reaches.</summary>
    public static readonly int ReachUp;

    /// <summary><see cref="ReachHorizontal"/> in whole chunks, rounded up.</summary>
    public static readonly int ReachChunkRadius;

    /// <summary>Leaf block the far LOD falls back to: the first leafy worldgen species' leaves (a species
    /// kept out of worldgen never sets it, so adding one leaves the far LOD unchanged).</summary>
    public static readonly byte LeavesBlock;

    // The widest LOD crown proxy of any leafy worldgen species: how far outside a chunk column
    // the proxy sweep looks for trunks whose crown can overhang it.
    internal static readonly int ProxyReach;

    // Explicit so the tables are built the first time anything touches this class (after
    // ContentRegistry.Load), not at whatever point beforefieldinit would allow
    static TreeGenerator()
    {
        Species = TreeSpeciesRegistry.All.OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();

        var biomes = Enum.GetValues<Biome>();
        SpeciesByBiome = new TreeSpecies?[biomes.Max(b => (int)b) + 1];
        foreach (var biome in biomes)
            // An oasis species is never the biome's ordinary tree: it is admitted inside a disc
            // instead (OasisSpecies below)
            SpeciesByBiome[(int)biome] = Species.FirstOrDefault(s =>
                s.Worldgen?.GrowsIn(biome) == true && s.Worldgen.Oasis == false);
        OasisSpecies = Species.FirstOrDefault(s => s.Worldgen?.Oasis == true);

        foreach (var species in Species)
        {
            if (!species.HasLeaves) continue;
            LeafById[species.LeavesId] = true;
            CanopyBlockById[species.LeavesId] = true;
            CanopyBlockById[species.WoodId] = true;
            if (LeavesBlock == BlockType.Air && species.Worldgen != null) LeavesBlock = species.LeavesId;
        }

        foreach (var species in Species)
        {
            if (species.Worldgen == null) continue;
            MaxDensity = Math.Max(MaxDensity, species.Worldgen.Density);
            var (horizontal, up, _) = TreeRasterizer.ReachOf(species);
            ReachHorizontal = Math.Max(ReachHorizontal, horizontal);
            ReachUp = Math.Max(ReachUp, up);
        }

        ReachChunkRadius = (ReachHorizontal + Chunk.Size - 1) / Chunk.Size;

        foreach (var species in Species)
            if (species.HasLeaves && species.Worldgen != null)
                ProxyReach = Math.Max(ProxyReach, species.CrownRadius);

        if (2 * ReachHorizontal > WorldGenerationConfig.TreeCellBlocks)
            throw new InvalidOperationException(
                $"Tree reach {ReachHorizontal} x 2 exceeds the {WorldGenerationConfig.TreeCellBlocks}-block batch cell; " +
                "a batch's neighbor obstacles must come from the 8 adjacent cells only.");
    }

    /// <summary>Stamps every tree whose volume can intersect the chunk at (worldX0, worldY0, worldZ0) (<see cref="TreeStamper.Stamp"/>).</summary>
    public static void Stamp(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0) =>
        TreeStamper.Stamp(blocks, ref state, worldX0, worldY0, worldZ0);

    /// <summary>
    /// Whether (x, z) roots a tree, of which species and at which base cell (the air cell over
    /// the column's ground). The gates run cheapest first and are side-effect free: the
    /// placement hash against the highest density, the biome's species, the spawn clearing, the
    /// species' density and openness thinning, lowest-hash spacing, then water, soil, ground
    /// height and slope.
    /// </summary>
    public static bool TryGetTree(int x, int z, out TreeSpecies species, out int baseY)
    {
        species = null!;
        baseY = 0;

        var own = PlacementHash(x, z);
        if (own >= MaxDensity)
            return false;

        var found = SpeciesAt(x, z);
        if (found?.Worldgen == null)
            return false;
        var rules = found.Worldgen;

        // Keep the origin spawn in the open (see TreeSpawnClearRadius)
        if (rules.SpawnClear
            && Math.Abs(x) <= WorldGenerationConfig.TreeSpawnClearRadius
            && Math.Abs(z) <= WorldGenerationConfig.TreeSpawnClearRadius)
            return false;

        if (own >= rules.Density)
            return false;

        // Open regions thin the trees (see TerrainGenerator.ForestOpenness), each species by its
        // own share of it. The mask is read only for the few columns that already pass the raw
        // density, so the common case never pays for the noise.
        if (rules.Openness > 0f
            && own >= rules.Density * (1f - rules.Openness * TerrainGenerator.ForestOpenness(x, z)))
            return false;

        var s = rules.Spacing;
        for (var dx = -s; dx <= s; dx++)
        for (var dz = -s; dz <= s; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            if (PlacementHash(x + dx, z + dz) < own)
                return false;
        }

        var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        var li = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
        var height = column.Heights[li];
        // Nothing grows in water; a trunk in a lake or river would still poke its crown out of
        // the surface. A dry basin below the waterline is land, so the test is the wet one.
        if (TerrainGenerator.IsWaterColumn(x, z, height))
            return false;
        if (column.DirtDepth[li] < rules.MinSoil)
            return false;
        if (height < rules.MinGround || height > rules.MaxGround)
            return false;

        var slope = rules.MaxSlope;
        if (Math.Abs(HeightAt(x + 1, z) - height) > slope
            || Math.Abs(HeightAt(x - 1, z) - height) > slope
            || Math.Abs(HeightAt(x, z + 1) - height) > slope
            || Math.Abs(HeightAt(x, z - 1) - height) > slope)
            return false;

        species = found;
        baseY = height + 1;
        return true;
    }

    // The raw placement bits of a column: PlacementHash derives the placement float from them, and
    // any other per-tree roll (the LOD crown proxy) derives its own, so they all share one source.
    private static ulong PlacementBits(int x, int z) =>
        SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ PlacementSalt);

    /// <summary>The column's placement hash in [0, 1): the density gate, spacing and batch order.</summary>
    internal static float PlacementHash(int x, int z) =>
        (SplitMix64.Mix(PlacementBits(x, z) + 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));

    private const ulong CrownSalt = 0xC0A5E1D5A17UL;

    /// <summary>
    /// The far-LOD proxy crown height of the tree rooted at (x, z), in [CrownHeightMin,
    /// CrownHeightMax]: rolled from the same placement bits as the placement itself, so it is a
    /// pure function of (x, z, seed) and the proxy and the real path agree on which column holds
    /// which tree.
    /// </summary>
    internal static int CrownHeightOf(TreeSpecies species, int x, int z)
    {
        // 24 bits over 2^24: strictly below 1, so the roll never lands one past the range
        var roll = (SplitMix64.Mix(PlacementBits(x, z) ^ CrownSalt) >> 40) * (1f / (1 << 24));
        return species.CrownHeightMin + (int)(roll * (species.CrownHeightMax - species.CrownHeightMin + 1));
    }

    /// <summary>
    /// The tree batch cell holding a coordinate. Arithmetic shift is floor division, so negative
    /// coordinates group into the right cell (<c>/</c> would round toward zero and split cell -1 in two).
    /// </summary>
    internal static int CellOf(int coord) => coord >> WorldGenerationConfig.TreeCellShift;

    internal static int HeightAt(int x, int z) =>
        ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            .Heights[((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];

    /// <summary>Whether a block id is the leaves of any tree species.</summary>
    public static bool IsLeaf(byte blockId) => LeafById[blockId];

    /// <summary>Wood or leaves of a species with a canopy: the blocks whose removal disturbs the
    /// far-LOD canopy. A leafless species' wood (cactus) is excluded because it has no canopy.</summary>
    public static bool IsTreeBlock(byte blockId) => CanopyBlockById[blockId];

    /// <summary>Highest canopy cell over the ground column (x, z), or 0 under open sky (<see cref="TreeCanopy.CanopyTop"/>).</summary>
    public static int CanopyTop(int x, int z) => TreeCanopy.CanopyTop(x, z);

    /// <summary>The leaf block wearing the canopy top over (x, z), air where there is none (<see cref="TreeCanopy.CanopyLeafBlock"/>).</summary>
    public static byte CanopyLeafBlock(int x, int z) => TreeCanopy.CanopyLeafBlock(x, z);

    /// <summary>The grown canopy over (x, z) (<see cref="TreeCanopy.TryGetCanopy(int, int, out int, out byte)"/>).</summary>
    public static bool TryGetCanopy(int x, int z, out int top, out byte leaf) =>
        TreeCanopy.TryGetCanopy(x, z, out top, out leaf);

    /// <summary>The grown or proxy canopy over (x, z) (<see cref="TreeCanopy.TryGetCanopy(int, int, bool, out int, out byte)"/>).</summary>
    public static bool TryGetCanopy(int x, int z, bool proxy, out int top, out byte leaf) =>
        TreeCanopy.TryGetCanopy(x, z, proxy, out top, out leaf);

    /// <summary>Marks the canopy columns within Chebyshev <paramref name="radius"/> of (x, z) as disturbed (<see cref="TreeCanopy.PoisonCanopy"/>).</summary>
    public static void PoisonCanopy(int x, int z, int radius) => TreeCanopy.PoisonCanopy(x, z, radius);

    /// <summary>Records the crown a reactive growth left over ground column (x, z) for the far LOD; call after <see cref="PoisonCanopy"/> (<see cref="TreeCanopy.SetCanopyAdded"/>).</summary>
    public static bool SetCanopyAdded(int x, int z, int top, byte leaf) => TreeCanopy.SetCanopyAdded(x, z, top, leaf);

    /// <summary>
    /// Drops the cached tree shapes, cell batches and canopy tops (real and proxy); only <see cref="WorldSeed.Apply"/> (and
    /// tests) call it, since both are a function of the seed.
    /// </summary>
    internal static void ResetCaches()
    {
        TreeBatches.ResetCaches();
        TreeCanopy.ResetCaches();
    }
}
