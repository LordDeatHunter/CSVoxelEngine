using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// The worldgen front-end over the tree growth engine (<see cref="TreeGrower"/>): decides which
/// columns root a tree and of which <see cref="TreeSpecies"/> (each species' <c>worldgen</c>
/// rules), grows every tree to maturity over the terrain heightmap and stamps the rasterised
/// cells into chunks. The trees of one 64x64 XZ cell grow together (<see cref="TreeBatch"/>),
/// each avoiding the cells of the batch's earlier trees and the SOLO shapes (one tree grown
/// alone, cached per trunk) of the neighbour cells' trees. Like <see cref="WormCarver"/>'s
/// cells, a batch is a pure function of its cell key: any chunk stamps the share of every
/// nearby batch that crosses it, so neighbours agree without shared state. Growth only uses cells above the terrain
/// heightmap, wood only replaces air or leaves and leaves only settle into air, so trees never
/// bury terrain and caves (carved before this runs) never lose rock to them.
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
    private static readonly TreeSpecies?[] SpeciesByBiome;

    private static readonly bool[] LeafById = new bool[256];

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
    private static readonly int ProxyReach;

    // Explicit so the tables are built the first time anything touches this class (after
    // ContentRegistry.Load), not at whatever point beforefieldinit would allow
    static TreeGenerator()
    {
        Species = TreeSpeciesRegistry.All.OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();

        var biomes = Enum.GetValues<Biome>();
        SpeciesByBiome = new TreeSpecies?[biomes.Max(b => (int)b) + 1];
        foreach (var biome in biomes)
            SpeciesByBiome[(int)biome] = Species.FirstOrDefault(s => s.Worldgen?.GrowsIn(biome) == true);

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
                "a batch's neighbour obstacles must come from the 8 adjacent cells only.");
    }

    /// <summary>
    /// One grown, rasterised tree: its species, base cell and every cell it writes in rasterizer
    /// order (wood first: the first <see cref="WoodCount"/> cells, nodes then shells, then the leaves), plus the
    /// vertical span of those cells. Immutable after build and shared across threads.
    /// </summary>
    internal sealed class TreeShape
    {
        public required TreeSpecies Species { get; init; }
        public required int X { get; init; }
        public required int BaseY { get; init; }
        public required int Z { get; init; }
        public required TreeCell[] Cells { get; init; }
        public required int WoodCount { get; init; }
        public required int MinY { get; init; }
        public required int MaxY { get; init; }

        /// <summary>
        /// Wraps a grown tree's rasterised cells, computing the vertical span (seeded with the
        /// base cell, so an empty raster still spans its base).
        /// </summary>
        internal static TreeShape Build(TreeSpecies species, int x, int baseY, int z, TreeCell[] cells, int woodCount)
        {
            var minY = baseY;
            var maxY = baseY;
            foreach (var cell in cells)
            {
                if (cell.Y < minY) minY = cell.Y;
                if (cell.Y > maxY) maxY = cell.Y;
            }

            return new TreeShape
            {
                Species = species,
                X = x,
                BaseY = baseY,
                Z = z,
                Cells = cells,
                WoodCount = woodCount,
                MinY = minY,
                MaxY = maxY,
            };
        }
    }

    // A tree is a pure function of its trunk's (x, z), but every chunk in reach (and the far-LOD
    // canopy sweep) needs it, and growing one costs far more than reading it. These are the SOLO
    // shapes: BuildBatch reads them as the neighbour cells' obstacles, on the generation workers
    // and (through the first LOD ring's batch canopy) the LOD builder thread; the safety
    // argument lives in GenerationalCache, the sizing knobs in WorldGenerationConfig.
    private static readonly GenerationalCache<(int X, int Z), TreeShape> ShapeCache =
        new(WorldGenerationConfig.TreeShapeCacheCapacity, WorldGenerationConfig.TreeShapeCacheGenerations);

    // Static delegate, not a lambda at the call site. GetOrAdd's factory takes the key alone, and
    // the caller has the species and baseY in hand by the time it reaches the cache -- but
    // capturing them would allocate a closure on EVERY call, hits included, on the four chunk-gen
    // threads and the LOD builder. Recomputing both from the key costs two cache-backed lookups on
    // a MISS only (BiomeAt and HeightAt are pure, the same reads TryGetTree made), the same reason
    // Chunk.ComputeColumn and WormCarver.BuildCellFor are static fields.
    private static readonly Func<(int X, int Z), TreeShape> BuildShapeFor = static key =>
    {
        // Timed for WorldgenStats' `grow` figures: this is the miss path, shared by the
        // generation workers and the far-LOD canopy sweep on the LOD builder thread
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var species = SpeciesByBiome[(int)TerrainGenerator.BiomeAt(key.X, key.Z)]
                      ?? throw new InvalidOperationException($"No tree species grows at ({key.X}, {key.Z}).");
        var baseY = HeightAt(key.X, key.Z) + 1;
        var space = new VirtualTreeSpace(GrowthGround);
        var graph = TreeGrower.GrowToMaturity(species, key.X, baseY, key.Z, space);
        var cells = TreeRasterizer.Rasterize(graph, species, space, out var woodCount).ToArray();
        var shape = TreeShape.Build(species, key.X, baseY, key.Z, cells, woodCount);
        _growsOnThread++;
        WorldgenStats.AddGrow(System.Diagnostics.Stopwatch.GetTimestamp() - start);
        return shape;
    };

    // Trees grown on this thread, solo or in a batch. Per-thread on purpose: a test proves a
    // synchronous call grew nothing even while other test classes generate chunks in parallel,
    // which the process-wide cache Misses counters cannot.
    [ThreadStatic] private static long _growsOnThread;

    /// <summary>Trees grown (solo or in a batch) on the calling thread so far; a test seam.</summary>
    internal static long GrowsOnThisThread => _growsOnThread;

    // The ground a tree grows over: the terrain heightmap, raised to the water level in a column
    // Chunk.FillWater floods (the same Bed < level rule), so no limb or leaf is planned into a
    // cell the stamp would then find full of water -- a drowned parent would leave the rest of
    // the limb floating above the surface. A static delegate field, built once, so a miss
    // allocates no delegate. Pure: reads only the deterministic column data.
    private static readonly Func<int, int, int> GrowthGround = static (x, z) =>
    {
        var column = Chunk.GetColumnData(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        var li = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
        var height = column.Heights[li];
        var level = column.WaterLevel[li];
        return WorldGenerationConfig.WaterEnabled && column.Bed[li] < level ? Math.Max(height, level) : height;
    };

    /// <summary>Lifetime shape-cache misses, for the profiler's console line.</summary>
    internal static long ShapeCacheMisses => ShapeCache.Misses;

    /// <summary>The grown tree rooted at (x, z); only meaningful where <see cref="TryGetTree"/> is true.</summary>
    internal static TreeShape ShapeOf(int x, int z) => ShapeCache.GetOrAdd((x, z), BuildShapeFor);

    // Every tree of a 64x64 cell grown together (TreeBatch). Shared by Stamp on the generation
    // workers and the first LOD ring's canopy on the LOD builder; sizing knobs in
    // WorldGenerationConfig, the safety argument in GenerationalCache.
    private static readonly GenerationalCache<(int X, int Z), TreeBatch> BatchCache =
        new(WorldGenerationConfig.TreeBatchCacheCapacity, WorldGenerationConfig.TreeBatchCacheGenerations);

    // Static delegate, not a lambda at the call site: a closure would allocate on every call,
    // hits included, on the generation workers and the LOD builder (see BuildShapeFor).
    private static readonly Func<(int X, int Z), TreeBatch> BuildBatchFor = static key => BuildBatch(key.X, key.Z);

    // Duplicate-build diagnostics: GenerationalCache has no single-flight, so several workers
    // missing one new cell together each build it (deterministic, interchangeable results). The
    // in-flight set only counts how often that happens; lock-free, diagnostics only.
    private static readonly ConcurrentDictionary<(int X, int Z), byte> BatchInFlight = new();
    private static long _batchDuplicates;

    /// <summary>The batch of every tree rooted in the cell (cellX, cellZ) (see <see cref="CellOf"/>).</summary>
    internal static TreeBatch BatchOf(int cellX, int cellZ) => BatchCache.GetOrAdd((cellX, cellZ), BuildBatchFor);

    /// <summary>Lifetime batch-cache misses, for the profiler's console line.</summary>
    internal static long BatchCacheMisses => BatchCache.Misses;

    /// <summary>Lifetime batch-cache generation rotations, for the profiler's console line.</summary>
    internal static long BatchCacheRotations => BatchCache.Rotations;

    /// <summary>Batch builds started while another build of the same cell was still running.</summary>
    internal static long BatchDuplicateBuilds => Volatile.Read(ref _batchDuplicates);

    /// <summary>
    /// The batch-grown tree rooted at (x, z): the shape the chunks stamp. Only meaningful where
    /// <see cref="TryGetTree"/> is true; for tests and probes.
    /// </summary>
    internal static TreeShape BatchShapeOf(int x, int z)
    {
        foreach (var tree in BatchOf(CellOf(x), CellOf(z)).Trees)
            if (tree.X == x && tree.Z == z)
                return tree;
        throw new InvalidOperationException($"No batch tree is rooted at ({x}, {z}).");
    }

    private readonly record struct BatchTrunk(float Hash, int X, int Z, TreeSpecies Species, int BaseY);

    // (hash, x, z) is the design's placement-hash order: a total order, never scan or dictionary
    // order. TreeGrower.SeedFor makes each tree's RNG independent of that order, so only the
    // obstacles differ between a tree's solo and batch growth.
    private static readonly Comparison<BatchTrunk> TrunkOrder = static (a, b) =>
    {
        var c = a.Hash.CompareTo(b.Hash);
        if (c != 0) return c;
        c = a.X.CompareTo(b.X);
        return c != 0 ? c : a.Z.CompareTo(b.Z);
    };

    private static TreeBatch BuildBatch(int cellX, int cellZ)
    {
        var key = (cellX, cellZ);
        var owner = BatchInFlight.TryAdd(key, 0);
        if (!owner) Interlocked.Increment(ref _batchDuplicates);
        try
        {
            var size = WorldGenerationConfig.TreeCellBlocks;
            var x0 = cellX << WorldGenerationConfig.TreeCellShift;
            var z0 = cellZ << WorldGenerationConfig.TreeCellShift;

            var trunks = new List<BatchTrunk>();
            for (var tx = x0; tx < x0 + size; tx++)
            for (var tz = z0; tz < z0 + size; tz++)
                if (TryGetTree(tx, tz, out var species, out var baseY))
                    trunks.Add(new BatchTrunk(PlacementHash(tx, tz), tx, tz, species, baseY));

            if (trunks.Count == 0)
                return new TreeBatch { Trees = [], MinY = int.MaxValue, MaxY = int.MinValue };
            trunks.Sort(TrunkOrder);

            // The solo build's water-raised ground, so a batch tree sees the same terrain
            var space = new VirtualTreeSpace(GrowthGround);

            // Neighbour obstacles: the SOLO shapes of the neighbour cells' trunks, never their
            // batches (that would recurse and make the result depend on which cell was asked
            // first). The ring is 2R wide because both trees reach R from their trunks, and the
            // static constructor guarantees it stays inside the 8 adjacent cells. A pair farther
            // apart than the sum of their reaches can never share a cell (both stay inside their
            // reach boxes), so it is skipped. Occupation is a set, so its order is irrelevant, but
            // the scan order is fixed anyway.
            var r = ReachHorizontal;
            for (var tx = x0 - 2 * r; tx < x0 + size + 2 * r; tx++)
            for (var tz = z0 - 2 * r; tz < z0 + size + 2 * r; tz++)
            {
                if (tx >= x0 && tx < x0 + size && tz >= z0 && tz < z0 + size) continue;
                if (!TryGetTree(tx, tz, out var nbSpecies, out _)) continue;

                var nbReach = TreeRasterizer.ReachOf(nbSpecies).Horizontal;
                var reaches = false;
                foreach (var own in trunks)
                {
                    var distance = Math.Max(Math.Abs(own.X - tx), Math.Abs(own.Z - tz));
                    if (distance > nbReach + TreeRasterizer.ReachOf(own.Species).Horizontal) continue;
                    reaches = true;
                    break;
                }

                if (reaches)
                    space.OccupyAll(ShapeOf(tx, tz).Cells);
            }

            var shapes = new TreeShape[trunks.Count];
            var minY = int.MaxValue;
            var maxY = int.MinValue;
            for (var i = 0; i < trunks.Count; i++)
            {
                var t = trunks[i];
                // One AddGrow per tree, so `grows` and `grow ms/tree` keep their meaning
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                var graph = TreeGrower.GrowToMaturity(t.Species, t.X, t.BaseY, t.Z, space);
                var cells = TreeRasterizer.Rasterize(graph, t.Species, space, out var woodCount).ToArray();
                space.OccupyAll(cells);
                var shape = TreeShape.Build(t.Species, t.X, t.BaseY, t.Z, cells, woodCount);
                _growsOnThread++;
                WorldgenStats.AddGrow(System.Diagnostics.Stopwatch.GetTimestamp() - start);

                shapes[i] = shape;
                minY = Math.Min(minY, shape.MinY);
                maxY = Math.Max(maxY, shape.MaxY);
            }

            return new TreeBatch { Trees = shapes, MinY = minY, MaxY = maxY };
        }
        finally
        {
            if (owner) BatchInFlight.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Stamps every tree whose volume can intersect this chunk into <paramref name="blocks"/>,
    /// writing each cell's state byte into <paramref name="state"/> (allocated on the first
    /// write). Runs on a generation worker thread, after the cave carvers, before the solid
    /// recount. Trees are grown per 64x64 cell (<see cref="TreeBatch"/>): the chunk reads every
    /// cell whose reach-expanded box meets it, visited in cell order (x, then z) and within a
    /// cell in placement-hash order. Every wood cell goes in first (into air or leaves), then the
    /// leaves (into air only), so a crossing canopy never overwrites wood. That fixed order is
    /// what settles the leftover overlap deterministically: a cell's trees and a neighbour cell's
    /// trees (which grew against this cell's SOLO shapes, not its batch) can still meet at the
    /// border, and a tree's unchecked base cell can sit in an earlier tree's leaves -- wood is
    /// first-come, leaves never go over anything.
    /// </summary>
    public static void Stamp(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        var r = ReachHorizontal;
        var x1 = worldX0 + Chunk.Size;
        var y1 = worldY0 + Chunk.Size;
        var z1 = worldZ0 + Chunk.Size;
        var cx0 = CellOf(worldX0 - r);
        var cx1 = CellOf(x1 - 1 + r);
        var cz0 = CellOf(worldZ0 - r);
        var cz1 = CellOf(z1 - 1 + r);

        List<TreeShape>? near = null;
        for (var cx = cx0; cx <= cx1; cx++)
        for (var cz = cz0; cz <= cz1; cz++)
        {
            var batch = BatchOf(cx, cz);
            if (batch.MaxY < worldY0 || batch.MinY >= y1)
                continue;

            foreach (var shape in batch.Trees)
            {
                if (shape.X + r < worldX0 || shape.X - r >= x1 || shape.Z + r < worldZ0 || shape.Z - r >= z1)
                    continue;
                if (shape.MinY >= y1 || shape.MaxY < worldY0)
                    continue;
                near ??= new List<TreeShape>(4);
                near.Add(shape);
            }
        }

        if (near == null) return;

        foreach (var shape in near)
        {
            var cells = shape.Cells;
            for (var i = 0; i < shape.WoodCount; i++)
            {
                if (!TryLocal(cells[i], worldX0, worldY0, worldZ0, out var index)) continue;
                var current = blocks[index];
                if (current != BlockType.Air && !LeafById[current]) continue;
                blocks[index] = cells[i].Block;
                state ??= new byte[Chunk.BlockCount];
                state[index] = cells[i].State;
            }

            StampRoots(blocks, ref state, worldX0, worldY0, worldZ0, shape);
        }

        foreach (var shape in near)
        {
            var cells = shape.Cells;
            for (var i = shape.WoodCount; i < cells.Length; i++)
            {
                if (!TryLocal(cells[i], worldX0, worldY0, worldZ0, out var index)) continue;
                if (blocks[index] != BlockType.Air) continue;
                blocks[index] = cells[i].Block;
                state ??= new byte[Chunk.BlockCount];
                state[index] = cells[i].State;
            }
        }
    }

    private static bool TryLocal(in TreeCell cell, int worldX0, int worldY0, int worldZ0, out int index)
    {
        var lx = cell.X - worldX0;
        var ly = cell.Y - worldY0;
        var lz = cell.Z - worldZ0;
        if ((uint)lx >= Chunk.Size || (uint)ly >= Chunk.Size || (uint)lz >= Chunk.Size)
        {
            index = 0;
            return false;
        }

        index = Chunk.Idx(lx, ly, lz);
        return true;
    }

    // A cave mouth can open under a trunk after the tree was grown over the heightmap; grow the
    // trunk down through post-carve air so it reaches solid ground. Only the chunk holding the
    // base cell walks, and it stops at its own floor, so roots never cross a chunk floor: a chunk
    // reads only its own blocks, every chunk stays a pure function of position, and no root stub
    // can be left hanging in a cave below.
    private static void StampRoots(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0,
        TreeShape shape)
    {
        var lx = shape.X - worldX0;
        var lz = shape.Z - worldZ0;
        var baseLy = shape.BaseY - worldY0;
        if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size || (uint)baseLy >= Chunk.Size)
            return;
        if (shape.WoodCount == 0) return;

        var depth = TreeRasterizer.ReachOf(shape.Species).Down;
        var rootState = WoodState.Encode(WoodState.Thickness(shape.Cells[0].State), TreeDirections.Up, false);
        for (var d = 1; d <= depth; d++)
        {
            var ly = baseLy - d;
            if (ly < 0) break;

            var index = Chunk.Idx(lx, ly, lz);
            if (blocks[index] != BlockType.Air) break;
            blocks[index] = shape.Species.WoodId;
            state ??= new byte[Chunk.BlockCount];
            state[index] = rootState;
        }
    }

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

        var found = SpeciesByBiome[(int)TerrainGenerator.BiomeAt(x, z)];
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

        var column = Chunk.GetColumnData(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
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

    private static int HeightAt(int x, int z) =>
        Chunk.GetColumnData(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            .Heights[((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];

    /// <summary>Whether a block id is the leaves of any tree species.</summary>
    public static bool IsLeaf(byte blockId) => LeafById[blockId];

    /// <summary>Wood or leaves of a species with a canopy: the blocks whose removal disturbs the
    /// far-LOD canopy. A leafless species' wood (cactus) is excluded because it has no canopy.</summary>
    public static bool IsTreeBlock(byte blockId) => CanopyBlockById[blockId];

    // Canopy tops per chunk column (index (x & mask) << shift | (z & mask)): the highest leaf
    // cell over each ground column, 0 where the sky is clear, plus the leaf block that owns
    // that top cell. The far LOD reads both instead of block data so distant forests show in the
    // right foliage. Entries are a pure function of the chunk column (BuildCanopyTops reads only
    // the cell batches, themselves a pure function of their cell), so eviction is always safe --
    // edits never enter here, they live in CanopyDisturbed, which the CALLERS consult.
    internal sealed class CanopyColumns(int[] tops, byte[] leafBlocks)
    {
        public readonly int[] Tops = tops;
        public readonly byte[] LeafBlocks = leafBlocks;
    }

    // Its one engine caller is the far LOD: LodRegion.BuildMeshData asks TryGetCanopy once per
    // FINE column of a region, so a ring sweep walks far more distinct chunk columns than the cap
    // holds. Sizing knobs live in WorldGenerationConfig; the safety argument lives in
    // GenerationalCache.
    private static readonly GenerationalCache<(int X, int Z), CanopyColumns> CanopyCache =
        new(WorldGenerationConfig.TreeCanopyCacheCapacity, WorldGenerationConfig.TreeCanopyCacheGenerations);

    // Static delegate, not a lambda at the call site: a closure would allocate on every call, hits
    // included, on the LOD builder thread (see BuildShapeFor).
    private static readonly Func<(int X, int Z), CanopyColumns> BuildCanopyFor =
        static key => BuildCanopyTops(key.X, key.Z);

    // The far rings' canopy (LodLevelSpec.ProxyCanopy): the species' lod crown proxies per chunk
    // column, laid out like CanopyCache's entries. Cheap to rebuild and a pure function of the
    // chunk column, read by the same per-fine-column sweep, so it is sized like the real one.
    private static readonly GenerationalCache<(int X, int Z), CanopyColumns> ProxyCanopyCache =
        new(WorldGenerationConfig.TreeProxyCanopyCacheCapacity, WorldGenerationConfig.TreeProxyCanopyCacheGenerations);

    // Static delegate, not a lambda at the call site (see BuildShapeFor).
    private static readonly Func<(int X, int Z), CanopyColumns> BuildProxyCanopyFor =
        static key => BuildProxyCanopyTops(key.X, key.Z);

    // Fine columns whose canopy was edited by the player (mined wood/leaves, blasts, fells).
    // The static canopy above knows nothing of edits, so lookups here read 0 (no bump) rather
    // than resurrecting a chopped crown at long range. Entries are gameplay truth, so the cap
    // only bounds memory; clearing it can briefly show a stale bump far away, never a hole.
    // Process-global but per WORLD: WorldSeed.Apply resets it between worlds (ResetCanopyDisturbed),
    // and a save carries it across a restart through LodEditState (lod-edits.bin; the wholesale
    // Clear at CanopyDisturbedLimit makes that lossy in a very long session, M-10).
    private static readonly ConcurrentDictionary<(int X, int Z), byte> CanopyDisturbed = new();

    /// <summary>
    /// Drops the cached tree shapes, cell batches and canopy tops (real and proxy); only <see cref="WorldSeed.Apply"/> (and
    /// tests) call it, since both are a function of the seed.
    /// </summary>
    internal static void ResetCaches()
    {
        ShapeCache.Clear();
        BatchCache.Clear();
        CanopyCache.Clear();
        ProxyCanopyCache.Clear();
    }

    /// <summary>
    /// Forgets every felled-canopy marker, which belongs to the previous world's edits; only
    /// <see cref="WorldSeed.Apply"/> calls it.
    /// </summary>
    internal static void ResetCanopyDisturbed() => CanopyDisturbed.Clear();

    /// <summary>
    /// Highest canopy cell over the ground column (x, z), or 0 under open sky. Disturbed
    /// columns (see <see cref="PoisonCanopy"/>) read 0. Safe to call from any thread.
    /// </summary>
    public static int CanopyTop(int x, int z)
    {
        if (CanopyDisturbed.ContainsKey((x, z)))
            return 0;

        return GetCanopyTops(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];
    }

    public static byte CanopyLeafBlock(int x, int z)
    {
        if (CanopyDisturbed.ContainsKey((x, z)))
            return BlockType.Air;

        TryGetCanopy(x, z, out _, out var leaf);
        return leaf;
    }

    /// <summary>The grown canopy over (x, z); see <see cref="TryGetCanopy(int, int, bool, out int, out byte)"/>.</summary>
    public static bool TryGetCanopy(int x, int z, out int top, out byte leaf) =>
        TryGetCanopy(x, z, proxy: false, out top, out leaf);

    /// <summary>
    /// The canopy top over the ground column (x, z) and the leaf block wearing it; false under
    /// open sky or where the canopy was disturbed (see <see cref="PoisonCanopy"/>). With
    /// <paramref name="proxy"/> it reads the species' <c>lod</c> crown proxies instead of the
    /// grown trees, which never runs tree growth (the far rings, <see cref="LodLevelSpec.ProxyCanopy"/>).
    /// Safe to call from any thread.
    /// </summary>
    public static bool TryGetCanopy(int x, int z, bool proxy, out int top, out byte leaf)
    {
        top = 0;
        leaf = BlockType.Air;
        if (CanopyDisturbed.ContainsKey((x, z)))
            return false;

        var entry = proxy
            ? GetProxyCanopyColumns(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            : GetCanopyColumns(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        var i = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
        top = entry.Tops[i];
        if (top <= 0)
            return false;
        leaf = entry.LeafBlocks[i];
        return true;
    }

    /// <summary>
    /// Marks the canopy columns within Chebyshev <paramref name="radius"/> of (x, z) as disturbed
    /// (see <see cref="CanopyTop"/>). A single mined or blasted wood or leaf cell passes
    /// <see cref="SingleEditPoisonRadius"/>; a felled tree passes <see cref="FellPoisonRadius"/>
    /// once at the break, which covers its whole canopy.
    /// </summary>
    public static void PoisonCanopy(int x, int z, int radius)
    {
        // One bound check per call, not per cell: ConcurrentDictionary.Count takes every lock, and
        // the limit only bounds memory, so overshooting it by one call's square is harmless.
        if (CanopyDisturbed.Count >= WorldGenerationConfig.CanopyDisturbedLimit)
            CanopyDisturbed.Clear();

        for (var dx = -radius; dx <= radius; dx++)
        for (var dz = -radius; dz <= radius; dz++)
            CanopyDisturbed[(x + dx, z + dz)] = 0;

        Interlocked.Increment(ref _canopyEditVersion);
    }

    private static int _canopyEditVersion;

    /// <summary>Bumped once per <see cref="PoisonCanopy"/> call, so a save can tell whether anything changed.</summary>
    internal static int CanopyEditVersion => Volatile.Read(ref _canopyEditVersion);

    /// <summary>Every felled-canopy marker, for the per-world LOD edit save (<c>LodEditState</c>).</summary>
    internal static (int X, int Z)[] ExportCanopyDisturbed() => CanopyDisturbed.Keys.ToArray();

    /// <summary>
    /// Adds saved felled-canopy markers to the live set (a marker is a plain flag, so the union is
    /// the merge). Call before <c>LodManager.Start</c> so the first region builds see it. Leaves
    /// <see cref="CanopyEditVersion"/> alone: the imported markers are what the file holds.
    /// </summary>
    internal static void ImportCanopyDisturbed(IEnumerable<(int X, int Z)> columns)
    {
        foreach (var column in columns)
            CanopyDisturbed.TryAdd(column, 0);
    }

    internal static int[] GetCanopyTops(int chunkX, int chunkZ) => GetCanopyColumns(chunkX, chunkZ).Tops;

    internal static CanopyColumns GetCanopyColumns(int chunkX, int chunkZ) =>
        CanopyCache.GetOrAdd((chunkX, chunkZ), BuildCanopyFor);

    internal static CanopyColumns GetProxyCanopyColumns(int chunkX, int chunkZ) =>
        ProxyCanopyCache.GetOrAdd((chunkX, chunkZ), BuildProxyCanopyFor);

    // Raises each ground column under a nearby tree's leaf cells to the highest leaf cell above
    // it. Trunks up to ReachHorizontal outside the chunk column still overhang it. The canopy is
    // the BATCH canopy -- the same cell walk Stamp does, over the same TreeBatch shapes -- so it is
    // what the chunks stamp, and the first LOD ring agrees with the full-detail chunks that
    // replace it. The far rings never come here: they read the per-species proxy instead.
    private static CanopyColumns BuildCanopyTops(int chunkX, int chunkZ)
    {
        var r = ReachHorizontal;
        var x0 = chunkX * Chunk.Size;
        var z0 = chunkZ * Chunk.Size;
        var x1 = x0 + Chunk.Size;
        var z1 = z0 + Chunk.Size;
        var tops = new int[Chunk.Size * Chunk.Size];
        var leaves = new byte[Chunk.Size * Chunk.Size];

        var cx0 = CellOf(x0 - r);
        var cx1 = CellOf(x1 - 1 + r);
        var cz0 = CellOf(z0 - r);
        var cz1 = CellOf(z1 - 1 + r);
        for (var cx = cx0; cx <= cx1; cx++)
        for (var cz = cz0; cz <= cz1; cz++)
        foreach (var shape in BatchOf(cx, cz).Trees)
        {
            var species = shape.Species;
            if (!species.HasLeaves)
                continue;
            if (shape.X + r < x0 || shape.X - r >= x1 || shape.Z + r < z0 || shape.Z - r >= z1)
                continue;

            var cells = shape.Cells;
            for (var c = shape.WoodCount; c < cells.Length; c++)
            {
                var lx = cells[c].X - x0;
                var lz = cells[c].Z - z0;
                if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size)
                    continue;

                var i = (lx << Chunk.SizeShift) | lz;
                if (cells[c].Y > tops[i])
                {
                    tops[i] = cells[c].Y;
                    leaves[i] = species.LeavesId;
                }
            }
        }

        return new CanopyColumns(tops, leaves);
    }

    // The proxy canopy of a chunk column: every leafy trunk TryGetTree accepts within ProxyReach
    // (the ONE placement source, shared with the real path) raises the columns under its proxy
    // crown (LodCrownProxy) to base + offset, highest wins. Never calls ShapeOf or BatchOf, so the
    // far sweep runs no growth.
    private static CanopyColumns BuildProxyCanopyTops(int chunkX, int chunkZ)
    {
        var x0 = chunkX * Chunk.Size;
        var z0 = chunkZ * Chunk.Size;
        var tops = new int[Chunk.Size * Chunk.Size];
        var leaves = new byte[Chunk.Size * Chunk.Size];

        for (var tx = x0 - ProxyReach; tx < x0 + Chunk.Size + ProxyReach; tx++)
        for (var tz = z0 - ProxyReach; tz < z0 + Chunk.Size + ProxyReach; tz++)
        {
            if (!TryGetTree(tx, tz, out var species, out var baseY) || !species.HasLeaves)
                continue;

            var height = CrownHeightOf(species, tx, tz);
            var radius = species.CrownRadius;
            for (var dx = -radius; dx <= radius; dx++)
            for (var dz = -radius; dz <= radius; dz++)
            {
                var lx = tx + dx - x0;
                var lz = tz + dz - z0;
                if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size)
                    continue;
                var offset = LodCrownProxy.TopOffset(species.LodProfile, height, radius, dx, dz);
                if (offset < 1)
                    continue;

                var i = (lx << Chunk.SizeShift) | lz;
                if (baseY + offset > tops[i])
                {
                    tops[i] = baseY + offset;
                    leaves[i] = species.LeavesId;
                }
            }
        }

        return new CanopyColumns(tops, leaves);
    }
}
