using System;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The great lakes (BIOMEGEN §6, phase D): a second, much rarer lake tier beside the ponds of
/// <see cref="WaterBodies"/>. Each <see cref="WorldGenerationConfig.GreatLakeCellBlocks"/> cell rolls at
/// most one candidate through the shared hashed-site rule (<see cref="TerrainSites"/>); a candidate is
/// the union of 1 to 3 lobe discs, each outline pulled inward by
/// <see cref="TerrainNoise.GreatLakeShapeNoise"/>, and may carry an island in its main lobe. Its
/// verdict (<see cref="BuildSite"/>) is cached per cell (<see cref="SiteAt"/>), so every expensive
/// check (the rim walk, the interior pass, the river scan, the island ground) runs once per cell and
/// never per column; a column (<see cref="Sample(float, float, out int, out int, out float)"/>) only
/// rolls the cells that can reach it, culls them by distance and reads the cached verdict.
/// <para>
/// Every output (the bowl weight, the ravine guard and <see cref="NearAnyLobe"/>) is exactly 0 outside
/// the accepted lobe discs grown by <see cref="WorldGenerationConfig.GreatLakeRavineGuardBlocks"/>, and
/// within <see cref="WorldGenerationConfig.GreatLakeSpawnClearRadius"/> of the origin, which no
/// accepted site reaches. Acceptance reads only <see cref="TerrainGenerator.LandHeight"/> (and through
/// it the climate weights), <see cref="WaterBodies.RiverWeight"/> and the massif and lone-peak verdicts
/// (<see cref="TerrainPeaks.MassifSiteAt"/>, <see cref="TerrainPeaks.PeakSiteAt"/>), which never read a
/// great lake, so it never recurses; it never reads <see cref="Chunks.ColumnCache"/>, a pond or another
/// great lake. A pure function of (x, z) and <see cref="WorldGenerationConfig.Seed"/>, safe on any
/// thread.
/// </para>
/// </summary>
internal static class GreatLakes
{
    // Declared in dependency order: every const below reads only the ones above it (and config
    // consts), so no static-initializer order can bite.

    // Salts the great-lake cell hash ("GLAK") so it never correlates with another SplitMix64.Hash user.
    private const int Salt = 0x474C414B;

    /// <summary>The most lobes a great lake has: the main one plus two rolled extras.</summary>
    internal const int MaxLobes = 3;

    // The band past a lobe outline across which the ravine guard falls to 0.
    private const float Guard = WorldGenerationConfig.GreatLakeRavineGuardBlocks;

    /// <summary>
    /// The largest reach of a site in units of its main lobe's nominal radius: an extra lobe's center
    /// lies at most <see cref="WorldGenerationConfig.GreatLakeLobeOffsetMax"/> main radii out and its
    /// radius is at most <see cref="WorldGenerationConfig.GreatLakeLobeRadiusMax"/> of one, and the main
    /// lobe alone reaches 1. Every accepted site's <see cref="GreatLakeSite.Reach"/> is at most its
    /// nominal radius times this, which makes the per-column distance cull exact.
    /// </summary>
    internal const float ReachFactor =
        WorldGenerationConfig.GreatLakeLobeOffsetMax + WorldGenerationConfig.GreatLakeLobeRadiusMax > 1f
            ? WorldGenerationConfig.GreatLakeLobeOffsetMax + WorldGenerationConfig.GreatLakeLobeRadiusMax
            : 1f;

    // The farthest any lobe edge lies from its site's rolled center.
    private const float MaxReach = WorldGenerationConfig.GreatLakeMaxRadius * ReachFactor;

    // The lowest-hash spacing between candidate centers: two footprints grown by the guard never meet.
    private const float Spacing = 2f * (MaxReach + Guard);

    // Folded in as an expression rather than an early return, so a disabled tier leaves no
    // unreachable code: a chance of 0 makes every roll miss.
    private static readonly float Chance =
        WorldGenerationConfig.GreatLakesEnabled ? WorldGenerationConfig.GreatLakeChance : 0f;

    // One verdict per great-lake cell, accepted or not. A pure function of (cell, seed), so it lives
    // in the GenerationalCache like the other worldgen caches; sizing knobs in WorldGenerationConfig.
    private static readonly GenerationalCache<(int X, int Z), GreatLakeSite> Cache =
        new(WorldGenerationConfig.GreatLakeSiteCacheCapacity, WorldGenerationConfig.GreatLakeSiteCacheGenerations);

