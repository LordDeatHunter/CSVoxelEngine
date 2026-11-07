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
/// ruin and stamps its own part through <see cref="StructureStamper"/>. The far LOD shows none.
/// </summary>
public static class RuinPlacement
{
    private const int SurfaceSalt = 0x5E7A;
    private const int UndergroundSalt = 0x5E7B;
    private const int HellSalt = 0x5E7C;
    private const int SkySalt = 0x5E7D;

    // The farthest a footprint reaches from its center column, with a column of slack
    private const int Reach = StructureRegistry.MaxSize / 2 + 1;

    private static readonly int CellBlocks = 1 << WorldFeatureConfig.RuinCellShift;

    // ---- Surface ----

    /// <summary>The world-y band <see cref="StampSurface"/> can write: up to the template height over the tallest ground in reach.</summary>
    internal static StampBand SurfaceBand(int neighborhoodMaxHeight) =>
        new(int.MinValue, neighborhoodMaxHeight + StructureRegistry.MaxHeight);

    /// <summary>Stamps every surface ruin whose footprint reaches the chunk: dry, level ground of a land biome.</summary>
    public static void StampSurface(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        if (StructureRegistry.Of(StructureKind.Surface).Count == 0) return;
        foreach (var (cellX, cellZ) in CellsReaching(worldX0, worldZ0))
        {
            if (!TryRollCell(cellX, cellZ, SurfaceSalt, WorldFeatureConfig.RuinSurfaceChance, out var hash, out var x, out var z))
                continue;
            if (!TryFindSurfaceSite(x, z, hash, out var template, out var ground)) continue;
            var turns = (int)(SplitMix64.Mix(hash + 3) & 3);
            StructureStamper.Stamp(blocks, ref state, worldX0, worldY0, worldZ0, template, turns,
                StructureStamper.OriginOf(x, template), ground + 1 - template.GroundLayer, StructureStamper.OriginOf(z, template),
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
        if (biome == Biome.Ocean) return false;
        if (!StructureRegistry.TryPick(StructureKind.Surface, biome, SplitMix64.Unit(hash, 4), out template)) return false;

        ground = HeightAt(x, z);
        if (TerrainGenerator.IsWaterColumn(x, z, ground)) return false;
        var half = template.Size / 2;
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
        foreach (var (cellX, cellZ) in CellsReaching(worldX0, worldZ0))
        {
            if (!TryRollCell(cellX, cellZ, UndergroundSalt, WorldFeatureConfig.RuinUndergroundChance, out var hash, out var x, out var z))
                continue;
            if (!TryFindUndergroundSite(x, z, hash, out var template, out var originY)) continue;
            var turns = (int)(SplitMix64.Mix(hash + 3) & 3);
            StructureStamper.Stamp(blocks, ref state, worldX0, worldY0, worldZ0, template, turns,
                StructureStamper.OriginOf(x, template), originY, StructureStamper.OriginOf(z, template),
                int.MinValue, int.MaxValue);
        }
    }

    /// <summary>The template and the world y of its bottom layer for the room centered on (x, z): a hashed depth under the
    /// ground at the center and over the barrier, false when the column is too shallow to hold it.</summary>
    internal static bool TryFindUndergroundSite(int x, int z, ulong hash, out StructureTemplate template, out int originY)
    {
        originY = 0;
        if (!StructureRegistry.TryPick(StructureKind.Underground, null, SplitMix64.Unit(hash, 4), out template)) return false;

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
        var topRow = Math.Min(worldY0 + Chunk.Size - 1, WorldGenerationConfig.HellTopY);
        if (topRow < worldY0) return;
        var first = HellGenerator.LayerIndex(topRow);
        var last = HellGenerator.LayerIndex(worldY0);
        for (var layer = first; layer <= last; layer++)
        foreach (var (cellX, cellZ) in CellsReaching(worldX0, worldZ0))
        {
            var chanceHash = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ HellSalt ^ (layer * 0x2545F491));
            if (!TryRollHash(chanceHash, cellX, cellZ, WorldFeatureConfig.RuinHellChance, out var x, out var z)) continue;
            if (!TryFindHellSite(x, z, layer, chanceHash, out var template, out var floorY)) continue;

            var layerTop = WorldGenerationConfig.HellTopY - layer * WorldGenerationConfig.HellLayerPeriodBlocks;
            var turns = (int)(SplitMix64.Mix(chanceHash + 3) & 3);
            StructureStamper.Stamp(blocks, ref state, worldX0, worldY0, worldZ0, template, turns,
                StructureStamper.OriginOf(x, template), floorY - template.GroundLayer, StructureStamper.OriginOf(z, template),
                layerTop - WorldGenerationConfig.HellLayerPeriodBlocks + 1, layerTop);
        }
    }

    /// <summary>The template and the floor y (the layer standing on it sits here) of the hell ruin centered on (x, z) in
    /// <paramref name="layer"/>: the first pocket floor, scanning down from a hashed row, with the whole footprint level
    /// and clear above it. False when the column holds none.</summary>
    internal static bool TryFindHellSite(int x, int z, int layer, ulong hash, out StructureTemplate template, out int floorY)
    {
        floorY = 0;
        if (!StructureRegistry.TryPick(StructureKind.Hell, null, SplitMix64.Unit(hash, 4), out template)) return false;

        var top = WorldGenerationConfig.HellTopY - layer * WorldGenerationConfig.HellLayerPeriodBlocks;
        var bottom = top - HellGenerator.PocketHeight + 1;
        var start = top - (int)(SplitMix64.Unit(hash, 5) * HellGenerator.PocketHeight);
        var samples = new Dictionary<long, float>();
        var clear = template.Height - template.GroundLayer;
        for (var y = start; y > bottom; y--)
        {
            if (!HellGenerator.IsCavernAir(x, y, z, samples) || HellGenerator.IsOpen(x, y - 1, z, samples)) continue;
            if (!FootprintIsLevelAndClear(x, z, y, template.Size, clear, samples)) continue;
            floorY = y;
            return true;
        }
        return false;
    }

    private static bool FootprintIsLevelAndClear(int x, int z, int floor, int size, int clear, Dictionary<long, float> samples)
    {
        var half = size / 2;
        for (var dx = -half; dx <= half; dx++)
        for (var dz = -half; dz <= half; dz++)
        {
            if (HellGenerator.IsOpen(x + dx, floor - 1, z + dz, samples)) return false;
            for (var dy = 0; dy < clear; dy++)
                if (!HellGenerator.IsCavernAir(x + dx, floor + dy, z + dz, samples)) return false;
        }
        return true;
    }

    // ---- Sky ----

    /// <summary>The world-y band <see cref="StampSky"/> can write: the island band.</summary>
    internal static StampBand SkyBand() => SkyIslandGenerator.WriteBand();

    /// <summary>Stamps every sky ruin whose footprint reaches the chunk: at most one per island, on its top.</summary>
    public static void StampSky(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        if (StructureRegistry.Of(StructureKind.Sky).Count == 0) return;
        Span<SkyIsland> islands = stackalloc SkyIsland[16];
        var count = SkyIslands.CollectIslands(worldX0 - Reach, worldZ0 - Reach, worldX0 + Chunk.Size - 1 + Reach,
            worldZ0 + Chunk.Size - 1 + Reach, islands);
        for (var k = 0; k < count; k++)
        {
            if (!TryFindSkySite(islands[k], out var template, out var turns, out var x, out var z, out var top)) continue;
            StructureStamper.Stamp(blocks, ref state, worldX0, worldY0, worldZ0, template, turns,
                StructureStamper.OriginOf(x, template), top + 1 - template.GroundLayer, StructureStamper.OriginOf(z, template),
                int.MinValue, int.MaxValue);
        }
    }

    /// <summary>The ruin an island holds, centered on its center column: false when its roll fails or its top is not level
    /// across the whole footprint (every column an island column, within <see cref="WorldFeatureConfig.RuinSkyMaxStep"/>).</summary>
    internal static bool TryFindSkySite(in SkyIsland island, out StructureTemplate template, out int turns, out int x,
        out int z, out int top)
    {
        template = null!;
        turns = 0;
        top = 0;
        x = (int)MathF.Floor(island.CenterX);
        z = (int)MathF.Floor(island.CenterZ);
        var hash = SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ SkySalt);
        if (SplitMix64.Unit(hash, 0) >= WorldFeatureConfig.RuinSkyChance) return false;
        if (!StructureRegistry.TryPick(StructureKind.Sky, null, SplitMix64.Unit(hash, 4), out template)) return false;
        turns = (int)(SplitMix64.Mix(hash + 3) & 3);

        if (!SkyIslandGenerator.TryGetColumn(island, x, z, out _, out top)) return false;
        var half = template.Size / 2;
        for (var dx = -half; dx <= half; dx++)
        for (var dz = -half; dz <= half; dz++)
        {
            if (!SkyIslandGenerator.TryGetColumn(island, x + dx, z + dz, out _, out var columnTop)
                || Math.Abs(columnTop - top) > WorldFeatureConfig.RuinSkyMaxStep)
                return false;
        }
        return true;
    }

