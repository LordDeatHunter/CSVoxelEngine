using System;

namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// Where the sky islands are (plans/LAYERS.md <c>L-12</c>): the island zones and the island sites. A zone is
/// one hashed candidate per <see cref="SkyIslandConfig.ZoneCellBlocks"/> cell (<see cref="TerrainSites"/>),
/// its size skewed small so big zones are rare, kept <see cref="SkyIslandConfig.ZoneGapBlocks"/> clear of
/// every lower-hash neighbor; its outline wobbles inward by a noise and its weight eases from 1 in the core to
/// 0 at the rim. An island is one hashed candidate per <see cref="SkyIslandConfig.IslandCellBlocks"/> cell
/// anywhere in the band, accepted where max(<see cref="SkyIslandConfig.IslandOpenSkyWeight"/>, its center's
/// zone weight) beats a roll, so islands stand everywhere and a zone is a denser cluster on top; it is kept
/// <see cref="SkyIslandConfig.IslandGapBlocks"/> clear of its lower-hash neighbors, so two islands never
/// touch, and off every landmass's land (<see cref="SkyLandmasses.TouchesLand"/>). Everything is a pure
/// function of position and <see cref="WorldGenerationConfig.Seed"/>, safe on any worker thread; nothing is
/// cached, since a chunk asks for its few sites once (<see cref="CollectIslands"/>).
/// </summary>
public static class SkyIslands
{
    /// <summary>
    /// The most islands <see cref="CollectIslands"/> writes for a box no wider than the sky ruin box: at most
    /// 2 x 2 island cells reach it (see <see cref="SkyIslandConfig.IslandCellBlocks"/>), plus the few landmass
    /// discs whose bounds reach it (landmass discs keep <see cref="SkyIslandConfig.LandmassGapBlocks"/> apart);
    /// every caller's stack buffer is this long.
    /// </summary>
    public const int MaxIslandsPerBox = 16;

    private const int ZoneSalt = 0x534B595A; // "SKYZ"
    private const int IslandSalt = 0x534B5949; // "SKYI"

    private static FastNoiseLite ZoneNoise;

    /// <summary>The island outline noise.</summary>
    internal static FastNoiseLite OutlineNoise;

    /// <summary>The noise that roughens an island's top.</summary>
    internal static FastNoiseLite TopNoise;

    /// <summary>The noise whose high values hang stalactites under an island.</summary>
    internal static FastNoiseLite StalactiteNoise;

    static SkyIslands() => Reseed();

    /// <summary>Builds the noises from the current seed (static constructor and <see cref="WorldSeed.Apply"/>).</summary>
    internal static void Reseed()
    {
        ZoneNoise = TerrainGenerator.MakeNoise(168, SkyIslandConfig.ZoneWobbleFrequency);
        OutlineNoise = TerrainGenerator.MakeNoise(169, SkyIslandConfig.IslandWobbleFrequency);
        TopNoise = TerrainGenerator.MakeNoise(170, SkyIslandConfig.IslandTopDetailFrequency);
        StalactiteNoise = TerrainGenerator.MakeNoise(171, SkyIslandConfig.IslandStalactiteFrequency);
        SkyLandmasses.Reseed();
    }

    /// <summary>
    /// The zone at (x, z): the deepest zone over every cell that can reach it, or the default (weight 0) in
    /// open sky. Pure; the query <c>L-15</c>'s sky spawner scales its mobs by (<see cref="SkyZone.Tier"/>).
    /// </summary>
    public static SkyZone ZoneAt(float x, float z)
    {
        var best = default(SkyZone);
        TerrainSites.CellRange(x, SkyIslandConfig.ZoneCellBlocks, SkyIslandConfig.ZoneMaxRadius, out var minX, out var maxX);
        TerrainSites.CellRange(z, SkyIslandConfig.ZoneCellBlocks, SkyIslandConfig.ZoneMaxRadius, out var minZ, out var maxZ);
        for (var gx = minX; gx <= maxX; gx++)
        for (var gz = minZ; gz <= maxZ; gz++)
        {
            if (!TryGetZone(gx, gz, out var centerX, out var centerZ, out var radius, out var tier)) continue;

            var weight = ZoneWeight(centerX, centerZ, radius, x, z);
            if (weight > best.Weight) best = new SkyZone(weight, tier, radius, centerX, centerZ);
        }

        // A landmass is its own zone: full weight over its bounding disc, easing out across its fringe, where
        // the broken-off islands drift
        if (best.Weight < 1f && SkyLandmasses.TryFindInfluence(x, z, out var landmass, out var landWeight)
            && landWeight > best.Weight)
            best = new SkyZone(landWeight, SkyLandmasses.TierOf(landmass), landmass.Radius, landmass.CenterX, landmass.CenterZ);
        return best;
    }

