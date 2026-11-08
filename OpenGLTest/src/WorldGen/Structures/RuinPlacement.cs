using System;
using System.Collections.Generic;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Structures;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Hell;
using VoxelEngine.WorldGen.Sky;

namespace VoxelEngine.WorldGen.Structures;

/// <summary>
/// Where the structure templates stand in the world: four placements (surface, underground, hell, sky), each a pure function
/// of (cell, seed) like the rest of worldgen. A hashed cell of <see cref="WorldFeatureConfig.RuinCellShift"/> blocks may hold
/// a ruin, whose template, quarter turn and site all come from the hash; every chunk the footprint crosses finds the same
/// ruin and stamps its own part through <see cref="StructureStamper"/>. The far LOD shows none. The site rules are shared
/// with the waystone satellites (<see cref="WaystonePlacement"/>), which stand a short walk from a ruin.
/// </summary>
public static class RuinPlacement
{
    private const int SurfaceSalt = 0x5E7A;
    private const int UndergroundSalt = 0x5E7B;
    private const int HellSalt = 0x5E7C;
    private const int SkySalt = 0x5E7D;

    /// <summary>The farthest a footprint reaches from its center column, with a column of slack.</summary>
    internal const int Reach = StructureRegistry.MaxSize / 2 + 1;

    private static readonly int CellBlocks = 1 << WorldFeatureConfig.RuinCellShift;

    // ---- Surface ----

    /// <summary>The world-y band <see cref="StampSurface"/> can write: up to the template height over the tallest ground in reach.</summary>
    internal static StampBand SurfaceBand(int neighborhoodMaxHeight) =>
        new(int.MinValue, neighborhoodMaxHeight + StructureRegistry.MaxHeight);

    /// <summary>Stamps every surface ruin whose footprint reaches the chunk: dry, level ground of a land biome.</summary>
    public static void StampSurface(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        if (StructureRegistry.Of(StructureKind.Surface).Count == 0) return;
        foreach (var (cellX, cellZ) in CellsReaching(worldX0, worldZ0, Reach))
        {
            if (!TryRollRuin(StructureKind.Surface, cellX, cellZ, 0, out var hash, out var x, out var z)) continue;
            if (!TryFindSurfaceSite(x, z, hash, out var template, out var ground)) continue;
            StampAt(blocks, ref state, worldX0, worldY0, worldZ0, template, TurnsOf(hash), x, ground + 1 - template.GroundLayer, z,
                int.MinValue, int.MaxValue);
        }
    }

    /// <summary>The template and ground height of the surface ruin centered on (x, z), or false when the biome, water or
    /// slope refuses it. A pure function of the column and the hash.</summary>
    internal static bool TryFindSurfaceSite(int x, int z, ulong hash, out StructureTemplate template, out int ground)
    {
        template = null!;
        ground = 0;
        var biome = TerrainGenerator.BiomeAt(x, z);
        return biome != Biome.Ocean
               && StructureRegistry.TryPick(StructureKind.Surface, biome, SplitMix64.Unit(hash, 4), out template)
               && TrySurfaceGround(template, x, z, out ground);
    }

    /// <summary>The ground height under <paramref name="template"/> centered on (x, z) of a land column: false on water or where
    /// the footprint steps more than <see cref="WorldFeatureConfig.RuinSurfaceMaxStep"/>.</summary>
    internal static bool TrySurfaceGround(StructureTemplate template, int x, int z, out int ground)
    {
        ground = HeightAt(x, z);
        if (TerrainGenerator.IsWaterColumn(x, z, ground)) return false;
        var half = template.Half;
        for (var dx = -half; dx <= half; dx++)
        for (var dz = -half; dz <= half; dz++)
        {
            var h = HeightAt(x + dx, z + dz);
            if (Math.Abs(h - ground) > WorldFeatureConfig.RuinSurfaceMaxStep || TerrainGenerator.IsWaterColumn(x + dx, z + dz, h))
                return false;
        }
        return true;
    }

    // ---- Underground ----