    // Static delegate, not a lambda at the call site: a closure would allocate on every call,
    // hits included, on the generation workers and the LOD builder.
    private static readonly Func<(int X, int Z), GreatLakeSite> BuildSiteFor = BuildSite;

    // A river that really cuts a channel at (x, z): its weight first, so the land height (for the
    // lowland fade that dries a river into a wash) is only read on a river.
    private static readonly Func<float, float, bool> CuttingRiverAt = static (x, z) =>
        WaterBodies.RiverWeight(x, z) > 0f && WaterBodies.RiverLowland(ForestHeight(x, z)) > 0f;

    /// <summary>The cached verdict of great-lake cell (<paramref name="gx"/>, <paramref name="gz"/>):
    /// <see cref="BuildSite"/>, computed once per cell.</summary>
    internal static GreatLakeSite SiteAt(int gx, int gz) => Cache.GetOrAdd((gx, gz), BuildSiteFor);

    /// <summary>Drops every cached verdict. Only for <see cref="WorldSeed.Apply"/>, while no worldgen
    /// thread runs: every verdict is a function of the seed.</summary>
    internal static void ResetCaches() => Cache.Clear();

    /// <summary>
    /// The great lake of a cell, or <c>default</c> when it has none. Cheap rejections come first:
    /// <list type="number">
    /// <item>the roll must pass, and the candidate must win the lowest-hash spacing against its neighbors;</item>
    /// <item>its main lobe and rolled extra lobes, grown by the guard, must stay out of the spawn clearing;</item>
    /// <item>no accepted massif or lone-peak disc may meet a lobe disc grown by the guard, so a lake never
    /// planes a relief landmark flat;</item>
    /// <item>the rim walk over each lobe's shore annulus (the band where the bowl weight stays under
    /// <see cref="WorldGenerationConfig.WaterBodyThreshold"/>, whose ground forms the banks) must find no
    /// ocean weight, a full desert gate and a biome that holds water, and a spread of at most
    /// <see cref="WorldGenerationConfig.GreatLakeSiteFlatness"/>;</item>
    /// <item>a coarse interior pass makes the same biome checks over each lobe, and refuses interior
    /// ground more than <see cref="WorldGenerationConfig.GreatLakeInteriorRiseMax"/> above the highest
    /// rim point, so no inland relief is erased silently.</item>
    /// </list>
    /// The surface is <see cref="WorldGenerationConfig.GreatLakeBankDepth"/> under the lowest rim point,
    /// the floor a rolled depth under the surface, and the shore ramp widens with the rim spread. A
    /// cutting river anywhere in a lobe's shore annulus makes the lake river-fed: it then rests at
    /// <see cref="WorldGenerationConfig.SeaLevel"/>, the river's own waterline, and is refused only when
    /// its rim-chosen surface lies below the sea or more than
    /// <see cref="WorldGenerationConfig.GreatLakeRiverFedMaxDrop"/> above it. An island rolled into the
    /// main lobe is kept only where its ground stands at least a bank above the surface, wholly on
    /// lake-holding land and crossed by no cutting river; a rejected island leaves the lake accepted.
    /// </summary>
    internal static GreatLakeSite BuildSite((int X, int Z) cell)
    {
        const int cellBlocks = WorldGenerationConfig.GreatLakeCellBlocks;
        if (!TerrainSites.TryRoll(cell.X, cell.Z, cellBlocks, Salt, Chance, WorldGenerationConfig.GreatLakeRadiusSkew,
                out var hash, out var centerX, out var centerZ, out var size))
            return default;
        if (TerrainSites.YieldsToNeighbor(cell.X, cell.Z, hash, centerX, centerZ, Spacing, cellBlocks, Salt,
                Chance, WorldGenerationConfig.GreatLakeRadiusSkew))
            return default;

        var nominal = MathUtils.Lerp(WorldGenerationConfig.GreatLakeMinRadius, WorldGenerationConfig.GreatLakeMaxRadius, size);
        var lobe0 = new GreatLakeLobe(centerX, centerZ, nominal);
        var lobe1 = default(GreatLakeLobe);
        var lobe2 = default(GreatLakeLobe);
        var lobeCount = 1;
        var reach = nominal;
        for (var e = 0; e < MaxLobes - 1; e++)
        {
            if (SplitMix64.Unit(hash, 5 + e) >= WorldGenerationConfig.GreatLakeExtraLobeChance) continue;

            var angle = SplitMix64.Unit(hash, 7 + 3 * e) * MathF.Tau;
            var offset = nominal * MathUtils.Lerp(WorldGenerationConfig.GreatLakeLobeOffsetMin,
                WorldGenerationConfig.GreatLakeLobeOffsetMax, SplitMix64.Unit(hash, 8 + 3 * e));
            var radius = nominal * MathUtils.Lerp(WorldGenerationConfig.GreatLakeLobeRadiusMin,
                WorldGenerationConfig.GreatLakeLobeRadiusMax, SplitMix64.Unit(hash, 9 + 3 * e));
            var lobe = new GreatLakeLobe(centerX + MathF.Cos(angle) * offset, centerZ + MathF.Sin(angle) * offset, radius);
            if (lobeCount == 1) lobe1 = lobe;
            else lobe2 = lobe;
            lobeCount++;
            reach = MathF.Max(reach, offset + radius);
        }

        if (MathF.Sqrt(centerX * centerX + centerZ * centerZ) - (reach + Guard)
            < WorldGenerationConfig.GreatLakeSpawnClearRadius)
            return default;

        var probe = new GreatLakeSite(true, 0, 0, 0f, false, lobeCount, reach, lobe0, lobe1, lobe2, default);
        if (MeetsReliefSite(probe)) return default;

        // Rim walk: the shore annulus of every lobe, tracking one rim range over all of them.
        var rimMin = float.PositiveInfinity;
        var rimMax = float.NegativeInfinity;
        var interiorMax = float.NegativeInfinity;
        for (var k = 0; k < lobeCount; k++)
        {
            var lobe = probe.Lobe(k);
            if (!TryWalkRings(lobe.X, lobe.Z, RimInner(lobe.Radius), lobe.Radius + 1f,
                    WorldGenerationConfig.GreatLakeRimSpacing, WorldGenerationConfig.GreatLakeSiteFlatness,
                    trackRange: true, ref rimMin, ref rimMax, ref interiorMax))
                return default;
        }

        // Interior pass: coarse, never touching the rim range.
        for (var k = 0; k < lobeCount; k++)
        {
            var lobe = probe.Lobe(k);
            if (!TryWalkRings(lobe.X, lobe.Z, 0f, RimInner(lobe.Radius), WorldGenerationConfig.GreatLakeInteriorSpacing,
                    float.PositiveInfinity, trackRange: false, ref rimMin, ref rimMax, ref interiorMax))
                return default;
        }

        if (interiorMax > rimMax + WorldGenerationConfig.GreatLakeInteriorRiseMax) return default;

        var shore = MathUtils.Lerp(WorldGenerationConfig.GreatLakeShoreShareMin, WorldGenerationConfig.GreatLakeShoreShareMax,
            (rimMax - rimMin) / WorldGenerationConfig.GreatLakeSiteFlatness);
        var depth = MathUtils.Lerp(WorldGenerationConfig.GreatLakeMinDepth, WorldGenerationConfig.GreatLakeMaxDepth,
            SplitMix64.Unit(hash, 4));
        var surface = (int)MathF.Round(rimMin - WorldGenerationConfig.GreatLakeBankDepth);

        // Rivers end in lakes: the shore annulus is exactly the band where the bowl weight is under the
        // threshold, so a river is scaled away (RiverShare) everywhere else in the lake, and one that
        // reaches the annulus meets the water at its own level.
        var riverFed = false;
        for (var k = 0; k < lobeCount && !riverFed; k++)
        {
            var lobe = probe.Lobe(k);
            riverFed = TerrainSites.AnyInAnnulus(lobe.X, lobe.Z, RimInner(lobe.Radius), lobe.Radius + 1f,
                WaterBodies.RiverSampleSpacing, CuttingRiverAt);
        }

        if (riverFed)
        {
            if (surface < WorldGenerationConfig.SeaLevel
                || surface - WorldGenerationConfig.SeaLevel > WorldGenerationConfig.GreatLakeRiverFedMaxDrop)
                return default;
            surface = WorldGenerationConfig.SeaLevel;
        }

        var floor = (int)MathF.Round(surface - depth);
        var island = TryIsland(hash, nominal, centerX, centerZ, surface);
        return new GreatLakeSite(true, surface, floor, shore, riverFed, lobeCount, reach, lobe0, lobe1, lobe2, island);
    }