    /// <summary>The size class of a zone of rolled size <paramref name="size"/> (0..1): 0 small, 1 medium, 2 big.</summary>
    public static int TierOf(float size) =>
        size >= SkyIslandConfig.ZoneLargeSize ? SkyZone.BigTier : size >= SkyIslandConfig.ZoneMediumSize ? 1 : 0;

    /// <summary>The zone of cell (<paramref name="gx"/>, <paramref name="gz"/>): false when the cell rolls none
    /// or its roll gives way to a crowding neighbor.</summary>
    internal static bool TryGetZone(int gx, int gz, out float centerX, out float centerZ, out float radius, out int tier)
    {
        radius = 0f;
        tier = 0;
        if (!TerrainSites.TryRoll(gx, gz, SkyIslandConfig.ZoneCellBlocks, ZoneSalt, SkyIslandConfig.ZoneChance,
                SkyIslandConfig.ZoneSizeSkew, out var hash, out centerX, out centerZ, out var size))
            return false;

        radius = MathUtils.Lerp(SkyIslandConfig.ZoneMinRadius, SkyIslandConfig.ZoneMaxRadius, size);
        tier = TierOf(size);
        return !TerrainSites.YieldsToSizedNeighbor(gx, gz, hash, centerX, centerZ, radius,
            SkyIslandConfig.ZoneMinRadius, SkyIslandConfig.ZoneMaxRadius, SkyIslandConfig.ZoneGapBlocks,
            SkyIslandConfig.ZoneCellBlocks, ZoneSalt, SkyIslandConfig.ZoneChance, SkyIslandConfig.ZoneSizeSkew);
    }

    /// <summary>0..1 how deep (x, z) lies in the zone: 1 inside the core share of its wobbled outline, easing
    /// to 0 at the outline, 0 outside it.</summary>
    private static float ZoneWeight(float centerX, float centerZ, float radius, float x, float z)
    {
        var dx = x - centerX;
        var dz = z - centerZ;
        var d2 = dx * dx + dz * dz;
        if (d2 >= radius * radius) return 0f;

        var outline = TerrainSites.InwardWobble(radius, SkyIslandConfig.ZoneWobble, ZoneNoise, x, z);
        var d = MathF.Sqrt(d2);
        if (d >= outline) return 0f;
        return 1f - MathUtils.SmoothStep(SkyIslandConfig.ZoneCoreShare, 1f, d / outline);
    }

