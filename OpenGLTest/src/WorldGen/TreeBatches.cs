using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The grown tree shapes worldgen stamps and the far LOD reads: the SOLO shapes (one tree grown
/// alone over the heightmap, cached per trunk) and the <see cref="TreeBatch"/> of every 64x64 XZ
/// cell, whose trees grow together against the neighbor cells' solo shapes. Both caches are pure
/// functions of their key, read concurrently by the generation workers, the LOD builder and the
/// server's main-thread LOD builds. <see cref="TreeGenerator"/> owns the placement both read and
/// clears these caches in <see cref="TreeGenerator.ResetCaches"/>.
/// </summary>
internal static class TreeBatches
{
    /// <summary>
    /// One grown, rasterized tree: its species, base cell and every cell it writes in rasterizer
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
        /// Wraps a grown tree's rasterized cells, computing the vertical span (seeded with the
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
    // shapes: BuildBatch reads them as the neighbor cells' obstacles, on the generation workers
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
        var species = TreeGenerator.SpeciesByBiome[(int)TerrainGenerator.BiomeAt(key.X, key.Z)]
                      ?? throw new InvalidOperationException($"No tree species grows at ({key.X}, {key.Z}).");
        var baseY = TreeGenerator.HeightAt(key.X, key.Z) + 1;
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
    // ChunkTerrainFill.FillWater floods (the same Bed < level rule), so no limb or leaf is planned into a
    // cell the stamp would then find full of water -- a drowned parent would leave the rest of
    // the limb floating above the surface. A static delegate field, built once, so a miss
    // allocates no delegate. Pure: reads only the deterministic column data.
    private static readonly Func<int, int, int> GrowthGround = static (x, z) =>
    {
        var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        var li = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
        var height = column.Heights[li];
        var level = column.WaterLevel[li];
        return WorldGenerationConfig.WaterEnabled && column.Bed[li] < level ? Math.Max(height, level) : height;
    };

    /// <summary>Lifetime shape-cache misses, for the profiler's console line.</summary>
    internal static long ShapeCacheMisses => ShapeCache.Misses;

    /// <summary>The grown tree rooted at (x, z); only meaningful where <see cref="TreeGenerator.TryGetTree"/> is true.</summary>
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

    /// <summary>The batch of every tree rooted in the cell (cellX, cellZ) (see <see cref="TreeGenerator.CellOf"/>).</summary>
    internal static TreeBatch BatchOf(int cellX, int cellZ) => BatchCache.GetOrAdd((cellX, cellZ), BuildBatchFor);

    /// <summary>Lifetime batch-cache misses, for the profiler's console line.</summary>
    internal static long BatchCacheMisses => BatchCache.Misses;

    /// <summary>Lifetime batch-cache generation rotations, for the profiler's console line.</summary>
    internal static long BatchCacheRotations => BatchCache.Rotations;

    /// <summary>Batch builds started while another build of the same cell was still running.</summary>
    internal static long BatchDuplicateBuilds => Volatile.Read(ref _batchDuplicates);

    /// <summary>
    /// The batch-grown tree rooted at (x, z): the shape the chunks stamp. Only meaningful where
    /// <see cref="TreeGenerator.TryGetTree"/> is true; for tests and probes.
    /// </summary>
    internal static TreeShape BatchShapeOf(int x, int z)
    {
        foreach (var tree in BatchOf(TreeGenerator.CellOf(x), TreeGenerator.CellOf(z)).Trees)
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
                if (TreeGenerator.TryGetTree(tx, tz, out var species, out var baseY))
                    trunks.Add(new BatchTrunk(TreeGenerator.PlacementHash(tx, tz), tx, tz, species, baseY));

            if (trunks.Count == 0)
                return new TreeBatch { Trees = [], MinY = int.MaxValue, MaxY = int.MinValue };
            trunks.Sort(TrunkOrder);

            // The solo build's water-raised ground, so a batch tree sees the same terrain
            var space = new VirtualTreeSpace(GrowthGround);

            // Neighbor obstacles: the SOLO shapes of the neighbor cells' trunks, never their
            // batches (that would recurse and make the result depend on which cell was asked
            // first). The ring is 2R wide because both trees reach R from their trunks, and the
            // TreeGenerator's static constructor guarantees it stays inside the 8 adjacent cells. A pair farther
            // apart than the sum of their reaches can never share a cell (both stay inside their
            // reach boxes), so it is skipped. Occupation is a set, so its order is irrelevant, but
            // the scan order is fixed anyway.
            var r = TreeGenerator.ReachHorizontal;
            for (var tx = x0 - 2 * r; tx < x0 + size + 2 * r; tx++)
            for (var tz = z0 - 2 * r; tz < z0 + size + 2 * r; tz++)
            {
                if (tx >= x0 && tx < x0 + size && tz >= z0 && tz < z0 + size) continue;
                if (!TreeGenerator.TryGetTree(tx, tz, out var nbSpecies, out _)) continue;

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

    /// <summary>Drops the cached solo shapes and cell batches; called by <see cref="TreeGenerator.ResetCaches"/> only.</summary>
    internal static void ResetCaches()
    {
        ShapeCache.Clear();
        BatchCache.Clear();
    }
}