    /// <summary>The world-y band <see cref="StampUnderground"/> can write: above the barrier, a cover of rock under the
    /// tallest ground in reach.</summary>
    internal static StampBand UndergroundBand(int neighborhoodMaxHeight) =>
        new(WorldGenerationConfig.BarrierTopY + 1, neighborhoodMaxHeight - WorldFeatureConfig.RuinUndergroundCover);

    /// <summary>Stamps every sealed underground room whose footprint reaches the chunk.</summary>
    public static void StampUnderground(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        if (StructureRegistry.Of(StructureKind.Underground).Count == 0) return;
        foreach (var (cellX, cellZ) in CellsReaching(worldX0, worldZ0, Reach))
        {
            if (!TryRollRuin(StructureKind.Underground, cellX, cellZ, 0, out var hash, out var x, out var z)) continue;
            if (!TryFindUndergroundSite(x, z, hash, out var template, out var originY)) continue;
            StampAt(blocks, ref state, worldX0, worldY0, worldZ0, template, TurnsOf(hash), x, originY, z, int.MinValue, int.MaxValue);
        }
    }

    /// <summary>The template and the world y of its bottom layer for the room centered on (x, z): a hashed depth under the
    /// ground at the center and over the barrier, false when the column is too shallow to hold it.</summary>
    internal static bool TryFindUndergroundSite(int x, int z, ulong hash, out StructureTemplate template, out int originY)
    {
        originY = 0;
        return StructureRegistry.TryPick(StructureKind.Underground, null, SplitMix64.Unit(hash, 4), out template)
               && TryUndergroundOrigin(template, x, z, hash, out originY);
    }

    /// <summary>The world y of <paramref name="template"/>'s bottom layer, sealed under the ground at (x, z) at a depth the
    /// hash picks between the barrier and <see cref="WorldFeatureConfig.RuinUndergroundCover"/> under the ground.</summary>
    internal static bool TryUndergroundOrigin(StructureTemplate template, int x, int z, ulong hash, out int originY)
    {
        originY = 0;
        var lowest = WorldGenerationConfig.BarrierTopY + 1 + template.GroundLayer;
        var highest = HeightAt(x, z) - WorldFeatureConfig.RuinUndergroundCover - (template.Height - template.GroundLayer);
        if (highest < lowest) return false;

        var floor = lowest + (int)(SplitMix64.Unit(hash, 5) * (highest - lowest + 1));
        originY = floor - template.GroundLayer;
        return true;
    }

    // ---- Hell ----

    /// <summary>Stamps every hell ruin whose footprint reaches the chunk, in each hell layer the chunk spans. Its cells stay
    /// inside their own layer.</summary>
    public static void StampHell(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        if (StructureRegistry.Of(StructureKind.Hell).Count == 0) return;
        if (!TryHellLayers(worldY0, out var first, out var last)) return;
        for (var layer = first; layer <= last; layer++)
        foreach (var (cellX, cellZ) in CellsReaching(worldX0, worldZ0, Reach))
        {
            if (!TryRollRuin(StructureKind.Hell, cellX, cellZ, layer, out var hash, out var x, out var z)) continue;
            if (!TryFindHellSite(x, z, layer, hash, out var template, out var floorY)) continue;
            StampInLayer(blocks, ref state, worldX0, worldY0, worldZ0, template, TurnsOf(hash), x, floorY - template.GroundLayer, z,
                layer);
        }
    }

    /// <summary>The hell layers the chunk whose floor is <paramref name="worldY0"/> spans, top first: false above hell.</summary>
    internal static bool TryHellLayers(int worldY0, out int first, out int last)
    {
        var topRow = Math.Min(worldY0 + Chunk.Size - 1, WorldGenerationConfig.HellTopY);
        first = HellGenerator.LayerIndex(topRow);
        last = HellGenerator.LayerIndex(worldY0);
        return topRow >= worldY0;
    }