    /// <summary>
    /// The island rolled into the main lobe, or <c>default</c> (radius 0) when the lake has none: the
    /// roll must pass, the island must fit inside the main lobe's dry-of-shore core, its ground must be
    /// lake-holding land at least <see cref="WorldGenerationConfig.GreatLakeBankDepth"/> above the
    /// <paramref name="surface"/> everywhere, and no cutting river may cross it.
    /// </summary>
    private static GreatLakeLobe TryIsland(ulong hash, float nominal, float centerX, float centerZ, int surface)
    {
        if (SplitMix64.Unit(hash, 13) >= WorldGenerationConfig.GreatLakeIslandChance) return default;

        var radius = MathUtils.Lerp(WorldGenerationConfig.GreatLakeIslandMinRadius,
            WorldGenerationConfig.GreatLakeIslandMaxRadius, SplitMix64.Unit(hash, 16));
        var maxOffset = nominal * (1f - WorldGenerationConfig.GreatLakeShapeAmplitude)
            * (1f - WorldGenerationConfig.GreatLakeShoreShareMax) - radius;
        if (maxOffset <= 0f) return default;

        var angle = SplitMix64.Unit(hash, 14) * MathF.Tau;
        var offset = SplitMix64.Unit(hash, 15) * maxOffset;
        var x = centerX + MathF.Cos(angle) * offset;
        var z = centerZ + MathF.Sin(angle) * offset;

        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        var unused = float.NegativeInfinity;
        if (!TryWalkRings(x, z, 0f, radius + 1f, WorldGenerationConfig.GreatLakeRimSpacing, float.PositiveInfinity,
                trackRange: true, ref min, ref max, ref unused))
            return default;
        if (min < surface + WorldGenerationConfig.GreatLakeBankDepth) return default;
        if (TerrainSites.AnyInAnnulus(x, z, 0f, radius + 1f, WaterBodies.RiverSampleSpacing, CuttingRiverAt))
            return default;

        return new GreatLakeLobe(x, z, radius);
    }

