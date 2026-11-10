using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Structures;
using VoxelEngine.WorldGen.Features;

namespace VoxelEngine.WorldGen.Structures;

/// <summary>
/// Where the battle towers stand: one hashed candidate per <see cref="WorldGenerationConfig.TowerCellBlocks"/> cell, a pure
/// function of (cell, seed) like the ruins (<see cref="RuinPlacement"/>). The hash rolls the chance, the center (jittered so the
/// footprint and its site ring stay inside the cell, so two towers never meet), the radius, floor count, stair turn and the
/// dressing templates; the site rules (outside the spawn clear, a dry land center, level dry ground over the disc and a ring
/// round it, the top under the cloud slab) are judged once and cached (<see cref="GenerationalCache{TKey, TValue}"/>, the
/// thirteenth site). Every chunk the footprint crosses finds the same <see cref="TowerSite"/> and stamps its own part through
/// <see cref="TowerGenerator"/>; a chunk no tower reaches pays only the cheap rolls and box rejects. The far LOD shows none.
/// </summary>
public static class TowerPlacement
{
    // The next structure salt after the waystone satellites' 0x5E7E (plans/BIOMEGEN.md)
    private const int TowerSalt = 0x5E7F;

    /// <summary>The farthest a tower writes from its center column: the largest wall plus the entrance apron.</summary>
    internal const int Reach = WorldGenerationConfig.TowerMaxRadius + WorldGenerationConfig.TowerApronDepth;

    /// <summary>The farthest above its ground slab a tower writes: the most floors plus the taller of the forcefield dome on
    /// its parapet and the altar.</summary>
    internal const int MaxHeightAboveGround = WorldGenerationConfig.TowerMaxFloors * WorldGenerationConfig.TowerFloorHeight
                                              + (DomeAboveRoof > WorldGenerationConfig.TowerAltarHeight
                                                  ? DomeAboveRoof
                                                  : WorldGenerationConfig.TowerAltarHeight);

    // The dome's apex over the roof slab (TowerDome.TopY)
    private const int DomeAboveRoof = WorldGenerationConfig.TowerParapetHeight + WorldGenerationConfig.TowerDomeHeight;

    // How far the center keeps from its cell's edges: the reach plus the site ring and a column of slack
    private const int Margin = Reach + WorldGenerationConfig.TowerSiteWaterRing + 1;
    private const int Cell = WorldGenerationConfig.TowerCellBlocks;

    private static readonly GenerationalCache<(int X, int Z), TowerSite?> Cache =
        new(WorldGenerationConfig.TowerSiteCacheCapacity, WorldGenerationConfig.TowerSiteCacheGenerations);

    // A static field, not a lambda at the call site: a closure would allocate on every call (see TreeBatches.BuildShapeFor)
    private static readonly Func<(int X, int Z), TowerSite?> BuildFor = static key => Build(key.X, key.Z);

    /// <summary>Drops every cached site (<see cref="WorldSeed.Apply"/>): they depend on the seed.</summary>
    internal static void ResetCache() => Cache.Clear();

    /// <summary>The tower cell world column coordinate <paramref name="v"/> lies in.</summary>
    internal static int CellOf(int v) => (int)Math.Floor((double)v / Cell);

