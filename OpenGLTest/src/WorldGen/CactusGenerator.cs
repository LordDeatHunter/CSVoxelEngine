using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// Desert saguaros: a tapered stem that throws a few arms which bend upward into prongs, like a
/// pitchfork. Same growth rules as <see cref="TreeGenerator"/> in every way that matters — a pure
/// function of the stem's (x, z), cached, wood ids taken from the <c>"cactus"</c> wood family —
/// but with no leaves and only three taper sizes (stem, prong, tip). Only desert columns qualify,
/// so a cactus never grows in the forest or on a sea floor.
/// </summary>
public static class CactusGenerator
{
    private const int PlacementSalt = 0x4CAC7A;
    private const int ShapeSalt = 0x4CAC7B;

    // Stems are a pure function of (x, z) but every chunk in reach rebuilds each one, so the
    // shapes are cached exactly like tree trunks: deterministic, read-only after build.
    private static readonly ConcurrentDictionary<(int X, int Z), (int BaseY, CactusShape Shape)> ShapeCache = new();
    private const int ShapeCacheLimit = 16384;

    private static readonly byte WoodFamily;

    static CactusGenerator()
    {
        WoodFamily = ContentRegistry.RequireWoodFamily("cactus");
    }

    /// <summary>Taper level to cactus block: 0 is the stem, 1 the prong, 2 the tip.</summary>
    public static byte WoodId(int level) => BlockType.WoodBlock(WoodFamily, level);

    /// <summary>
    /// Stamps every cactus whose volume can intersect this chunk into <paramref name="blocks"/>.
    /// Runs on a generation worker thread, after the sea and the trees, before the solid recount.
    /// Wood only goes into air, so nothing a carver or the sea left is overwritten.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        const int R = WorldGenerationConfig.CactusReachBlocks;
        var chunkMaxY = worldY0 + Chunk.Size;

        List<CactusShape>? near = null;
        for (var tx = worldX0 - R; tx < worldX0 + Chunk.Size + R; tx++)
        for (var tz = worldZ0 - R; tz < worldZ0 + Chunk.Size + R; tz++)
        {
            if (!TryGetCactus(tx, tz, out var baseY, out var shape))
                continue;
            if (baseY > chunkMaxY || baseY + WorldGenerationConfig.CactusMaxHeightBlocks < worldY0)
                continue;

            near ??= new List<CactusShape>(4);
            near.Add(shape);
        }

        if (near == null) return;