    /// <summary>Stamps a hell template whose bottom layer is at <paramref name="originY"/>, its cells kept inside
    /// <paramref name="layer"/>.</summary>
    internal static void StampInLayer(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0,
        StructureTemplate template, int turns, int x, int originY, int z, int layer)
    {
        var layerTop = WorldGenerationConfig.HellTopY - layer * WorldGenerationConfig.HellLayerPeriodBlocks;
        StampAt(blocks, ref state, worldX0, worldY0, worldZ0, template, turns, x, originY, z,
            layerTop - WorldGenerationConfig.HellLayerPeriodBlocks + 1, layerTop);
    }

    /// <summary>The template and the floor y (the layer standing on it sits here) of the hell ruin centered on (x, z) in
    /// <paramref name="layer"/>: the first pocket floor, scanning down from a hashed row, with the whole footprint level
    /// and clear above it. False when the column holds none.</summary>
    internal static bool TryFindHellSite(int x, int z, int layer, ulong hash, out StructureTemplate template, out int floorY)
    {
        floorY = 0;
        return StructureRegistry.TryPick(StructureKind.Hell, null, SplitMix64.Unit(hash, 4), out template)
               && TryHellFloor(template, x, z, layer, SplitMix64.Unit(hash, 5), 0, out floorY);
    }

    /// <summary>The floor y under <paramref name="template"/> centered on (x, z) in <paramref name="layer"/>: the first pocket
    /// floor, scanning down from <paramref name="startShare"/> (0..1) of the pocket's height under its top, whose footprint is
    /// level within <paramref name="step"/> and clear above. Step 0 is strictly level: solid under every column, open air
    /// above. A step of n lets a column's ground lie up to n rows lower (solid within n + 1 rows under the floor) or bulge
    /// into the n rows above it (air only from n rows up), for a template whose n layers under its ground layer fill the
    /// dips and whose next layers write over or carve the bulges.</summary>
    internal static bool TryHellFloor(StructureTemplate template, int x, int z, int layer, float startShare, int step,
        out int floorY)
    {
        floorY = 0;
        var top = WorldGenerationConfig.HellTopY - layer * WorldGenerationConfig.HellLayerPeriodBlocks;
        var bottom = top - HellGenerator.PocketHeight + 1;
        var start = top - (int)(startShare * HellGenerator.PocketHeight);
        var samples = new Dictionary<long, float>();
        var clear = template.Height - template.GroundLayer;
        for (var y = start; y > bottom; y--)
        {
            if (!HellGenerator.IsCavernAir(x, y, z, samples) || HellGenerator.IsOpen(x, y - 1, z, samples)) continue;
            if (!FootprintIsLevelAndClear(x, z, y, template.Half, clear, step, samples)) continue;
            floorY = y;
            return true;
        }
        return false;
    }

    private static bool FootprintIsLevelAndClear(int x, int z, int floor, int half, int clear, int step,
        Dictionary<long, float> samples)
    {
        for (var dx = -half; dx <= half; dx++)
        for (var dz = -half; dz <= half; dz++)
        {
            if (!SolidWithin(x + dx, floor, z + dz, step, samples)) return false;
            for (var dy = step; dy < clear; dy++)
                if (!HellGenerator.IsCavernAir(x + dx, floor + dy, z + dz, samples)) return false;
        }
        return true;
    }

    // Whether the column holds ground in the step + 1 rows under the floor
    private static bool SolidWithin(int x, int floor, int z, int step, Dictionary<long, float> samples)
    {
        for (var dy = 1; dy <= step + 1; dy++)
            if (!HellGenerator.IsOpen(x, floor - dy, z, samples)) return true;
        return false;
    }

    // ---- Sky ----

    /// <summary>The world-y band <see cref="StampSky"/> can write: the island band.</summary>
    internal static StampBand SkyBand() => SkyIslandGenerator.WriteBand();

