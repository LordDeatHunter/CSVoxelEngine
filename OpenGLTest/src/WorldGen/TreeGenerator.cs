using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
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
    private const ulong DynamicSpeciesSalt = 0xD1A0C5EEC15UL;

    /// <summary>Poison radius for a single mined or blasted canopy cell: its own column and the
    /// ring the far-LOD cell sampling can straddle.</summary>
    public const int SingleEditPoisonRadius = 1;

    /// <summary>Poison radius for a felled tree: everything any tree can reach from its trunk.</summary>
    public static int FellPoisonRadius => ReachHorizontal;

    /// <summary>Fell radius for an explicitly present species, including a manually placed
    /// Dynamic-only giant in a Legacy world; ordinary Legacy trees retain their old bound.</summary>
    public static int FellPoisonRadiusFor(ushort woodId)
    {
        var species = TreeSpeciesRegistry.ForBlock(woodId);
        return species == null ? FellPoisonRadius : Math.Max(FellPoisonRadius, TreeRasterizer.ReachOf(species).Horizontal);
    }

    /// <summary>Every registered species in ordinal name order (never dictionary order).</summary>
    private static readonly TreeSpecies[] Species;

    /// <summary>The worldgen species of each biome (indexed by the enum value), null where none grows.</summary>
    internal static readonly TreeSpecies?[] SpeciesByBiome;

    /// <summary>The species planted inside an oasis disc, or null when none declares
    /// <c>worldgen.oasis</c>.</summary>
    private static readonly TreeSpecies? OasisSpecies;

    private sealed class ProfileTable
    {
        internal required TreeSpecies?[] Primary { get; init; }
        internal required TreeSpecies[][] Choices { get; init; }
        internal required TreeSpecies? Oasis { get; init; }
        internal required float MaxDensity { get; init; }
        internal required int ReachHorizontal { get; init; }
        internal required int ReachUp { get; init; }
        internal required int ReachChunkRadius { get; init; }
        internal required int ProxyReach { get; init; }
    }

    private static readonly ProfileTable LegacyTable;
    private static readonly ProfileTable DynamicTable;
    private static ProfileTable ActiveTable => WorldSeed.Profile == WorldGenProfile.Dynamic ? DynamicTable : LegacyTable;

    /// <summary>The species planted at a column: an oasis palm inside its disc, otherwise the
    /// Legacy first biome member or a separately rolled Dynamic eligible member.</summary>
    private static TreeSpecies? SpeciesAt(int x, int z)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic)
            return OasisSpecies != null && OasisGenerator.IsDisc(x, z)
                ? OasisSpecies
                : SpeciesByBiome[(int)TerrainGenerator.FloraBiomeAt(x, z)];

        if (DynamicTable.Oasis != null && OasisGenerator.IsDisc(x, z)) return DynamicTable.Oasis;
        return DynamicSpeciesFor(TerrainGenerator.FloraBiomeAt(x, z), x, z);
    }

    /// <summary>The immutable Dynamic row, exposed read-only for content contract checks.</summary>
    internal static IReadOnlyList<TreeSpecies> DynamicRowFor(Biome biome) =>
        Array.AsReadOnly(DynamicTable.Choices[(int)biome]);

    /// <summary>The selected species of an actual Dynamic biome row, without a terrain query.</summary>
    internal static TreeSpecies? DynamicSpeciesFor(Biome biome, int x, int z)
    {
        var choices = DynamicTable.Choices[(int)biome];
        if (choices.Length == 0) return null;
        return ChooseDynamicSpecies(choices, x, z);
    }

    /// <summary>The separately salted root choice, before any picked-species placement gate.</summary>
    internal static TreeSpecies ChooseDynamicSpecies(TreeSpecies[] choices, int x, int z)
    {
        if (choices.Length == 0) throw new ArgumentException("Need at least one eligible tree species.", nameof(choices));
        if (choices.Length == 1) return choices[0];
        double total = 0;
        foreach (var species in choices) total += species.Worldgen!.DynamicWeight!.Value;
        var roll = (SplitMix64.Mix(PlacementBits(x, z) ^ DynamicSpeciesSalt) >> 11) * (1.0 / (1UL << 53)) * total;
        foreach (var species in choices)
        {
            roll -= species.Worldgen!.DynamicWeight!.Value;
            if (roll < 0) return species;
        }
        return choices[^1];
    }

    // Leaves of every species, by id. Sized once at first touch (see the static constructor), so
    // a read must go through IsLeaf unless the id is known to be loaded content.
    internal static readonly bool[] LeafById;

    // Wood and leaves of every species with a canopy: what disturbs the far-LOD canopy. A
    // leafless species (cactus) has no canopy, so its wood is left out, as before.
    private static readonly bool[] CanopyBlockById;

    // The highest worldgen density: a column whose placement hash is at or above it can never
    // root anything, so it is rejected before any noise is read.
    private static float MaxDensity => ActiveTable.MaxDensity;

    /// <summary>How far horizontally any worldgen tree reaches from its trunk (<see cref="TreeRasterizer.ReachOf"/>).</summary>
    public static int ReachHorizontal => ActiveTable.ReachHorizontal;

    /// <summary>How far above its base cell any worldgen tree reaches.</summary>
    public static int ReachUp => ActiveTable.ReachUp;

    /// <summary><see cref="ReachHorizontal"/> in whole chunks, rounded up.</summary>
    public static int ReachChunkRadius => ActiveTable.ReachChunkRadius;

    /// <summary>Leaf block the far LOD falls back to: the first leafy worldgen species' leaves (a species
    /// kept out of worldgen never sets it, so adding one leaves the far LOD unchanged).</summary>
    public static readonly ushort LeavesBlock;

    // The widest LOD crown proxy of any leafy worldgen species: how far outside a chunk column
    // the proxy sweep looks for trunks whose crown can overhang it.
    internal static int ProxyReach => ActiveTable.ProxyReach;

    // Explicit so the tables are built the first time anything touches this class, not at
    // whatever point beforefieldinit would allow; RequireLoaded enforces that this is after
    // ContentRegistry.Load, so the per-id tables are sized for the loaded content
    static TreeGenerator()
    {
        BlockTables.RequireLoaded(nameof(TreeGenerator));
        LeafById = new bool[BlockTables.IdCount];
        CanopyBlockById = new bool[BlockTables.IdCount];

        Species = TreeSpeciesRegistry.All.OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();

        LegacyTable = BuildTable(WorldGenProfile.Legacy);
        DynamicTable = BuildTable(WorldGenProfile.Dynamic);
        SpeciesByBiome = LegacyTable.Primary;
        OasisSpecies = LegacyTable.Oasis;

        foreach (var species in Species)
        {
            if (!species.HasLeaves) continue;
            LeafById[species.LeavesId] = true;
            CanopyBlockById[species.LeavesId] = true;
            CanopyBlockById[species.WoodId] = true;
            if (LeavesBlock == BlockType.Air && species.Worldgen?.Allows(WorldGenProfile.Legacy) == true)
                LeavesBlock = species.LeavesId;
        }
    }

    private static ProfileTable BuildTable(WorldGenProfile profile)
    {
        var biomes = Enum.GetValues<Biome>();
        var primary = new TreeSpecies?[biomes.Max(b => (int)b) + 1];
        var choices = new TreeSpecies[primary.Length][];
        foreach (var biome in biomes)
        {
            var members = Species.Where(s => s.Worldgen?.Allows(profile) == true
                && s.Worldgen.GrowsIn(biome) && !s.Worldgen.Oasis).ToArray();
            primary[(int)biome] = members.FirstOrDefault();
            var weighted = profile == WorldGenProfile.Dynamic
                ? members.Where(s => s.Worldgen!.DynamicWeight.HasValue).ToArray()
                : [];
            choices[(int)biome] = profile == WorldGenProfile.Dynamic && weighted.Length > 0
                ? weighted
                : members.Take(1).ToArray();
            if (weighted.Length > 1)
            {
                var spacing = weighted[0].Worldgen!.Spacing;
                if (weighted.Any(s => s.Worldgen!.Spacing != spacing))
                    throw new InvalidOperationException($"Dynamic {biome} tree species need the same spacing.");
            }
        }

        var eligible = profile == WorldGenProfile.Dynamic
            ? choices.SelectMany(row => row ?? []).Concat(Species.Where(s => s.Worldgen?.Allows(profile) == true
                && s.Worldgen.Oasis)).Distinct().ToArray()
            : Species.Where(s => s.Worldgen?.Allows(profile) == true).ToArray();
        var reachHorizontal = 0;
        var reachUp = 0;
        var proxyReach = 0;
        var maxDensity = 0f;
        foreach (var species in eligible)
        {
            maxDensity = Math.Max(maxDensity, species.Worldgen!.DensityFor(profile));
            var (horizontal, up, _) = TreeRasterizer.ReachOf(species);
            if (species.HasLeaves && !TreeProxyReach.FitsGrownReach(species.CrownRadius, horizontal))
                throw new InvalidOperationException(
                    $"{profile} tree {species.Name} LOD crown radius {species.CrownRadius} exceeds grown horizontal reach {horizontal}.");
            reachHorizontal = Math.Max(reachHorizontal, horizontal);
            reachUp = Math.Max(reachUp, up);
            if (species.HasLeaves) proxyReach = Math.Max(proxyReach, species.CrownRadius);
        }
        if (2 * reachHorizontal > WorldGenerationConfig.TreeCellBlocks)
            throw new InvalidOperationException(
                $"{profile} tree reach {reachHorizontal} x 2 exceeds the {WorldGenerationConfig.TreeCellBlocks}-block batch cell.");
        return new ProfileTable
        {
            Primary = primary,
            Choices = choices,
            Oasis = eligible.FirstOrDefault(s => s.Worldgen!.Oasis),
            MaxDensity = maxDensity,
            ReachHorizontal = reachHorizontal,
            ReachUp = reachUp,
            ReachChunkRadius = (reachHorizontal + Chunk.Size - 1) / Chunk.Size,
            ProxyReach = proxyReach,
        };
    }

    /// <summary>Stamps every tree whose volume can intersect the chunk at (worldX0, worldY0, worldZ0) (<see cref="TreeStamper.Stamp"/>).</summary>
    public static void Stamp(ushort[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0) =>
        TreeStamper.Stamp(blocks, ref state, worldX0, worldY0, worldZ0);

    /// <summary>
    /// Whether (x, z) roots a tree, of which species and at which base cell (the air cell over
    /// the column's ground). The gates run cheapest first and are side-effect free: the
    /// placement hash against the profile's highest density, species choice, the spawn clearing,
    /// species density and openness thinning, lowest-hash spacing, then water, soil, ground
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
        var reach = TreeRasterizer.ReachOf(found).Horizontal;
        if (FeatureExclusion.Intersects(x - reach, z - reach, x + reach, z + reach))
            return false;
        var rules = found.Worldgen;

        // Keep the origin spawn in the open (see TreeSpawnClearRadius)
        if (rules.SpawnClear
            && Math.Abs(x) <= WorldGenerationConfig.TreeSpawnClearRadius
            && Math.Abs(z) <= WorldGenerationConfig.TreeSpawnClearRadius)
            return false;

        var density = rules.DensityFor(WorldSeed.Profile);
        if (own >= density)
            return false;
        if (own >= density * TerrainErg.Retention(x, z))
            return false;

        // Open regions thin the trees (see TerrainGenerator.ForestOpenness), each species by its
        // own share of it. The mask is read only for the few columns that already pass the raw
        // density, so the common case never pays for the noise.
        if (rules.Openness > 0f
            && own >= density * (1f - rules.Openness * TerrainGenerator.ForestOpenness(x, z)))
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
        if (!SurfaceClearance.Allows(height, 1 + TreeRasterizer.ReachOf(found).Up))
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

    /// <summary>Whether a block id is the leaves of any tree species (false for an id past the
    /// table, which a later, wider content load can produce).</summary>
    public static bool IsLeaf(ushort blockId)
    {
        var table = LeafById;
        return (uint)blockId < (uint)table.Length && table[blockId];
    }

    /// <summary>Wood or leaves of a species with a canopy: the blocks whose removal disturbs the
    /// far-LOD canopy. A leafless species' wood (cactus) is excluded because it has no canopy.
    /// False for an id past the table, as <see cref="IsLeaf"/>.</summary>
    public static bool IsTreeBlock(ushort blockId)
    {
        var table = CanopyBlockById;
        return (uint)blockId < (uint)table.Length && table[blockId];
    }

    /// <summary>Highest canopy cell over the ground column (x, z), or 0 under open sky (<see cref="TreeCanopy.CanopyTop"/>).</summary>
    public static int CanopyTop(int x, int z) => TreeCanopy.CanopyTop(x, z);

    /// <summary>The leaf block wearing the canopy top over (x, z), air where there is none (<see cref="TreeCanopy.CanopyLeafBlock"/>).</summary>
    public static ushort CanopyLeafBlock(int x, int z) => TreeCanopy.CanopyLeafBlock(x, z);

    /// <summary>The grown canopy over (x, z) (<see cref="TreeCanopy.TryGetCanopy(int, int, out int, out ushort)"/>).</summary>
    public static bool TryGetCanopy(int x, int z, out int top, out ushort leaf) =>
        TreeCanopy.TryGetCanopy(x, z, out top, out leaf);

    /// <summary>The grown or proxy canopy over (x, z) (<see cref="TreeCanopy.TryGetCanopy(int, int, bool, out int, out ushort)"/>).</summary>
    public static bool TryGetCanopy(int x, int z, bool proxy, out int top, out ushort leaf) =>
        TreeCanopy.TryGetCanopy(x, z, proxy, out top, out leaf);

    /// <summary>Marks the canopy columns within Chebyshev <paramref name="radius"/> of (x, z) as disturbed (<see cref="TreeCanopy.PoisonCanopy"/>).</summary>
    public static void PoisonCanopy(int x, int z, int radius) => TreeCanopy.PoisonCanopy(x, z, radius);

    /// <summary>Records the crown a reactive growth left over ground column (x, z) for the far LOD; call after <see cref="PoisonCanopy"/> (<see cref="TreeCanopy.SetCanopyAdded"/>).</summary>
    public static bool SetCanopyAdded(int x, int z, int top, ushort leaf) => TreeCanopy.SetCanopyAdded(x, z, top, leaf);

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