    /// <summary>The cell's roll: its hash and, when the chance passes, its center and radius. The footprint box
    /// (center ± radius + apron) follows from these alone, so a box reject needs no cache lookup.</summary>
    internal static bool TryRoll(int cellX, int cellZ, out ulong hash, out int centerX, out int centerZ, out int radius)
    {
        hash = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ TowerSalt);
        centerX = 0;
        centerZ = 0;
        radius = 0;
        if (SplitMix64.Unit(hash, 0) >= WorldGenerationConfig.TowerChance) return false;
        var span = (ulong)(Cell - 2 * Margin);
        centerX = cellX * Cell + Margin + (int)(SplitMix64.Mix(hash + 1) % span);
        centerZ = cellZ * Cell + Margin + (int)(SplitMix64.Mix(hash + 2) % span);
        radius = Math.Clamp(
            WorldGenerationConfig.TowerMinRadius
            + (int)(SplitMix64.Unit(hash, 4) * (WorldGenerationConfig.TowerMaxRadius - WorldGenerationConfig.TowerMinRadius + 1)),
            WorldGenerationConfig.TowerMinRadius, WorldGenerationConfig.TowerMaxRadius);
        return true;
    }

    // Whether the rolled footprint box (center ± radius + apron) overlaps the box minX..maxX, minZ..maxZ
    private static bool BoxMeets(int centerX, int centerZ, int radius, int minX, int minZ, int maxX, int maxZ)
    {
        var outer = radius + WorldGenerationConfig.TowerApronDepth;
        return centerX + outer >= minX && centerX - outer <= maxX && centerZ + outer >= minZ && centerZ - outer <= maxZ;
    }

    private static TowerSite? Build(int cellX, int cellZ)
    {
        if (!TryRoll(cellX, cellZ, out var hash, out var cx, out var cz, out var radius)) return null;
        var floorPool = StructureRegistry.Of(StructureKind.TowerFloor);
        if (floorPool.Count == 0 || StructureRegistry.Of(StructureKind.TowerAltar).Count == 0) return null;

        var clear = WorldGenerationConfig.TowerSpawnClearRadius + Reach;
        if ((double)cx * cx + (double)cz * cz < (double)clear * clear) return null;

        var biome = TerrainGenerator.BiomeAt(cx, cz);
        if (biome == Biome.Ocean) return null;
        var ground = RuinPlacement.HeightAt(cx, cz);
        if (TerrainGenerator.IsWaterColumn(cx, cz, ground)) return null;
        if (ground + MaxHeightAboveGround >= WorldGenerationConfig.CloudSlabBottomY) return null;
        if (!GroundIsLevelAndDry(cx, cz, ground)) return null;

        var floors = Math.Clamp(
            WorldGenerationConfig.TowerMinFloors
            + (int)(SplitMix64.Unit(hash, 5) * (WorldGenerationConfig.TowerMaxFloors - WorldGenerationConfig.TowerMinFloors + 1)),
            WorldGenerationConfig.TowerMinFloors, WorldGenerationConfig.TowerMaxFloors);
        var turns = (int)(SplitMix64.Mix(hash + 3) & 3);
        StructureRegistry.TryPick(StructureKind.TowerAltar, null, SplitMix64.Unit(hash, 6), out var altar);
        var altarTurns = (int)(SplitMix64.Mix(hash + 7) & 3);
        var floorTemplates = new StructureTemplate[floors];
        var floorTurns = new int[floors];
        for (var k = 0; k < floors; k++)
        {
            StructureRegistry.TryPick(StructureKind.TowerFloor, null, SplitMix64.Unit(hash, 8 + k), out floorTemplates[k]);
            floorTurns[k] = (int)(SplitMix64.Mix(hash + 16 + (ulong)k) & 3);
        }
        return new TowerSite(cellX, cellZ, cx, cz, ground, radius, floors, turns, TowerPalette.For(biome), altar, altarTurns,
            floorTemplates, floorTurns);
    }

    // A sparse grid over the disc of the reach plus the site ring: every sample within TowerMaxStep of the center's ground
    // and dry, so the floor, the entrance approach and the apron sit near the ground slab and no water stands beside them
    private static bool GroundIsLevelAndDry(int cx, int cz, int ground)
    {
        const int r = Reach + WorldGenerationConfig.TowerSiteWaterRing;
        for (var dx = -r; dx <= r; dx += WorldGenerationConfig.TowerSiteSampleStep)
        for (var dz = -r; dz <= r; dz += WorldGenerationConfig.TowerSiteSampleStep)
        {
            if (dx * dx + dz * dz > r * r + r) continue;
            var h = RuinPlacement.HeightAt(cx + dx, cz + dz);
            if (Math.Abs(h - ground) > WorldGenerationConfig.TowerMaxStep || TerrainGenerator.IsWaterColumn(cx + dx, cz + dz, h))
                return false;
        }
        return true;
    }

    /// <summary>The tower of cell (<paramref name="cellX"/>, <paramref name="cellZ"/>), or false when the cell holds none.
    /// Rolls first, so a cell whose chance fails never reaches the cache.</summary>
    public static bool TryGetSite(int cellX, int cellZ, out TowerSite site)
    {
        site = null!;
        if (!TryRoll(cellX, cellZ, out _, out _, out _, out _)) return false;
        var built = Cache.GetOrAdd((cellX, cellZ), BuildFor);
        if (built == null) return false;
        site = built;
        return true;
    }

    /// <summary>The tower whose footprint box holds column (<paramref name="x"/>, <paramref name="z"/>), or false.</summary>
    public static bool TryFindAt(int x, int z, out TowerSite site)
    {
        site = null!;
        for (var cellX = CellOf(x - Reach); cellX <= CellOf(x + Reach); cellX++)
        for (var cellZ = CellOf(z - Reach); cellZ <= CellOf(z + Reach); cellZ++)
        {
            if (!TryRoll(cellX, cellZ, out _, out var cx, out var cz, out var radius)) continue;
            if (!BoxMeets(cx, cz, radius, x, z, x, z)) continue;
            if (TryGetSite(cellX, cellZ, out site)) return true;
        }
        return false;
    }

    /// <summary>The tower nearest (<paramref name="x"/>, <paramref name="z"/>) among the cells of the first Chebyshev ring of
    /// cells round it (up to <paramref name="maxRings"/>) that holds any. Scans whole cells, so it is for tests and tools.</summary>
    internal static bool TryFindNearest(int x, int z, int maxRings, out TowerSite site)
    {
        site = null!;
        var homeX = CellOf(x);
        var homeZ = CellOf(z);
        for (var ring = 0; ring <= maxRings; ring++)
        {
            var best = double.MaxValue;
            for (var i = -ring; i <= ring; i++)
            for (var j = -ring; j <= ring; j++)
            {
                if (Math.Max(Math.Abs(i), Math.Abs(j)) != ring) continue;
                if (!TryGetSite(homeX + i, homeZ + j, out var candidate)) continue;
                var ddx = (double)candidate.CenterX - x;
                var ddz = (double)candidate.CenterZ - z;
                var d = ddx * ddx + ddz * ddz;
                if (d >= best) continue;
                best = d;
                site = candidate;
            }
            if (site != null) return true;
        }
        return false;
    }

    /// <summary>Whether any tower's footprint box overlaps the box <paramref name="minX"/>..<paramref name="maxX"/>,
    /// <paramref name="minZ"/>..<paramref name="maxZ"/>.</summary>
    internal static bool Meets(int minX, int minZ, int maxX, int maxZ)
    {
        for (var cellX = CellOf(minX - Reach); cellX <= CellOf(maxX + Reach); cellX++)
        for (var cellZ = CellOf(minZ - Reach); cellZ <= CellOf(maxZ + Reach); cellZ++)
        {
            if (!TryRoll(cellX, cellZ, out _, out var cx, out var cz, out var radius)) continue;
            if (!BoxMeets(cx, cz, radius, minX, minZ, maxX, maxZ)) continue;
            if (TryGetSite(cellX, cellZ, out _)) return true;
        }
        return false;
    }

    /// <summary>The highest <see cref="TowerSite.TopY"/> of the towers whose footprint box overlaps the chunk column at
    /// (<paramref name="worldX0"/>, <paramref name="worldZ0"/>), or <see cref="int.MinValue"/>. Allocation-free.</summary>
    internal static int TopReaching(int worldX0, int worldZ0)
    {
        var top = int.MinValue;
        var maxX = worldX0 + Chunk.Size - 1;
        var maxZ = worldZ0 + Chunk.Size - 1;
        for (var cellX = CellOf(worldX0 - Reach); cellX <= CellOf(maxX + Reach); cellX++)
        for (var cellZ = CellOf(worldZ0 - Reach); cellZ <= CellOf(maxZ + Reach); cellZ++)
        {
            if (!TryRoll(cellX, cellZ, out _, out var cx, out var cz, out var radius)) continue;
            if (!BoxMeets(cx, cz, radius, worldX0, worldZ0, maxX, maxZ)) continue;
            if (TryGetSite(cellX, cellZ, out var site)) top = Math.Max(top, site.TopY);
        }
        return top;
    }

    /// <summary>The world-y band <see cref="Stamp"/> can write: up to a tower's height over the tallest ground in reach (the
    /// center's ground, inside the neighborhood scan, is that ground or lower).</summary>
    internal static StampBand Band(int neighborhoodMaxHeight) => new(int.MinValue, neighborhoodMaxHeight + MaxHeightAboveGround);

    /// <summary>Stamps every tower whose footprint reaches the chunk whose minimum corner is (<paramref name="worldX0"/>,
    /// <paramref name="worldY0"/>, <paramref name="worldZ0"/>).</summary>
    public static void Stamp(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        var maxX = worldX0 + Chunk.Size - 1;
        var maxZ = worldZ0 + Chunk.Size - 1;
        for (var cellX = CellOf(worldX0 - Reach); cellX <= CellOf(maxX + Reach); cellX++)
        for (var cellZ = CellOf(worldZ0 - Reach); cellZ <= CellOf(maxZ + Reach); cellZ++)
        {
            if (!TryRoll(cellX, cellZ, out _, out var cx, out var cz, out var radius)) continue;
            if (!BoxMeets(cx, cz, radius, worldX0, worldZ0, maxX, maxZ)) continue;
            if (!TryGetSite(cellX, cellZ, out var site)) continue;
            if (site.TopY < worldY0 || site.BottomY > worldY0 + Chunk.Size - 1) continue;
            TowerGenerator.StampSite(blocks, ref state, worldX0, worldY0, worldZ0, site);
        }
    }
}