    /// <summary>
    /// Whether an accepted massif or lone peak's disc meets any lobe disc of <paramref name="site"/>
    /// grown by the guard. Reads the cached relief verdicts one way: a relief site never reads a lake.
    /// </summary>
    private static bool MeetsReliefSite(in GreatLakeSite site)
    {
        var reach = site.Reach + Guard;
        return MeetsSiteOf(site, WorldGenerationConfig.MassifCellBlocks, reach + WorldGenerationConfig.MassifMaxRadius,
                   massif: true)
               || MeetsSiteOf(site, WorldGenerationConfig.PeakCellBlocks, reach + WorldGenerationConfig.PeakMaxRadius,
                   massif: false);
    }

    private static bool MeetsSiteOf(in GreatLakeSite site, int cellBlocks, float reach, bool massif)
    {
        TerrainSites.CellRange(site.Lobe0.X, cellBlocks, reach, out var minX, out var maxX);
        TerrainSites.CellRange(site.Lobe0.Z, cellBlocks, reach, out var minZ, out var maxZ);
        for (var gx = minX; gx <= maxX; gx++)
        for (var gz = minZ; gz <= maxZ; gz++)
        {
            var relief = massif ? TerrainPeaks.MassifSiteAt(gx, gz) : TerrainPeaks.PeakSiteAt(gx, gz);
            if (!relief.Exists) continue;

            for (var k = 0; k < site.LobeCount; k++)
            {
                var lobe = site.Lobe(k);
                var dx = relief.CenterX - lobe.X;
                var dz = relief.CenterZ - lobe.Z;
                var touch = relief.Radius + lobe.Radius + Guard;
                if (dx * dx + dz * dz < touch * touch) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The inner radius of a lobe's shore annulus: inside the inward-wobbled outline and a
    /// <see cref="WorldGenerationConfig.GreatLakeRimBandShare"/> of the widest shore ramp further in,
    /// less a block, so the annulus covers every point whose bowl weight stays under the threshold.
    /// </summary>
    private static float RimInner(float radius) =>
        radius * (1f - WorldGenerationConfig.GreatLakeShapeAmplitude)
               * (1f - WorldGenerationConfig.GreatLakeShoreShareMax * WorldGenerationConfig.GreatLakeRimBandShare)
        - 1f;

    /// <summary>
    /// Walks the land height over evenly spaced rings from <paramref name="rIn"/> to
    /// <paramref name="rOut"/> (both included, at least two rings) round the center, each with at least 8
    /// points at most <paramref name="spacing"/> blocks apart (a ring of radius 0 or less is its center).
    /// False at once on any sample with an ocean weight, a desert gate under 1 or a biome that holds no
    /// water. With <paramref name="trackRange"/> each height updates <paramref name="min"/> and
    /// <paramref name="max"/> and the walk is false as soon as their spread passes
    /// <paramref name="flatness"/>; without it only <paramref name="interiorMax"/> is updated.
    /// </summary>
    private static bool TryWalkRings(float centerX, float centerZ, float rIn, float rOut, float spacing,
        float flatness, bool trackRange, ref float min, ref float max, ref float interiorMax)
    {
        var rings = Math.Max(2, (int)MathF.Ceiling((rOut - rIn) / spacing) + 1);
        for (var ring = 0; ring < rings; ring++)
        {
            var r = rIn + (rOut - rIn) * ring / (rings - 1);
            var points = r <= 0f ? 1 : Math.Max(8, (int)MathF.Ceiling(MathF.Tau * r / spacing));
            for (var k = 0; k < points; k++)
            {
                float x = centerX, z = centerZ;
                if (r > 0f)
                {
                    var angle = k * (MathF.Tau / points);
                    x += MathF.Cos(angle) * r;
                    z += MathF.Sin(angle) * r;
                }

                var h = TerrainGenerator.LandHeight(x, z, out var biome, out var ocean, out var desert, out _);
                if (ocean > 0f || WaterBodies.SurfaceWaterGate(desert) < 1f || !WaterBodies.CanHoldSurfaceWater(biome))
                    return false;

                if (!trackRange)
                {
                    if (h > interiorMax) interiorMax = h;
                    continue;
                }

                if (h < min) min = h;
                if (h > max) max = h;
                if (max - min > flatness) return false;
            }
        }

        return true;
    }

    // The pre-blend forest profile at (x, z), which a river's lowland fade reads.
    private static float ForestHeight(float x, float z)
    {
        TerrainGenerator.LandHeight(x, z, out _, out _, out _, out var forest);
        return forest;
    }

    /// <summary><see cref="Sample(float, float, out int, out int, out float, out (int X, int Z))"/>
    /// without the winning cell.</summary>
    internal static float Sample(float x, float z, out int surface, out int floor, out float guard) =>
        Sample(x, z, out surface, out floor, out guard, out _);

    /// <summary>
    /// The great-lake bowl weight at (x, z): the max over the lobes of the winning site of
    /// SmoothStep(0, <see cref="GreatLakeSite.ShoreShare"/>, 1 - d / R), where R is the lobe radius pulled
    /// inward by the shape noise, dipped to 0 over the island. <paramref name="surface"/> and
    /// <paramref name="floor"/> are that site's (<see cref="TerrainGenerator.NoWater"/> and 0 where no
    /// site has weight), <paramref name="cell"/> its cell, and <paramref name="guard"/> the ravine guard:
    /// 1 inside any lobe outline, easing to 0 at <see cref="WorldGenerationConfig.GreatLakeRavineGuardBlocks"/>
    /// past it. Sites are spaced so at most one reaches a column. Exactly 0 (both) within
    /// <see cref="WorldGenerationConfig.GreatLakeSpawnClearRadius"/> and outside every accepted lobe disc
    /// grown by the guard; each candidate is culled by distance before its cached verdict is read.
    /// </summary>
    internal static float Sample(float x, float z, out int surface, out int floor, out float guard,
        out (int X, int Z) cell)
    {
        surface = TerrainGenerator.NoWater;
        floor = 0;
        guard = 0f;
        cell = default;
        const float clear = WorldGenerationConfig.GreatLakeSpawnClearRadius;
        if (x * x + z * z < clear * clear) return 0f;

        const int cellBlocks = WorldGenerationConfig.GreatLakeCellBlocks;
        TerrainSites.CellRange(x, cellBlocks, MaxReach + Guard, out var minX, out var maxX);
        TerrainSites.CellRange(z, cellBlocks, MaxReach + Guard, out var minZ, out var maxZ);
        var best = 0f;
        var noiseRead = false;
        var noise = 0f;
        for (var gx = minX; gx <= maxX; gx++)
        for (var gz = minZ; gz <= maxZ; gz++)
        {
            if (!TerrainSites.TryRoll(gx, gz, cellBlocks, Salt, Chance, WorldGenerationConfig.GreatLakeRadiusSkew,
                    out _, out var centerX, out var centerZ, out var size))
                continue;

            var nominal = MathUtils.Lerp(WorldGenerationConfig.GreatLakeMinRadius, WorldGenerationConfig.GreatLakeMaxRadius, size);
            var cx = x - centerX;
            var cz = z - centerZ;
            var cull = nominal * ReachFactor + Guard;
            if (cx * cx + cz * cz >= cull * cull) continue;

            var site = SiteAt(gx, gz);
            if (!site.Exists) continue;

            var weight = 0f;
            for (var k = 0; k < site.LobeCount; k++)
            {
                var lobe = site.Lobe(k);
                var dx = x - lobe.X;
                var dz = z - lobe.Z;
                var d2 = dx * dx + dz * dz;
                var outer = lobe.Radius + Guard;
                if (d2 >= outer * outer) continue;

                if (!noiseRead)
                {
                    noise = TerrainNoise.GreatLakeShapeNoise.GetNoise(x, z);
                    noiseRead = true;
                }

                var radius = TerrainSites.InwardWobbleOf(lobe.Radius, WorldGenerationConfig.GreatLakeShapeAmplitude, noise);
                var d = MathF.Sqrt(d2);
                var g = 1f - MathUtils.SmoothStep(radius, radius + Guard, d);
                if (g > guard) guard = g;
                if (d >= radius) continue;

                var w = MathUtils.SmoothStep(0f, site.ShoreShare, 1f - d / radius);
                if (w > weight) weight = w;
            }

            if (weight > 0f && site.Island.Radius > 0f)
                weight *= 1f - IslandDip(site.Island, x, z);
            if (weight <= best) continue;

            best = weight;
            surface = site.Surface;
            floor = site.Floor;
            cell = (gx, gz);
        }

        return best;
    }

    // How far an island lowers the bowl weight at (x, z): 1 on its core, easing to 0 at its edge and
    // 0 beyond it.
    private static float IslandDip(in GreatLakeLobe island, float x, float z)
    {
        var dx = x - island.X;
        var dz = z - island.Z;
        var d = MathF.Sqrt(dx * dx + dz * dz);
        if (d >= island.Radius) return 0f;
        return MathUtils.SmoothStep(0f, WorldGenerationConfig.GreatLakeIslandShoreShare, 1f - d / island.Radius);
    }

    /// <summary>
    /// How much of a river survives at a point of great-lake bowl weight <paramref name="greatWeight"/>:
    /// 1 outside the lake, exactly 0 wherever lake water stands (a weight at or above
    /// <see cref="WorldGenerationConfig.WaterBodyThreshold"/>), and continuous between, so a channel
    /// ends in the lake without a step.
    /// </summary>
    internal static float RiverShare(float greatWeight) =>
        1f - MathUtils.SmoothStep(0f, WorldGenerationConfig.WaterBodyThreshold, greatWeight);

    /// <summary>
    /// Whether an accepted great lake has a lobe whose center lies within its radius plus the guard
    /// plus <paramref name="margin"/> of (x, z): what a pond asks so it yields to a great lake. Each
    /// candidate is rolled and culled by distance before its cached verdict is read, and points that
    /// close to the origin are answered without a read, since no accepted site reaches the spawn clearing.
    /// </summary>
    internal static bool NearAnyLobe(float x, float z, float margin)
    {
        var near = WorldGenerationConfig.GreatLakeSpawnClearRadius - margin;
        if (near > 0f && x * x + z * z < near * near) return false;

        const int cellBlocks = WorldGenerationConfig.GreatLakeCellBlocks;
        TerrainSites.CellRange(x, cellBlocks, MaxReach + Guard + margin, out var minX, out var maxX);
        TerrainSites.CellRange(z, cellBlocks, MaxReach + Guard + margin, out var minZ, out var maxZ);
        for (var gx = minX; gx <= maxX; gx++)
        for (var gz = minZ; gz <= maxZ; gz++)
        {
            if (!TerrainSites.TryRoll(gx, gz, cellBlocks, Salt, Chance, WorldGenerationConfig.GreatLakeRadiusSkew,
                    out _, out var centerX, out var centerZ, out var size))
                continue;

            var nominal = MathUtils.Lerp(WorldGenerationConfig.GreatLakeMinRadius, WorldGenerationConfig.GreatLakeMaxRadius, size);
            var cx = x - centerX;
            var cz = z - centerZ;
            var cull = nominal * ReachFactor + Guard + margin;
            if (cx * cx + cz * cz >= cull * cull) continue;

            var site = SiteAt(gx, gz);
            if (!site.Exists) continue;

            for (var k = 0; k < site.LobeCount; k++)
            {
                var lobe = site.Lobe(k);
                var dx = x - lobe.X;
                var dz = z - lobe.Z;
                var within = lobe.Radius + Guard + margin;
                if (dx * dx + dz * dz < within * within) return true;
            }
        }

        return false;
    }
}