    /// <summary>
    /// The island of cell (<paramref name="gx"/>, <paramref name="gz"/>): false when the cell rolls none, the
    /// greater of the open-sky floor and its center's zone weight loses the roll, it gives way to a crowding
    /// neighbor or it would touch a landmass's land. Its base height is rolled
    /// so its whole body (dome, top detail, underside and stalactites) stays inside the island band.
    /// </summary>
    public static bool TryGetIsland(int gx, int gz, out SkyIsland island)
    {
        island = default;
        if (!TerrainSites.TryRoll(gx, gz, SkyIslandConfig.IslandCellBlocks, IslandSalt, SkyIslandConfig.IslandChance,
                SkyIslandConfig.IslandSizeSkew, out var hash, out var centerX, out var centerZ, out var size))
            return false;

        // Accepted when max(open-sky floor, zone weight) beats the roll; the zone is only asked above the floor
        var roll = SplitMix64.Unit(hash, 4);
        if (roll >= SkyIslandConfig.IslandOpenSkyWeight && ZoneAt(centerX, centerZ).Weight <= roll) return false;

        var radius = MathUtils.Lerp(SkyIslandConfig.IslandMinRadius, SkyIslandConfig.IslandMaxRadius, size);
        if (TerrainSites.YieldsToSizedNeighbor(gx, gz, hash, centerX, centerZ, radius,
                SkyIslandConfig.IslandMinRadius, SkyIslandConfig.IslandMaxRadius, SkyIslandConfig.IslandGapBlocks,
                SkyIslandConfig.IslandCellBlocks, IslandSalt, SkyIslandConfig.IslandChance, SkyIslandConfig.IslandSizeSkew))
            return false;

        // A landmass wins its land: no island stands within the gap of it, though one may fill its bays and the
        // open sky of its bounding disc. Last, since it is the dearest check
        if (SkyLandmasses.TouchesLand(centerX, centerZ, radius + SkyIslandConfig.IslandGapBlocks)) return false;

        var dome = radius * SkyIslandConfig.IslandDomePerRadius;
        var depth = radius * MathUtils.Lerp(SkyIslandConfig.IslandMinDepthPerRadius,
            SkyIslandConfig.IslandMaxDepthPerRadius, SplitMix64.Unit(hash, 5));

        // The column shape tops out at dome + detail (rounded) above the base, with the flora's
        // headroom over it, and hangs at most 1 + depth + a stalactite + a vine below it
        var baseMin = WorldGenerationConfig.SkyIslandBottomY + 1
                      + (int)MathF.Ceiling(depth + SkyIslandConfig.IslandStalactiteMax) + SkyIslandConfig.SkyVineMaxLength;
        var baseMax = WorldGenerationConfig.SkyIslandTopY - SkyIslandConfig.IslandFloraHeadroom
                      - (int)MathF.Ceiling(dome + SkyIslandConfig.IslandTopDetail + 0.5f);
        var baseY = baseMin + (int)(SplitMix64.Unit(hash, 6) * (baseMax - baseMin + 1));
        island = new SkyIsland(centerX, centerZ, radius, baseY, dome, depth);
        return true;
    }

    /// <summary>
    /// Writes into <paramref name="into"/>, landmasses first and then in cell order, every island whose nominal
    /// disc can reach the box <paramref name="minX"/>..<paramref name="maxX"/>,
    /// <paramref name="minZ"/>..<paramref name="maxZ"/> (inclusive world columns), and returns how many. The
    /// same order for any box, so a column asks a chunk's list and its own alike. A box no wider than the sky
    /// ruin box never yields more than <see cref="MaxIslandsPerBox"/>.
    /// </summary>
    public static int CollectIslands(int minX, int minZ, int maxX, int maxZ, Span<SkyIsland> into)
    {
        var count = SkyLandmasses.Collect(minX, minZ, maxX, maxZ, into, 0);
        var reach = SkyIslandConfig.IslandMaxRadius;
        TerrainSites.CellRange(minX, SkyIslandConfig.IslandCellBlocks, reach, out var cellMinX, out _);
        TerrainSites.CellRange(maxX, SkyIslandConfig.IslandCellBlocks, reach, out _, out var cellMaxX);
        TerrainSites.CellRange(minZ, SkyIslandConfig.IslandCellBlocks, reach, out var cellMinZ, out _);
        TerrainSites.CellRange(maxZ, SkyIslandConfig.IslandCellBlocks, reach, out _, out var cellMaxZ);

        for (var gx = cellMinX; gx <= cellMaxX; gx++)
        for (var gz = cellMinZ; gz <= cellMaxZ; gz++)
        {
            if (!TryGetIsland(gx, gz, out var island)) continue;
            if (island.CenterX + island.Radius < minX || island.CenterX - island.Radius > maxX + 1
                || island.CenterZ + island.Radius < minZ || island.CenterZ - island.Radius > maxZ + 1)
                continue;
            if (count == into.Length)
                throw new InvalidOperationException($"more than {into.Length} sky islands reach one box");

            into[count++] = island;
        }

        return count;
    }
}
