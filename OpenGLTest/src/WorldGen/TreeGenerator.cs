using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// The worldgen front-end over the tree growth engine (<see cref="TreeGrower"/>): decides which
/// columns root a tree and of which <see cref="TreeSpecies"/> (each species' <c>worldgen</c>
/// rules), grows every tree solo to maturity over the terrain heightmap, caches the rasterised
/// cells per trunk and stamps them into chunks. Like <see cref="WormCarver"/>, a tree is a pure
/// function of its trunk's (x, z): any chunk stamps the share of every nearby trunk that crosses
/// it, so neighbours agree without shared state. Growth only uses cells above the terrain
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

    /// <summary>How far below its base cell any worldgen tree reaches.</summary>
    public static readonly int ReachDown;

    /// <summary><see cref="ReachHorizontal"/> in whole chunks, rounded up.</summary>
    public static readonly int ReachChunkRadius;

    /// <summary>Leaf block the far LOD falls back to: the first leafy species' leaves.</summary>
    public static readonly byte LeavesBlock;

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
            if (LeavesBlock == BlockType.Air) LeavesBlock = species.LeavesId;
        }

        foreach (var species in Species)
        {
            if (species.Worldgen == null) continue;
            MaxDensity = Math.Max(MaxDensity, species.Worldgen.Density);
            var (horizontal, up, down) = TreeRasterizer.ReachOf(species);
            ReachHorizontal = Math.Max(ReachHorizontal, horizontal);
            ReachUp = Math.Max(ReachUp, up);
            ReachDown = Math.Max(ReachDown, down);
        }

        ReachChunkRadius = (ReachHorizontal + Chunk.Size - 1) / Chunk.Size;
    }

    /// <summary>
    /// One grown, rasterised tree: its species, base cell and every cell it writes in rasterizer
    /// order (wood first: the first <see cref="WoodCount"/> cells, then the leaves), plus the
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
    }

    // A tree is a pure function of its trunk's (x, z), but every chunk in reach (and the far-LOD
    // canopy sweep) needs it, and growing one costs far more than reading it. Shared between Stamp
    // on the generation workers and BuildCanopyTops on the LOD builder thread; the safety
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
        var cells = TreeRasterizer.Rasterize(graph, species, space).ToArray();

        var minY = baseY;
        var maxY = baseY;
        foreach (var cell in cells)
        {
            if (cell.Y < minY) minY = cell.Y;
            if (cell.Y > maxY) maxY = cell.Y;
        }

        var shape = new TreeShape
        {
            Species = species,
            X = key.X,
            BaseY = baseY,
            Z = key.Z,
            Cells = cells,
            WoodCount = graph.Count,
            MinY = minY,
            MaxY = maxY,
        };
        WorldgenStats.AddGrow(System.Diagnostics.Stopwatch.GetTimestamp() - start);
        return shape;
    };

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

    /// <summary>
    /// Stamps every tree whose volume can intersect this chunk into <paramref name="blocks"/>,
    /// writing each cell's state byte into <paramref name="state"/> (allocated on the first
    /// write). Runs on a generation worker thread, after the cave carvers, before the solid
    /// recount. Trees are visited in a fixed scan order; every wood cell goes in first (into air
    /// or leaves), then the leaves (into air only), so a crossing canopy never overwrites wood.
    /// </summary>
    public static void Stamp(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        var r = ReachHorizontal;
        List<TreeShape>? near = null;
        for (var tx = worldX0 - r; tx < worldX0 + Chunk.Size + r; tx++)
        for (var tz = worldZ0 - r; tz < worldZ0 + Chunk.Size + r; tz++)
        {
            if (!TryGetTree(tx, tz, out _, out var baseY))
                continue;
            if (baseY - ReachDown > worldY0 + Chunk.Size || baseY + ReachUp < worldY0)
                continue;

            var shape = ShapeOf(tx, tz);
            if (shape.MinY >= worldY0 + Chunk.Size || shape.MaxY < worldY0)
                continue;
            near ??= new List<TreeShape>(4);
            near.Add(shape);
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

    private static float PlacementHash(int x, int z)
    {
        var h = SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ PlacementSalt);
        return (SplitMix64.Mix(h + 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));
    }

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
    // TryGetTree and the grown shapes), so eviction is always safe -- edits never enter here,
    // they live in CanopyDisturbed, which the CALLERS consult.
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

    // Fine columns whose canopy was edited by the player (mined wood/leaves, blasts, fells).
    // The static canopy above knows nothing of edits, so lookups here read 0 (no bump) rather
    // than resurrecting a chopped crown at long range. Entries are gameplay truth, so the cap
    // only bounds memory; clearing it can briefly show a stale bump far away, never a hole.
    // Process-global but per WORLD: WorldSeed.Apply resets it between worlds (ResetCanopyDisturbed),
    // and a save carries it across a restart through LodEditState (lod-edits.bin; the wholesale
    // Clear at CanopyDisturbedLimit makes that lossy in a very long session, M-10).
    private static readonly ConcurrentDictionary<(int X, int Z), byte> CanopyDisturbed = new();

    /// <summary>
    /// Drops the cached tree shapes and canopy tops; only <see cref="WorldSeed.Apply"/> (and
    /// tests) call it, since both are a function of the seed.
    /// </summary>
    internal static void ResetCaches()
    {
        ShapeCache.Clear();
        CanopyCache.Clear();
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

    public static bool TryGetCanopy(int x, int z, out int top, out byte leaf)
    {
        top = 0;
        leaf = BlockType.Air;
        if (CanopyDisturbed.ContainsKey((x, z)))
            return false;

        var entry = GetCanopyColumns(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
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

    // Raises each ground column under a nearby tree's leaf cells to the highest leaf cell above
    // it. Trunks up to ReachHorizontal outside the chunk column still overhang it.
    private static CanopyColumns BuildCanopyTops(int chunkX, int chunkZ)
    {
        var r = ReachHorizontal;
        var x0 = chunkX * Chunk.Size;
        var z0 = chunkZ * Chunk.Size;
        var tops = new int[Chunk.Size * Chunk.Size];
        var leaves = new byte[Chunk.Size * Chunk.Size];

        for (var tx = x0 - r; tx < x0 + Chunk.Size + r; tx++)
        for (var tz = z0 - r; tz < z0 + Chunk.Size + r; tz++)
        {
            if (!TryGetTree(tx, tz, out var species, out _) || !species.HasLeaves)
                continue;

            var shape = ShapeOf(tx, tz);
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
}
