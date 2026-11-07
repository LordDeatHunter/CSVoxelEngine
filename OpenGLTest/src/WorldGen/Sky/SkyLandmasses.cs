using System;

namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// The sky's great landmasses: whole floating continents hundreds to a thousand blocks across, beside the
/// ordinary islands. A landmass is one hashed candidate per <see cref="SkyIslandConfig.LandmassCellBlocks"/>
/// cell (<see cref="TerrainSites"/>), its size skewed toward the smaller ones, kept
/// <see cref="SkyIslandConfig.LandmassGapBlocks"/> clear of every lower-hash neighbor. It is a
/// <see cref="SkyIsland"/> with an <see cref="SkyIsland.Aspect"/>, so everything that reads islands (the stamp,
/// the flora, the sky trees, the ruins, the mob spawner) reads it unchanged.
///
/// Its outline is a long ellipse of a hashed turn and aspect whose point is first domain-warped by a coherent
/// noise, so the coast bends, bulges and cuts bays rather than tracing a disc, then roughened a few blocks
/// inward at the rim. The top rolls over low hills and eases down to the rim; the underside is a broad, shallow
/// keel under the middle that thins out toward the coast, with the islands' stalactites. Pure functions of
/// position and the seed, like the islands: the point query equals the stamp bit for bit across seams, and an
/// ordinary island never stands within a landmass (<see cref="OverlapsDisc"/>).
/// </summary>
internal static class SkyLandmasses
{
    private const int LandmassSalt = 0x534B594C; // "SKYL"

    /// <summary>The warp noise channels, hills and keel; built by <see cref="Reseed"/>.</summary>
    private static FastNoiseLite _warpX, _warpZ, _hillNoise, _keelNoise;

    static SkyLandmasses() => Reseed();

    /// <summary>Builds the noises from the current seed (static constructor and <see cref="WorldSeed.Apply"/>).</summary>
    internal static void Reseed()
    {
        _warpX = TerrainGenerator.MakeNoise(175, 1f / 600f);
        _warpZ = TerrainGenerator.MakeNoise(176, 1f / 600f);
        _hillNoise = TerrainGenerator.MakeNoise(177, SkyIslandConfig.LandmassHillFrequency, octaves: 3);
        _keelNoise = TerrainGenerator.MakeNoise(178, SkyIslandConfig.LandmassKeelFrequency);
    }

    /// <summary>The landmass of cell (<paramref name="gx"/>, <paramref name="gz"/>): false when the cell rolls
    /// none or gives way to a crowding neighbor. The base height is rolled so its whole body stays in the band.</summary>
    internal static bool TryGetLandmass(int gx, int gz, out SkyIsland landmass)
    {
        landmass = default;
        if (!TerrainSites.TryRoll(gx, gz, SkyIslandConfig.LandmassCellBlocks, LandmassSalt, SkyIslandConfig.LandmassChance,
                SkyIslandConfig.LandmassSizeSkew, out var hash, out var centerX, out var centerZ, out var size))
            return false;

        var radius = MathUtils.Lerp(SkyIslandConfig.LandmassMinRadius, SkyIslandConfig.LandmassMaxRadius, size);
        if (TerrainSites.YieldsToSizedNeighbor(gx, gz, hash, centerX, centerZ, radius,
                SkyIslandConfig.LandmassMinRadius, SkyIslandConfig.LandmassMaxRadius, SkyIslandConfig.LandmassGapBlocks,
                SkyIslandConfig.LandmassCellBlocks, LandmassSalt, SkyIslandConfig.LandmassChance,
                SkyIslandConfig.LandmassSizeSkew))
            return false;

        var aspect = MathUtils.Lerp(SkyIslandConfig.LandmassMinAspect, 1f, SplitMix64.Unit(hash, 4));
        var angle = SplitMix64.Unit(hash, 5) * MathF.Tau;
        var hills = MathUtils.Lerp(SkyIslandConfig.LandmassMinHills, SkyIslandConfig.LandmassMaxHills, SplitMix64.Unit(hash, 6));
        var depth = MathUtils.Lerp(SkyIslandConfig.LandmassMinDepth, SkyIslandConfig.LandmassMaxDepth, SplitMix64.Unit(hash, 7));

        var baseMin = WorldGenerationConfig.SkyIslandBottomY + 1
                      + (int)MathF.Ceiling(depth + SkyIslandConfig.IslandStalactiteMax) + SkyIslandConfig.SkyVineMaxLength;
        var baseMax = WorldGenerationConfig.SkyIslandTopY - SkyIslandConfig.IslandFloraHeadroom
                      - (int)MathF.Ceiling(hills + SkyIslandConfig.IslandTopDetail + 0.5f);
        var baseY = baseMin + (int)(SplitMix64.Unit(hash, 8) * (baseMax - baseMin + 1));
        landmass = new SkyIsland(centerX, centerZ, radius, baseY, hills, depth, aspect, angle);
        return true;
    }