    /// <summary>Whether a sky ruin writes world cell (x, y, z): the footprint and a written template cell there. Lets a test (or any
    /// reader of the island point query) set aside the cells a ruin owns.</summary>
    internal static bool SkyRuinOwns(int x, int y, int z)
    {
        Span<SkyIsland> islands = stackalloc SkyIsland[16];
        var count = SkyIslands.CollectIslands(x, z, x, z, islands);
        for (var k = 0; k < count; k++)
        {
            if (!TryFindSkySite(islands[k], out var template, out var turns, out var cx, out var cz, out var top)) continue;
            var lx = x - StructureStamper.OriginOf(cx, template);
            var lz = z - StructureStamper.OriginOf(cz, template);
            var ly = y - (top + 1 - template.GroundLayer);
            if ((uint)lx < template.Size && (uint)lz < template.Size && (uint)ly < template.Height
                && template.CellAt(turns, lx, ly, lz) >= 0)
                return true;
        }
        return false;
    }

    // ---- Shared ----

    // The hashed cells whose center column could put a footprint cell into the chunk
    private static IEnumerable<(int CellX, int CellZ)> CellsReaching(int worldX0, int worldZ0)
    {
        var shift = WorldFeatureConfig.RuinCellShift;
        var minX = (worldX0 - Reach) >> shift;
        var maxX = (worldX0 + Chunk.Size - 1 + Reach) >> shift;
        var minZ = (worldZ0 - Reach) >> shift;
        var maxZ = (worldZ0 + Chunk.Size - 1 + Reach) >> shift;
        for (var cx = minX; cx <= maxX; cx++)
        for (var cz = minZ; cz <= maxZ; cz++)
            yield return (cx, cz);
    }

    private static bool TryRollCell(int cellX, int cellZ, int salt, float chance, out ulong hash, out int x, out int z)
    {
        hash = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ salt);
        return TryRollHash(hash, cellX, cellZ, chance, out x, out z);
    }

    // The cell's center column, jittered inside it, when the hash passes the chance
    private static bool TryRollHash(ulong hash, int cellX, int cellZ, float chance, out int x, out int z)
    {
        x = 0;
        z = 0;
        if (SplitMix64.Unit(hash, 0) >= chance) return false;
        var span = (ulong)CellBlocks;
        x = (cellX << WorldFeatureConfig.RuinCellShift) + (int)(SplitMix64.Mix(hash + 1) % span);
        z = (cellZ << WorldFeatureConfig.RuinCellShift) + (int)(SplitMix64.Mix(hash + 2) % span);
        return true;
    }

    private static int HeightAt(int x, int z) =>
        ColumnCache.GetHeights(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z))
            [VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];
}
