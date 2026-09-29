using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// Dynamic-Trees-style growth: a full-cube log base that tapers through branch boxes into
/// ultra-thin twig limbs tipped with leaf blobs. Like <see cref="WormCarver"/>, trees are a pure
/// function of their trunk's (x, z): any chunk stamps the share of every nearby trunk that
/// crosses it, so neighbours agree without shared state. Wood only ever replaces air or leaves
/// above the terrain heightmap, and leaves only settle into air, so trees never bury terrain and
/// caves (carved before this runs) never lose rock to them.
/// </summary>
public static class TreeGenerator
{
    // Hash salts; spacing/density share one stream so neighbours compare identical values
    private const int PlacementSalt = 0x7E5715;
    private const int ShapeSalt = 0x7EE51F;

    // Trunk shapes are a pure function of (x, z), but every chunk in reach rebuilds each
    // one (plus the canopy-top pass does it again), so shapes dominate worldgen garbage.
    // Cached exactly like worm paths: deterministic, read-only after build (callers only
    // enumerate the lists), safe to share across the generation workers.
    //
    // The old policy was `if (Count >= 16384) Clear()` on the miss path, which threw the whole
    // working set away at the cap AND evaluated the concurrent dictionary's Count -- a barrier that
    // takes EVERY lock in the table -- on every single miss, on the four generation workers and the
    // LOD builder alike (the same cost Chunk.ColumnCache shed in D2). This cache is genuinely
    // shared between Stamp (generation workers) and BuildCanopyTops (the far-LOD canopy sweep,
    // which scans a (2 * TreeReachBlocks + 16)^2 footprint per chunk column), so the two streams
    // used to evict each other wholesale. Residency is unchanged; only the eviction policy moved.
    // Sizing knobs live in WorldGenerationConfig; the safety argument lives in GenerationalCache.
    private static readonly GenerationalCache<(int X, int Z), (int BaseY, TreeShape Shape)> ShapeCache =
        new(WorldGenerationConfig.TreeShapeCacheCapacity, WorldGenerationConfig.TreeShapeCacheGenerations);

    // Static delegate, not a lambda at the call site. GetOrAdd's factory takes the key alone, and
    // TryGetTrunk has baseY and the biome in hand by the time it reaches the cache -- but capturing
    // them would allocate a closure on EVERY call, hits included, on the four chunk-gen threads and
    // the LOD builder. Recomputing both from the key costs two cache-backed lookups on a MISS only
    // (HeightAt and BiomeAt are pure and read the same deterministic column data TryGetTrunk just
    // read), so the trade is per-miss work for zero per-call garbage -- the same reason
    // Chunk.ComputeColumn and WormCarver.BuildCellFor are static fields. The identity
    // `baseY == HeightAt(x, z) + 1` is pinned by a test, not asserted here.
    private static readonly Func<(int X, int Z), (int BaseY, TreeShape Shape)> BuildShapeFor = static key =>
    {
        var baseY = HeightAt(key.X, key.Z) + 1;
        var shape = TerrainGenerator.BiomeAt(key.X, key.Z) == Biome.Taiga
            ? BuildSpruceShape(key.X, key.Z, baseY)
            : BuildShape(key.X, key.Z, baseY);
        return (baseY, shape);
    };

    /// <summary>Block the broadleaf canopy is made of; the wood ids come from <see cref="WoodId"/>.</summary>
    public static readonly byte LeavesBlock;

    /// <summary>Leaf and trunk blocks of the taiga conifer (see <see cref="BuildSpruceShape"/>).</summary>
    public static readonly byte SpruceLeavesBlock;

    /// <summary>Full-cube trunk block of the conifer, the spruce family's only taper level.</summary>
    public static readonly byte SpruceLogBlock;

    // Explicit so the lookup happens the first time anything touches this class (after
    // ContentRegistry.Load), not at whatever point beforefieldinit would allow
    static TreeGenerator()
    {
        LeavesBlock = ContentRegistry.RequireBlock("leaves").Id;
        SpruceLeavesBlock = ContentRegistry.RequireBlock("spruce_leaves").Id;
        SpruceLogBlock = ContentRegistry.RequireBlock("spruce_log").Id;
    }