    /// <summary>Writes into <paramref name="into"/> from <paramref name="start"/> on every landmass whose bounding
    /// disc can reach the box (inclusive world columns) and returns the new count.</summary>
    internal static int Collect(int minX, int minZ, int maxX, int maxZ, Span<SkyIsland> into, int start)
    {
        var reach = SkyIslandConfig.LandmassMaxRadius;
        TerrainSites.CellRange(minX, SkyIslandConfig.LandmassCellBlocks, reach, out var cellMinX, out _);
        TerrainSites.CellRange(maxX, SkyIslandConfig.LandmassCellBlocks, reach, out _, out var cellMaxX);
        TerrainSites.CellRange(minZ, SkyIslandConfig.LandmassCellBlocks, reach, out var cellMinZ, out _);
        TerrainSites.CellRange(maxZ, SkyIslandConfig.LandmassCellBlocks, reach, out _, out var cellMaxZ);

        var count = start;
        for (var gx = cellMinX; gx <= cellMaxX; gx++)
        for (var gz = cellMinZ; gz <= cellMaxZ; gz++)
        {
            if (!TryGetLandmass(gx, gz, out var landmass)) continue;
            if (landmass.CenterX + landmass.Radius < minX || landmass.CenterX - landmass.Radius > maxX + 1
                || landmass.CenterZ + landmass.Radius < minZ || landmass.CenterZ - landmass.Radius > maxZ + 1)
                continue;
            if (count == into.Length)
                throw new InvalidOperationException($"more than {into.Length} sky islands reach one box");

            into[count++] = landmass;
        }

        return count;
    }

    /// <summary>The width of the ring of broken-off islands around <paramref name="landmass"/>.</summary>
    internal static float FringeOf(in SkyIsland landmass) =>
        MathF.Min(landmass.Radius * SkyIslandConfig.LandmassFringeShare, SkyIslandConfig.LandmassFringeMaxBlocks);

    /// <summary>
    /// The landmass whose bounding disc or fringe holds (x, z), with its 0..1 weight there: 1 inside the
    /// bounding disc, easing to 0 across the fringe. False in open sky clear of every landmass.
    /// </summary>
    internal static bool TryFindInfluence(float x, float z, out SkyIsland landmass, out float weight)
    {
        var reach = SkyIslandConfig.LandmassMaxRadius + SkyIslandConfig.LandmassFringeMaxBlocks;
        TerrainSites.CellRange(x, SkyIslandConfig.LandmassCellBlocks, reach, out var minX, out var maxX);
        TerrainSites.CellRange(z, SkyIslandConfig.LandmassCellBlocks, reach, out var minZ, out var maxZ);
        for (var gx = minX; gx <= maxX; gx++)
        for (var gz = minZ; gz <= maxZ; gz++)
        {
            if (!TryGetLandmass(gx, gz, out landmass)) continue;
            var dx = x - landmass.CenterX;
            var dz = z - landmass.CenterZ;
            var d = MathF.Sqrt(dx * dx + dz * dz);
            var fringe = FringeOf(landmass);
            if (d >= landmass.Radius + fringe) continue;
            weight = 1f - MathUtils.SmoothStep(landmass.Radius, landmass.Radius + fringe, d);
            return true;
        }

        landmass = default;
        weight = 0f;
        return false;
    }

    /// <summary>Whether any landmass's bounding disc plus fringe reaches the box (inclusive world columns): the cheap
    /// gate that lets the island scan run beside a continent where no archipelago zone stands.</summary>
    internal static bool AnyNear(int minX, int minZ, int maxX, int maxZ)
    {
        var reach = SkyIslandConfig.LandmassMaxRadius + SkyIslandConfig.LandmassFringeMaxBlocks;
        TerrainSites.CellRange(minX, SkyIslandConfig.LandmassCellBlocks, reach, out var cellMinX, out _);
        TerrainSites.CellRange(maxX, SkyIslandConfig.LandmassCellBlocks, reach, out _, out var cellMaxX);
        TerrainSites.CellRange(minZ, SkyIslandConfig.LandmassCellBlocks, reach, out var cellMinZ, out _);
        TerrainSites.CellRange(maxZ, SkyIslandConfig.LandmassCellBlocks, reach, out _, out var cellMaxZ);
        for (var gx = cellMinX; gx <= cellMaxX; gx++)
        for (var gz = cellMinZ; gz <= cellMaxZ; gz++)
        {
            if (!TryGetLandmass(gx, gz, out var landmass)) continue;
            var dx = landmass.CenterX - Math.Clamp(landmass.CenterX, minX, maxX + 1);
            var dz = landmass.CenterZ - Math.Clamp(landmass.CenterZ, minZ, maxZ + 1);
            var range = landmass.Radius + FringeOf(landmass);
            if (dx * dx + dz * dz < range * range) return true;
        }

        return false;
    }
    /// <summary>Whether a disc of <paramref name="radius"/> at (x, z) reaches any landmass's bounding disc, which
    /// is how an ordinary island gives way to a landmass.</summary>
    internal static bool OverlapsDisc(float x, float z, float radius)
    {
        var reach = SkyIslandConfig.LandmassMaxRadius + radius;
        TerrainSites.CellRange(x, SkyIslandConfig.LandmassCellBlocks, reach, out var minX, out var maxX);
        TerrainSites.CellRange(z, SkyIslandConfig.LandmassCellBlocks, reach, out var minZ, out var maxZ);
        for (var gx = minX; gx <= maxX; gx++)
        for (var gz = minZ; gz <= maxZ; gz++)
        {
            if (!TryGetLandmass(gx, gz, out var landmass)) continue;
            var dx = x - landmass.CenterX;
            var dz = z - landmass.CenterZ;
            var spacing = landmass.Radius + radius;
            if (dx * dx + dz * dz < spacing * spacing) return true;
        }

        return false;
    }