    /// <summary>Stamps every sky ruin whose footprint reaches the chunk: at most one per island, on its top.</summary>
    public static void StampSky(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        if (StructureRegistry.Of(StructureKind.Sky).Count == 0) return;
        Span<SkyIsland> islands = stackalloc SkyIsland[SkyIslands.MaxIslandsPerBox];
        var count = CollectIslandsReaching(worldX0, worldZ0, islands);
        for (var k = 0; k < count; k++)
        {
            if (!TryFindSkySite(islands[k], out var template, out var turns, out var x, out var z, out var top)) continue;
            StructureStamper.Stamp(blocks, ref state, worldX0, worldY0, worldZ0, template, turns,
                StructureStamper.OriginOf(x, template), top + 1 - template.GroundLayer, StructureStamper.OriginOf(z, template),
                int.MinValue, int.MaxValue, SkyRuinKeepsChest(islands[k]));
        }
    }

    /// <summary>The islands whose disc can reach the chunk's columns widened by <see cref="Reach"/>: every island a sky ruin, or
    /// its waystone (which stays inside its island's disc), can write into the chunk from.</summary>
    internal static int CollectIslandsReaching(int worldX0, int worldZ0, Span<SkyIsland> into) =>
        SkyIslands.CollectIslands(worldX0 - Reach, worldZ0 - Reach, worldX0 + Chunk.Size - 1 + Reach,
            worldZ0 + Chunk.Size - 1 + Reach, into);

    // Most sky ruins stand without their chest: a hashed roll per island, one salt away from the ruin's own rolls
    private static bool SkyRuinKeepsChest(in SkyIsland island) =>
        SplitMix64.Unit(SkyHash(island), 5) < WorldFeatureConfig.RuinSkyChestChance;

    /// <summary>The hash an island's ruin rolls from: its center column and the seed.</summary>
    internal static ulong SkyHash(in SkyIsland island) =>
        SplitMix64.Hash((int)MathF.Floor(island.CenterX), (int)MathF.Floor(island.CenterZ), WorldGenerationConfig.Seed ^ SkySalt);

    /// <summary>Whether the island's hash passes the sky ruin chance (the cheap first test of <see cref="TryFindSkySite"/>).</summary>
    internal static bool SkyRuinRolls(ulong hash) => SplitMix64.Unit(hash, 0) < WorldFeatureConfig.RuinSkyChance;

    /// <summary>The ruin an island holds, centered on its center column: false when its roll fails or its top is not level
    /// across the whole footprint (every column an island column, within <see cref="WorldFeatureConfig.RuinSkyMaxStep"/>).</summary>
    internal static bool TryFindSkySite(in SkyIsland island, out StructureTemplate template, out int turns, out int x,
        out int z, out int top)
    {
        template = null!;
        top = 0;
        x = (int)MathF.Floor(island.CenterX);
        z = (int)MathF.Floor(island.CenterZ);
        var hash = SkyHash(island);
        turns = TurnsOf(hash);
        if (!SkyRuinRolls(hash)) return false;
        return StructureRegistry.TryPick(StructureKind.Sky, null, SplitMix64.Unit(hash, 4), out template)
               && TrySkyTop(island, template, x, z, out top);
    }

    /// <summary>The island top under <paramref name="template"/> centered on (x, z): false unless every footprint column is an
    /// island column within <see cref="WorldFeatureConfig.RuinSkyMaxStep"/> of the center's top.</summary>
    internal static bool TrySkyTop(in SkyIsland island, StructureTemplate template, int x, int z, out int top)
    {
        if (!SkyIslandGenerator.TryGetColumn(island, x, z, out _, out top)) return false;
        var half = template.Half;
        for (var dx = -half; dx <= half; dx++)
        for (var dz = -half; dz <= half; dz++)
        {
            if (!SkyIslandGenerator.TryGetColumn(island, x + dx, z + dz, out _, out var columnTop)
                || Math.Abs(columnTop - top) > WorldFeatureConfig.RuinSkyMaxStep)
                return false;
        }
        return true;
    }