        foreach (var shape in near)
            StampShape(blocks, worldX0, worldY0, worldZ0, shape);
    }

    /// <summary>
    /// Whether (x, z) grows a cactus, and its deterministic shape. Placement (density roll +
    /// lowest-hash spacing + desert ground rules) uses one hash stream; the shape uses another,
    /// so the canopy never shifts which columns are chosen.
    /// </summary>
    public static bool TryGetCactus(int x, int z, out int baseY, out CactusShape shape)
    {
        baseY = 0;
        shape = default;

        var own = PlacementHash(x, z);
        if (own >= WorldGenerationConfig.CactusDensity)
            return false;

        var s = WorldGenerationConfig.CactusSpacingRadius;
        for (var dx = -s; dx <= s; dx++)
        for (var dz = -s; dz <= s; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            if (PlacementHash(x + dx, z + dz) < own)
                return false;
        }

        if (TerrainGenerator.BiomeAt(x, z) != Biome.Desert)
            return false;

        var column = Chunk.GetColumnData(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        var li = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
        var height = column.Heights[li];
        if (TerrainGenerator.IsSubmerged(height))
            return false;
        if (height < WorldGenerationConfig.CactusMinGroundHeight || height > WorldGenerationConfig.CactusMaxGroundHeight)
            return false;

        var slope = WorldGenerationConfig.CactusMaxSlope;
        if (Math.Abs(HeightAt(x + 1, z) - height) > slope
            || Math.Abs(HeightAt(x - 1, z) - height) > slope
            || Math.Abs(HeightAt(x, z + 1) - height) > slope
            || Math.Abs(HeightAt(x, z - 1) - height) > slope)
            return false;

        baseY = height + 1;
        var skey = (x, z);
        if (ShapeCache.TryGetValue(skey, out var cached))
        {
            baseY = cached.BaseY;
            shape = cached.Shape;
            return true;
        }

        if (ShapeCache.Count >= ShapeCacheLimit)
            ShapeCache.Clear();

        shape = BuildShape(x, z, baseY);
        ShapeCache[skey] = (baseY, shape);
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

    // Cardinal arm directions only: every step must be face-adjacent to the previous cell,
    // otherwise the mesher (which only welds shared faces) leaves gaps.
    private static readonly (int Dx, int Dz)[] Directions =
        [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>A stem's wood cells; world coordinates.</summary>
    public record struct CactusShape(
        int TrunkX,
        int TrunkBaseY,
        int TrunkZ,
        List<(int X, int Y, int Z, byte Id)> Wood);

    private static CactusShape BuildShape(int x, int z, int baseY)
    {
        var rng = new SplitMix64(SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ ShapeSalt));
        var wood = new List<(int, int, int, byte)>(32);

        var stemH = rng.NextInt(WorldGenerationConfig.CactusMinHeight, WorldGenerationConfig.CactusMaxHeight + 1);
        for (var y = 0; y < stemH; y++)
            wood.Add((x, baseY + y, z, WoodId(0)));

        // Arms leave the upper stem sideways, then bend straight up into a prong. A one-cell
        // arm keeps the strict taper: the arm is one step thinner than the stem it leaves, and
        // every prong cell above it stays at that level until the tip.
        var arms = rng.NextInt(WorldGenerationConfig.CactusMaxArms + 1);
        for (var a = 0; a < arms; a++)
        {
            var dir = Directions[rng.NextInt(Directions.Length)];
            var startY = baseY + rng.NextInt(Math.Max(1, stemH - 2), stemH);

            var px = x + dir.Dx;
            var pz = z + dir.Dz;
            var py = startY;
            if (py <= HeightAt(px, pz))
                continue; // the arm leaves into a hillside; skip this arm
            wood.Add((px, py, pz, WoodId(1)));

            var rise = rng.NextInt(WorldGenerationConfig.CactusProngMinRise,
                WorldGenerationConfig.CactusProngMaxRise + 1);
            for (var step = 1; step <= rise; step++)
            {
                py++;
                if (py <= HeightAt(px, pz))
                    break;
                wood.Add((px, py, pz, WoodId(step == rise ? 2 : 1)));
            }
        }

        return new CactusShape(x, baseY, z, wood);
    }

    private static void StampShape(byte[] blocks, int worldX0, int worldY0, int worldZ0, CactusShape shape)
    {
        foreach (var (x, y, z, id) in shape.Wood)
        {
            var lx = x - worldX0;
            var ly = y - worldY0;
            var lz = z - worldZ0;
            if ((uint)lx >= Chunk.Size || (uint)ly >= Chunk.Size || (uint)lz >= Chunk.Size)
                continue;

            var i = Chunk.Idx(lx, ly, lz);
            if (blocks[i] == BlockType.Air)
                blocks[i] = id;
        }

        // Roots: a cave mouth can open under a stem after it was placed; grow it down through
        // post-carve air so it always reaches solid ground. Each chunk walks only its own
        // cells, so every chunk covering the column computes identical cells.
        var rootX = shape.TrunkX - worldX0;
        var rootZ = shape.TrunkZ - worldZ0;
        if ((uint)rootX >= Chunk.Size || (uint)rootZ >= Chunk.Size)
            return;
        for (var d = 1; d <= WorldGenerationConfig.CactusRootDepth; d++)
        {
            var ly = shape.TrunkBaseY - d - worldY0;
            if (ly < 0) break;
            if (ly >= Chunk.Size) continue;

            var i = Chunk.Idx(rootX, ly, rootZ);
            if (blocks[i] != BlockType.Air) break;
            blocks[i] = WoodId(0);
        }
    }
}