    /// <summary>The size class of a landmass, for the sky mobs' difficulty: the bigger, the harder.</summary>
    internal static int TierOf(in SkyIsland landmass) =>
        landmass.Radius >= SkyIslandConfig.LandmassBigTierRadius ? SkyZone.BigTier
        : landmass.Radius >= SkyIslandConfig.LandmassMediumTierRadius ? 1 : 0;

    /// <summary>
    /// The cells of <paramref name="landmass"/> in column (x, z), inclusive world y: false outside its outline.
    /// Every column is at least two cells thick, the top at or above the base and the bottom below it.
    /// </summary>
    internal static bool TryGetColumn(in SkyIsland landmass, int x, int z, out int bottom, out int top)
    {
        bottom = 0;
        top = -1;
        var semi = landmass.Radius / SkyIslandConfig.LandmassBoundShare;
        var dx0 = x - landmass.CenterX;
        var dz0 = z - landmass.CenterZ;
        if (dx0 * dx0 + dz0 * dz0 >= landmass.Radius * landmass.Radius) return false;

        // Warp the point by a coherent noise sampled so one wave spans about the landmass's own size; the
        // offset of the sampling keeps two landmasses from sharing a coast
        var scale = SkyIslandConfig.LandmassWarpWavelengthShare * 600f / semi;
        var ox = landmass.CenterX * 0.013f;
        var oz = landmass.CenterZ * 0.017f;
        var sx = x * scale + ox;
        var sz = z * scale + oz;
        var amp = semi * SkyIslandConfig.LandmassWarp;
        var rx = dx0 + _warpX.GetNoise(sx, sz) * amp;
        var rz = dz0 + _warpZ.GetNoise(sx, sz) * amp;

        var cos = MathF.Cos(landmass.Angle);
        var sin = MathF.Sin(landmass.Angle);
        var u = (rx * cos + rz * sin) / semi;
        var v = (-rx * sin + rz * cos) / (semi * landmass.Aspect);
        var q = u * u + v * v;
        if (q >= 1f) return false;

        var t = MathF.Sqrt(q);
        var edge = (1f - t) * semi * landmass.Aspect; // blocks in from the coast, along the short axis
        var rim = SkyIslandConfig.LandmassRimWobble * (0.5f + 0.5f * SkyIslands.OutlineNoise.GetNoise(x, z));
        if (edge <= rim) return false;

        var er = MathUtils.SmoothStep(0f, 1f, MathUtils.Clamp01(edge / SkyIslandConfig.LandmassTopRamp));
        var hills = 0.5f + 0.5f * _hillNoise.GetNoise(x, z);
        var detail = 0.5f * (SkyIslands.TopNoise.GetNoise(x, z) + 1f);
        top = landmass.BaseY + (int)((landmass.Dome * (0.2f + 0.8f * hills) + SkyIslandConfig.IslandTopDetail * detail) * er + 0.5f);

        var keelRamp = MathF.Min(semi * landmass.Aspect * 0.9f, SkyIslandConfig.LandmassKeelRamp);
        var eu = MathUtils.SmoothStep(0f, 1f, MathUtils.Clamp01(edge / keelRamp));
        var keel = 0.7f + 0.3f * (0.5f + 0.5f * _keelNoise.GetNoise(x, z));
        var stalactite = SkyIslands.StalactiteNoise.GetNoise(x, z);
        var drip = stalactite > SkyIslandConfig.IslandStalactiteThreshold
            ? (stalactite - SkyIslandConfig.IslandStalactiteThreshold) / (1f - SkyIslandConfig.IslandStalactiteThreshold)
              * SkyIslandConfig.IslandStalactiteMax * MathF.Sqrt(eu)
            : 0f;
        bottom = landmass.BaseY - 1 - (int)(landmass.Depth * MathF.Pow(eu, 0.8f) * keel + drip);
        return true;
    }
}