    /// <summary>Whether a sky ruin or its waystone writes world cell (x, y, z): the footprint and a written template cell there.
    /// Lets a test (or any reader of the island point query) set aside the cells a structure owns.</summary>
    internal static bool SkyRuinOwns(int x, int y, int z)
    {
        Span<SkyIsland> islands = stackalloc SkyIsland[SkyIslands.MaxIslandsPerBox];
        var count = SkyIslands.CollectIslands(x, z, x, z, islands);
        for (var k = 0; k < count; k++)
        {
            if (TryFindSkySite(islands[k], out var template, out var turns, out var cx, out var cz, out var top)
                && Owns(template, turns, cx, top + 1 - template.GroundLayer, cz, x, y, z))
                return true;
            if (WaystonePlacement.TryFindSky(islands[k], out _, out var waystone)
                && Owns(waystone.Template, waystone.Turns, waystone.X, waystone.OriginY, waystone.Z, x, y, z))
                return true;
        }
        return false;
    }

    private static bool Owns(StructureTemplate template, int turns, int centerX, int originY, int centerZ, int x, int y, int z)
    {
        var lx = x - StructureStamper.OriginOf(centerX, template);
        var lz = z - StructureStamper.OriginOf(centerZ, template);
        var ly = y - originY;
        return (uint)lx < template.Size && (uint)lz < template.Size && (uint)ly < template.Height
               && template.CellAt(turns, lx, ly, lz) >= 0;
    }

    // ---- Shared ----

    /// <summary>The hashed ruin cells whose center column, widened by <paramref name="reach"/>, can touch the chunk.</summary>
    internal static IEnumerable<(int CellX, int CellZ)> CellsReaching(int worldX0, int worldZ0, int reach)
    {
        var shift = WorldFeatureConfig.RuinCellShift;
        var minX = (worldX0 - reach) >> shift;
        var maxX = (worldX0 + Chunk.Size - 1 + reach) >> shift;
        var minZ = (worldZ0 - reach) >> shift;
        var maxZ = (worldZ0 + Chunk.Size - 1 + reach) >> shift;
        for (var cx = minX; cx <= maxX; cx++)
        for (var cz = minZ; cz <= maxZ; cz++)
            yield return (cx, cz);
    }

    /// <summary>The cell's ruin roll for a hashed kind (surface, underground, or hell in <paramref name="layer"/>): its hash,
    /// and when the chance passes, its center column jittered inside the cell. A pure function of (cell, layer, seed).</summary>
    internal static bool TryRollRuin(StructureKind kind, int cellX, int cellZ, int layer, out ulong hash, out int x, out int z)
    {
        var (salt, chance) = kind switch
        {
            StructureKind.Surface => (SurfaceSalt, WorldFeatureConfig.RuinSurfaceChance),
            StructureKind.Underground => (UndergroundSalt, WorldFeatureConfig.RuinUndergroundChance),
            StructureKind.Hell => (HellSalt ^ (layer * 0x2545F491), WorldFeatureConfig.RuinHellChance),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "sky ruins roll per island, not per cell"),
        };
        hash = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ salt);
        x = 0;
        z = 0;
        if (SplitMix64.Unit(hash, 0) >= chance) return false;
        var span = (ulong)CellBlocks;
        x = (cellX << WorldFeatureConfig.RuinCellShift) + (int)(SplitMix64.Mix(hash + 1) % span);
        z = (cellZ << WorldFeatureConfig.RuinCellShift) + (int)(SplitMix64.Mix(hash + 2) % span);
        return true;
    }

    /// <summary>The quarter turns a hash gives a placed template.</summary>
    internal static int TurnsOf(ulong hash) => (int)(SplitMix64.Mix(hash + 3) & 3);

    /// <summary>Stamps a template centered on column (x, z) with its bottom layer at <paramref name="originY"/>.</summary>
    internal static void StampAt(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0,
        StructureTemplate template, int turns, int x, int originY, int z, int minY, int maxY) =>
        StructureStamper.Stamp(blocks, ref state, worldX0, worldY0, worldZ0, template, turns,
            StructureStamper.OriginOf(x, template), originY, StructureStamper.OriginOf(z, template), minY, maxY);

    /// <summary>The generated ground height of column (x, z).</summary>
    internal static int HeightAt(int x, int z) =>
        ColumnCache.GetHeights(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z))
            [VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];
}
