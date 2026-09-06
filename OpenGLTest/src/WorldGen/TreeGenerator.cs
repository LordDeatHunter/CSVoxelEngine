using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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

        var own = PlacementHash(x, z);
        if (own >= WorldGenerationConfig.TreeDensity)
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

        baseY = height + 1;
        shape = BuildShape(x, z, baseY);
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

    /// <summary>Taper level to wood block: 0 is the full-cube log, 1..5 step Branch down to
    /// Twig. Limbs stop before exceeding level 5, so this never clamps two cells in a row.</summary>
    public static byte WoodId(int level) => level switch
    {
        0 => BlockType.Log,
        1 => BlockType.Branch,
        2 => BlockType.Limb,
        3 => BlockType.Sprig,
        4 => BlockType.Switch,
        _ => BlockType.Twig,
    };

    /// <summary>Level of a wood block id, inverse of <see cref="WoodId"/> (leaves: -1).</summary>
    public static int WoodLevel(byte blockId) => blockId switch
    {
        BlockType.Log => 0,
        BlockType.Branch => 1,
        BlockType.Limb => 2,
        BlockType.Sprig => 3,
        BlockType.Switch => 4,
        BlockType.Twig => 5,
        _ => -1,
    };

    /// <summary>A trunk's wood cells plus the leaf-blob centres; world coordinates.</summary>
    public record struct TreeShape(
        int TrunkX,
        int TrunkBaseY,
        int TrunkZ,
        List<(int X, int Y, int Z, byte Id)> Wood,
        List<(int X, int Y, int Z)> LeafBlobs);

    private static TreeShape BuildShape(int x, int z, int baseY)
    {
        var rng = new SplitMix64(SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ ShapeSalt));
        var wood = new List<(int, int, int, byte)>(64);
        var blobs = new List<(int, int, int)>(8);

        var trunkH = rng.NextInt(WorldGenerationConfig.TreeMinHeight, WorldGenerationConfig.TreeMaxHeight + 1);
        var logH = Math.Min(WorldGenerationConfig.TreeLogHeight, trunkH - 1);
        var taperCount = trunkH - logH; // upper-trunk cells stepping levels 1..3 bottom to top
        int TrunkLevel(int yy) => yy - baseY < logH ? 0 : 1 + (yy - baseY - logH) * 3 / taperCount;
        for (var y = 0; y < trunkH; y++)
            wood.Add((x, baseY + y, z, WoodId(TrunkLevel(baseY + y))));

        var topY = baseY + trunkH;
        wood.Add((x, topY, z, WoodId(TrunkLevel(topY - 1) + 1))); // leader spike into the crown
        blobs.Add((x, topY, z));

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
                    blobs.Add((px, py, pz));

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

        return new TreeShape(x, baseY, z, wood, blobs);
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
            if (current == BlockType.Air || current == BlockType.Leaves)
                blocks[i] = id;
        }

        // Roots: a cave mouth can open under a trunk after it was placed; grow the trunk down
        // through post-carve air so it always reaches solid ground. Each chunk walks only its
        // own cells, so every chunk covering the column computes identical cells.
        var rootX = shape.TrunkX - worldX0;
        var rootZ = shape.TrunkZ - worldZ0;
        if ((uint)rootX >= Chunk.Size || (uint)rootZ >= Chunk.Size)
            return;
        for (var d = 1; d <= WorldGenerationConfig.TreeRootDepth; d++)
        {
            var ly = shape.TrunkBaseY - d - worldY0;
            if (ly < 0) break;
            if (ly >= Chunk.Size) continue;

            var i = Chunk.Idx(rootX, ly, rootZ);
            if (blocks[i] != BlockType.Air) break;
            blocks[i] = BlockType.Log;
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
                blocks[i] = BlockType.Leaves;
        }
    }

    /// <summary>
    /// Every world cell a trunk's canopy can place leaves into: the crown blob plus one blob
    /// per limb tip, with the ragged shell edge. Chunk bounds and air checks are the caller's
    /// job; shared by the stamper and the canopy-top cache so they can never disagree.
    /// </summary>
    internal static IEnumerable<(int X, int Y, int Z)> CanopyCells(TreeShape shape)
    {
        var r = WorldGenerationConfig.TreeCrownRadius;
        foreach (var (cx, cy, cz) in shape.LeafBlobs)
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
    // cell over each ground column, 0 where the sky is clear. The far LOD reads this instead
    // of block data so distant forests show. Same growth cap + wholesale clear as the other
    // worldgen caches; entries are deterministic so a reset is harmless.
    private static readonly ConcurrentDictionary<(int X, int Z), int[]> CanopyCache = new();
    private const int CanopyCacheLimit = 4096;

    // Fine columns whose canopy was edited by the player (mined wood/leaves, blasts, fells).
    // The static canopy above knows nothing of edits, so lookups here read 0 (no bump) rather
    // than resurrecting a chopped crown at long range. Entries are gameplay truth, so the cap
    // only bounds memory; clearing it can briefly show a stale bump far away, never a hole.
    private static readonly ConcurrentDictionary<(int X, int Z), byte> CanopyDisturbed = new();
    private const int CanopyDisturbedLimit = 16384;

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

    /// <summary>
    /// Marks canopy columns around a player edit as disturbed (see <see cref="CanopyTop"/>).
    /// Call for every mined/placed/blasted wood or leaf block; felling covers its whole
    /// canopy with one call at the break.
    /// </summary>
    public static void PoisonCanopy(int x, int z)
    {
        var r = WorldGenerationConfig.TreeCanopyPoisonRadius;
        for (var dx = -r; dx <= r; dx++)
        for (var dz = -r; dz <= r; dz++)
        {
            if (CanopyDisturbed.Count >= CanopyDisturbedLimit)
                CanopyDisturbed.Clear();

            CanopyDisturbed[(x + dx, z + dz)] = 0;
        }
    }

    internal static int[] GetCanopyTops(int chunkX, int chunkZ)
    {
        var key = (chunkX, chunkZ);
        if (CanopyCache.TryGetValue(key, out var cached))
            return cached;

        var tops = BuildCanopyTops(chunkX, chunkZ);

        if (CanopyCache.Count >= CanopyCacheLimit)
            CanopyCache.Clear();

        CanopyCache[key] = tops;
        return tops;
    }

    // Raises each ground column under a nearby trunk's canopy cells to the highest leaf cell
    // above it. Trunks up to TreeReachBlocks outside the chunk still overhang it.
    private static int[] BuildCanopyTops(int chunkX, int chunkZ)
    {
        const int R = WorldGenerationConfig.TreeReachBlocks;
        var x0 = chunkX * Chunk.Size;
        var z0 = chunkZ * Chunk.Size;
        var tops = new int[Chunk.Size * Chunk.Size];

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
                    tops[i] = y;
            }
        }

        return tops;
    }
}