    /// <summary>
    /// Stamps every tree whose volume can intersect this chunk into <paramref name="blocks"/>.
    /// Runs on a generation worker thread, after the cave carvers, before the solid recount.
    /// Wood goes first so crossing canopies never overwrite it; leaves only fill cells that are
    /// still air afterwards.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        const int R = WorldGenerationConfig.TreeReachBlocks;
        var chunkMaxY = worldY0 + Chunk.Size;

        List<TreeShape>? near = null;
        for (var tx = worldX0 - R; tx < worldX0 + Chunk.Size + R; tx++)
        for (var tz = worldZ0 - R; tz < worldZ0 + Chunk.Size + R; tz++)
        {
            if (!TryGetTrunk(tx, tz, out var baseY, out var shape))
                continue;
            if (baseY > chunkMaxY || baseY + WorldGenerationConfig.TreeMaxHeightBlocks < worldY0)
                continue;

            near ??= new List<TreeShape>(4);
            near.Add(shape);
        }

        if (near == null) return;

        foreach (var shape in near)
            StampWood(blocks, worldX0, worldY0, worldZ0, shape);
        foreach (var shape in near)
            StampLeaves(blocks, worldX0, worldY0, worldZ0, shape);
    }

    /// <summary>
    /// Whether (x, z) grows a tree, and its deterministic shape. Placement (density roll +
    /// lowest-hash spacing + ground rules) uses one hash stream; the shape uses another so the
    /// canopy never shifts which columns are chosen.
    /// </summary>
    public static bool TryGetTrunk(int x, int z, out int baseY, out TreeShape shape)
    {
        baseY = 0;
        shape = default;

        // Keep the origin spawn in the open (see TreeSpawnClearRadius)
        if (Math.Abs(x) <= WorldGenerationConfig.TreeSpawnClearRadius
            && Math.Abs(z) <= WorldGenerationConfig.TreeSpawnClearRadius)
            return false;

        // Forest grows the broadleaf silhouette and taiga the conifer; a desert grows cacti and the
        // frozen plains and ocean floor stay bare (see CactusGenerator).
        var biome = TerrainGenerator.BiomeAt(x, z);
        if (biome is not (Biome.Forest or Biome.Taiga))
            return false;

        var own = PlacementHash(x, z);
        var density = biome == Biome.Taiga
            ? WorldGenerationConfig.SpruceDensity
            : WorldGenerationConfig.TreeDensity;
        if (own >= density)
            return false;

        // Open regions thin the trees (see TerrainGenerator.ForestOpenness), but a taiga keeps more
        // of its spruce so a cold forest still reads as wooded. The mask is read only for the few
        // columns that already pass the raw density, so the common case never pays for the noise.
        var openness = TerrainGenerator.ForestOpenness(x, z);
        if (biome == Biome.Taiga) openness *= WorldGenerationConfig.SpruceOpennessRelief;
        if (own >= density * (1f - openness))
            return false;

        var s = WorldGenerationConfig.TreeSpacingRadius;
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
        // Nothing grows in water; the stamps only write into air anyway, but a trunk in a lake or
        // river would still poke its crown out of the surface. A dry basin below the waterline is
        // land now, so the test is the wet one, not the height one.
        if (TerrainGenerator.IsWaterColumn(x, z, height))
            return false;
        if (column.DirtDepth[li] < WorldGenerationConfig.TreeMinSoil)
            return false;
        if (height < WorldGenerationConfig.TreeMinGroundHeight || height > WorldGenerationConfig.TreeMaxGroundHeight)
            return false;

        var slope = WorldGenerationConfig.TreeMaxSlope;
        if (Math.Abs(HeightAt(x + 1, z) - height) > slope
            || Math.Abs(HeightAt(x - 1, z) - height) > slope
            || Math.Abs(HeightAt(x, z + 1) - height) > slope
            || Math.Abs(HeightAt(x, z - 1) - height) > slope)
            return false;

        var entry = ShapeCache.GetOrAdd((x, z), BuildShapeFor);
        baseY = entry.BaseY;
        shape = entry.Shape;
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

    // Cardinal limb directions only: every step of a limb must be face-adjacent to the
    // previous cell, otherwise the mesher (which only welds shared faces) leaves gaps.
    private static readonly (int Dx, int Dz)[] Directions =
        [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>Taper level to wood block in the tree family: 0 is the full-cube log, 1..5 step
    /// Branch down to Twig. Limbs stop before exceeding the family's top level, so this never
    /// clamps two cells in a row. The mapping comes from each block's <c>wood.taperLevel</c>,
    /// so adding a taper step is a JSON change.</summary>
    public static byte WoodId(int level) => BlockType.WoodBlock(BlockType.TreeWoodFamily, level);

    /// <summary>Level of a wood block id, inverse of <see cref="WoodId"/> (leaves: -1).</summary>
    public static int WoodLevel(byte blockId) => BlockType.ById[blockId]?.WoodTaperLevel ?? -1;

    /// <summary>Whether a block id is canopy leaves of either silhouette (broadleaf or spruce).</summary>
    public static bool IsLeaf(byte blockId) => blockId == LeavesBlock || blockId == SpruceLeavesBlock;

    /// <summary>Wood of either canopy family, or their leaves: the blocks whose removal disturbs
    /// the far-LOD canopy. Cactus wood is excluded because it has no canopy.</summary>
    public static bool IsTreeBlock(byte blockId)
    {
        if (IsLeaf(blockId)) return true;
        if (WoodLevel(blockId) < 0) return false;
        var family = BlockType.WoodFamilyOf(blockId);
        return family == BlockType.TreeWoodFamily || family == BlockType.SpruceWoodFamily;
    }

    /// <summary>A trunk's wood cells, its leaf-blob centres (each with its own radius), and the
    /// leaf block they fill with; world coordinates.</summary>
    public record struct TreeShape(
        int TrunkX,
        int TrunkBaseY,
        int TrunkZ,
        List<(int X, int Y, int Z, byte Id)> Wood,
        List<(int X, int Y, int Z, int R)> LeafBlobs,
        byte LeafBlock);

    private static TreeShape BuildShape(int x, int z, int baseY)
    {
        var rng = new SplitMix64(SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ ShapeSalt));
        var wood = new List<(int, int, int, byte)>(64);
        var r = WorldGenerationConfig.TreeCrownRadius;
        var blobs = new List<(int, int, int, int)>(8);

        var trunkH = rng.NextInt(WorldGenerationConfig.TreeMinHeight, WorldGenerationConfig.TreeMaxHeight + 1);
        var logH = Math.Min(WorldGenerationConfig.TreeLogHeight, trunkH - 1);
        var taperCount = trunkH - logH; // upper-trunk cells stepping levels 1..3 bottom to top
        int TrunkLevel(int yy) => yy - baseY < logH ? 0 : 1 + (yy - baseY - logH) * 3 / taperCount;
        for (var y = 0; y < trunkH; y++)
            wood.Add((x, baseY + y, z, WoodId(TrunkLevel(baseY + y))));

        var topY = baseY + trunkH;
        wood.Add((x, topY, z, WoodId(TrunkLevel(topY - 1) + 1))); // leader spike into the crown
        blobs.Add((x, topY, z, r));

        // Limbs leave the upper trunk already one level thinner than the cell they grow from,
        // and narrow one more level every step along (bends included), so each cell has a
        // strictly wider wood neighbour behind it. Every step moves along exactly one axis,
        // so consecutive cells always share a face and the limb meshes as one connected tube.
        for (var b = 0; b < WorldGenerationConfig.TreeBranchCount; b++)
        {
            var dir = Directions[rng.NextInt(Directions.Length)];
            var startY = baseY + rng.NextInt(trunkH - 4, trunkH - 1);
            var length = rng.NextInt(WorldGenerationConfig.TreeBranchMinLength,
                WorldGenerationConfig.TreeBranchMaxLength + 1);
            // Limbs stop at Twig instead of stacking it: keeps the strict taper (no two equal
            // levels in a row) the mesher's joint caps rely on
            var steps = Math.Min(length, 5 - TrunkLevel(startY));
            var riseCap = startY + steps / 2 + 1;

            var px = x + dir.Dx;
            var pz = z + dir.Dz;
            var py = startY;
            for (var step = 1; step <= steps; step++)
            {
                if (py <= HeightAt(px, pz))
                    break; // ran into a hillside; the limb ends here

                var tip = step == steps;
                wood.Add((px, py, pz, WoodId(TrunkLevel(startY) + step)));
                if (tip)
                    blobs.Add((px, py, pz, r));

                if (step < steps)
                {
                    if (py < riseCap && rng.NextFloat() < WorldGenerationConfig.TreeBranchRiseChance)
                        py++;
                    else
                    {
                        px += dir.Dx;
                        pz += dir.Dz;
                    }
                }
            }
        }

        return new TreeShape(x, baseY, z, wood, blobs, LeavesBlock);
    }

    /// <summary>
    /// A taiga conifer: a full-cube spruce trunk with a cone of needle blobs, widest at the base
    /// and narrowing to a single-cell tip. Blobs are stacked every level with a radius that steps
    /// down with height, so the union reads as a soft conical crown instead of distinct tiers. No
    /// limbs: the trunk is never tapered, so there is nothing to weld.
    /// </summary>
    private static TreeShape BuildSpruceShape(int x, int z, int baseY)
    {
        var rng = new SplitMix64(SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ ShapeSalt));
        var wood = new List<(int, int, int, byte)>(16);
        var blobs = new List<(int, int, int, int)>(16);

        var trunkH = rng.NextInt(WorldGenerationConfig.SpruceMinHeight, WorldGenerationConfig.SpruceMaxHeight + 1);
        for (var y = 0; y < trunkH; y++)
            wood.Add((x, baseY + y, z, SpruceLogBlock));

        // Crown starts a couple of blocks up and tapers to the tip. The radius is rounded so a
        // small tree still gets a one-block tip instead of a bare spike.
        var crownBase = baseY + WorldGenerationConfig.SpruceCrownStart;
        var topY = baseY + trunkH;
        for (var y = crownBase; y <= topY; y++)
        {
            var t = topY <= crownBase ? 1f : (y - crownBase) / (float)(topY - crownBase);
            var radius = (int)MathF.Round(WorldGenerationConfig.SpruceMaxRadius * (1f - t));
            if (radius < 1) radius = 1;
            blobs.Add((x, y, z, radius));
        }

        return new TreeShape(x, baseY, z, wood, blobs, SpruceLeavesBlock);
    }

    private static void StampWood(byte[] blocks, int worldX0, int worldY0, int worldZ0, TreeShape shape)
    {
        foreach (var (x, y, z, id) in shape.Wood)
        {
            var lx = x - worldX0;
            var ly = y - worldY0;
            var lz = z - worldZ0;
            if ((uint)lx >= Chunk.Size || (uint)ly >= Chunk.Size || (uint)lz >= Chunk.Size)
                continue;

            var i = Chunk.Idx(lx, ly, lz);
            var current = blocks[i];
            if (current == BlockType.Air || IsLeaf(current))
                blocks[i] = id;
        }

        // Roots: a cave mouth can open under a trunk after it was placed; grow the trunk down
        // through post-carve air so it always reaches solid ground. Each chunk walks only its
        // own cells, so every chunk covering the column computes identical cells.
        var rootX = shape.TrunkX - worldX0;
        var rootZ = shape.TrunkZ - worldZ0;
        if ((uint)rootX >= Chunk.Size || (uint)rootZ >= Chunk.Size)
            return;
        var trunkId = shape.Wood.Count > 0 ? shape.Wood[0].Id : WoodId(0);
        for (var d = 1; d <= WorldGenerationConfig.TreeRootDepth; d++)
        {
            var ly = shape.TrunkBaseY - d - worldY0;
            if (ly < 0) break;
            if (ly >= Chunk.Size) continue;

            var i = Chunk.Idx(rootX, ly, rootZ);
            if (blocks[i] != BlockType.Air) break;
            blocks[i] = trunkId;
        }
    }

    private static void StampLeaves(byte[] blocks, int worldX0, int worldY0, int worldZ0, TreeShape shape)
    {
        foreach (var (x, y, z) in CanopyCells(shape))
        {
            var lx = x - worldX0;
            var ly = y - worldY0;
            var lz = z - worldZ0;
            if ((uint)lx >= Chunk.Size || (uint)ly >= Chunk.Size || (uint)lz >= Chunk.Size)
                continue;

            var i = Chunk.Idx(lx, ly, lz);
            if (blocks[i] == BlockType.Air)
                blocks[i] = shape.LeafBlock;
        }
    }

    /// <summary>
    /// Every world cell a trunk's canopy can place leaves into: each blob expanded to its own
    /// radius, with the ragged shell edge. Chunk bounds and air checks are the caller's job;
    /// shared by the stamper and the canopy-top cache so they can never disagree.
    /// </summary>
    internal static IEnumerable<(int X, int Y, int Z)> CanopyCells(TreeShape shape)
    {
        foreach (var (cx, cy, cz, r) in shape.LeafBlobs)
        {
            for (var dx = -r; dx <= r; dx++)
            for (var dy = -r; dy <= r; dy++)
            for (var dz = -r; dz <= r; dz++)
            {
                var d2 = dx * dx + dy * dy + dz * dz;
                if (d2 > r * r + 1)
                    continue;

                var x = cx + dx;
                var y = cy + dy;
                var z = cz + dz;
                if (y <= HeightAt(x, z))
                    continue;
                // Thin the outer shell into a ragged edge; parity on world coords keeps it
                // identical whichever chunk stamps the cell
                if (d2 > r * r && ((x + y + z) & 1) == 0)
                    continue;

                yield return (x, y, z);
            }
        }
    }

    // Canopy tops per chunk column (index (x & mask) << shift | (z & mask)): the highest leaf
    // cell over each ground column, 0 where the sky is clear, plus the leaf block that owns
    // that top cell (broadleaf vs spruce). The far LOD reads both instead of block data so
    // distant forests show in the right foliage. Entries are a pure function of the chunk column
    // (BuildCanopyTops reads only TryGetTrunk), so eviction is always safe -- edits never enter
    // here, they live in CanopyDisturbed, which the CALLERS consult. A new canopy species only
    // needs its LeafBlock stored here; the LOD then picks it up via CanopyLeafBlock.
    internal sealed class CanopyColumns(int[] tops, byte[] leafBlocks)
    {
        public readonly int[] Tops = tops;
        public readonly byte[] LeafBlocks = leafBlocks;
    }

    // Was `if (Count >= 4096) Clear()` on the miss path, with the concurrent dictionary's Count --
    // a barrier that takes every lock in the table -- evaluated on every miss. Its one engine
    // caller is the far LOD: LodRegion.BuildMeshData asks TryGetCanopy once per FINE column of a
    // region, so a ring sweep walks far more distinct chunk columns than the cap holds, and used to
    // wipe the whole set each time it ran past it. Residency is unchanged; only the eviction policy
    // moved. Sizing knobs live in WorldGenerationConfig; the safety argument lives in
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
    private const int CanopyDisturbedLimit = 16384;

    /// <summary>
    /// Drops the cached tree shapes and canopy tops; only <see cref="WorldSeed.Apply"/> calls it,
    /// since both are a function of the seed.
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
    /// Marks canopy columns around a player edit as disturbed (see <see cref="CanopyTop"/>).
    /// Call for every mined/placed/blasted wood or leaf block; felling covers its whole
    /// canopy with one call at the break.
    /// </summary>
    public static void PoisonCanopy(int x, int z)
    {
        // One bound check per call, not per cell: ConcurrentDictionary.Count takes every lock, and
        // the limit only bounds memory, so overshooting it by one call's square is harmless.
        if (CanopyDisturbed.Count >= CanopyDisturbedLimit)
            CanopyDisturbed.Clear();

        var r = WorldGenerationConfig.TreeCanopyPoisonRadius;
        for (var dx = -r; dx <= r; dx++)
        for (var dz = -r; dz <= r; dz++)
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

    // Raises each ground column under a nearby trunk's canopy cells to the highest leaf cell
    // above it. Trunks up to TreeReachBlocks outside the chunk still overhang it.
    private static CanopyColumns BuildCanopyTops(int chunkX, int chunkZ)
    {
        const int R = WorldGenerationConfig.TreeReachBlocks;
        var x0 = chunkX * Chunk.Size;
        var z0 = chunkZ * Chunk.Size;
        var tops = new int[Chunk.Size * Chunk.Size];
        var leaves = new byte[Chunk.Size * Chunk.Size];

        for (var tx = x0 - R; tx < x0 + Chunk.Size + R; tx++)
        for (var tz = z0 - R; tz < z0 + Chunk.Size + R; tz++)
        {
            if (!TryGetTrunk(tx, tz, out _, out var shape))
                continue;

            foreach (var (x, y, z) in CanopyCells(shape))
            {
                var lx = x - x0;
                var lz = z - z0;
                if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size)
                    continue;

                var i = (lx << Chunk.SizeShift) | lz;
                if (y > tops[i])
                {
                    tops[i] = y;
                    leaves[i] = shape.LeafBlock;
                }
            }
        }

        return new CanopyColumns(tops, leaves);
    }
}
