using System;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The great lakes (BIOMEGEN §6, phase D): a second, much rarer lake tier beside the ponds of
/// <see cref="WaterBodies"/>. Each <see cref="WorldGenerationConfig.GreatLakeCellBlocks"/> cell rolls at
/// most one candidate through the shared hashed-site rule (<see cref="TerrainSites"/>); a candidate is
/// the union of 1 to 3 lobe discs, each outline pulled inward by
/// <see cref="TerrainNoise.GreatLakeShapeNoise"/>, and may carry an island in its main lobe. Its
/// verdict (<see cref="BuildSite"/>) is cached per cell (<see cref="SiteAt"/>), so every expensive
/// check (the rim walk, the interior pass, the river scan, the ramp's river banks, the outflow corridor,
/// the island ground) runs once per cell and never per column; a column (<see cref="Sample"/>) only rolls
/// the cells that can reach it, culls them by distance and reads the cached verdict.
/// <para>
/// An elevated lake may carry a river ramp: past its lobes the river level steps down one block every
/// <see cref="WorldGenerationConfig.GreatLakeRiverRunBlocks"/> from its surface to the sea
/// (<see cref="RiverLevelAt"/>), so a river reaching it meets it at its own surface instead of forcing it
/// to the sea level, and it may let an outflow channel out of its low side down the same ramp
/// (<see cref="OutflowWeight"/>). A river-fed lake whose rivers cannot step down from it falls back to the
/// sea level.
/// </para>
/// <para>
/// Every output (the bowl weight, the ravine guard, the river level, bank and outflow of a ramped site
/// and <see cref="NearAnyFootprint"/>) is exactly 0, <see cref="WorldGenerationConfig.SeaLevel"/> or
/// <see cref="TerrainGenerator.NoWater"/> outside the accepted lobe discs grown by
/// <see cref="WorldGenerationConfig.GreatLakeRavineGuardBlocks"/> (by <see cref="RiverReach"/> for a
/// ramped site), and within <see cref="WorldGenerationConfig.GreatLakeSpawnClearRadius"/> of the origin,
/// which no accepted site reaches. Acceptance reads only <see cref="TerrainGenerator.LandHeight"/> (and through
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
    /// The farthest past a nominal lobe edge any river output of a ramped lake reaches: its bank zone
    /// runs to <see cref="GreatLakeSite.RampLength"/> + 1 (at most
    /// <see cref="WorldGenerationConfig.GreatLakeRampMaxLength"/> + 1), then its ravine guard eases out
    /// over <see cref="WorldGenerationConfig.GreatLakeRavineGuardBlocks"/>. The outflow lies inside the
    /// bank zone by its length cap.
    /// </summary>
    internal const float RiverReach = WorldGenerationConfig.GreatLakeRampMaxLength + 1f + Guard;

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
        WaterBodies.RiverWeight(x, z) > 0f && WaterBodies.RiverLowland(ForestHeight(x, z), WorldGenerationConfig.SeaLevel) > 0f;

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
    /// cutting river anywhere in a lobe's shore annulus makes the lake river-fed; a river-fed lake under
    /// the sea is refused. A lake over the sea may then get a river ramp
    /// (<see cref="GreatLakeSite.RampLength"/>): the river level steps down from its surface to the sea
    /// over the ramp (<see cref="RiverLevelAt"/>), kept only when it fits (<see cref="RampFits"/>) and every
    /// river on it stands in ground over its level (<see cref="RiversHoldOnRamp"/>). A ramped lake may
    /// get an outflow (<see cref="TryOutflow"/>); a river-fed lake keeps its ramp and its own surface
    /// either way, a rim-fed one only to carry an outflow. A river-fed lake without a ramp rests at
    /// <see cref="WorldGenerationConfig.SeaLevel"/>, the river's own waterline, and is refused when its
    /// rim-chosen surface lies more than <see cref="WorldGenerationConfig.GreatLakeRiverFedMaxDrop"/> above
    /// it. An island rolled into the main lobe is kept only where its ground stands at least a bank above
    /// the surface, wholly on lake-holding land and crossed by no river cutting toward the lake's river
    /// level; a rejected island leaves the lake accepted.
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

        var probe = new GreatLakeSite(true, 0, 0, 0f, false, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
            lobeCount, reach, lobe0, lobe1, lobe2, default);
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

        if (riverFed && surface < WorldGenerationConfig.SeaLevel) return default;

        // An elevated lake keeps its own surface when its rivers can step down from it: the ramp must
        // fit inside the site spacing and every river on it must stand in ground over its level. A
        // river-fed lake keeps the ramp whatever the outflow; a rim-fed one only to carry an outflow.
        var rampLength = 0f;
        var outflow = default(GreatLakeSite); // carrier of the outflow fields
        if (surface > WorldGenerationConfig.SeaLevel && RampFits(reach, centerX, centerZ, surface, out var length))
        {
            var ramped = probe with { Surface = surface, RampLength = length };
            if (RiversHoldOnRamp(ramped))
            {
                var withOutflow = TryOutflow(ramped);
                if (riverFed || withOutflow.HasOutflow)
                {
                    rampLength = length;
                    outflow = withOutflow;
                }
            }
        }

        // A river-fed lake whose rivers cannot step down from it rests at the river's sea level instead.
        if (riverFed && rampLength <= 0f)
        {
            if (surface - WorldGenerationConfig.SeaLevel > WorldGenerationConfig.GreatLakeRiverFedMaxDrop)
                return default;
            surface = WorldGenerationConfig.SeaLevel;
        }

        var floor = (int)MathF.Round(surface - depth);
        var island = TryIsland(hash, nominal, centerX, centerZ, surface,
            rampLength > 0f ? surface : WorldGenerationConfig.SeaLevel);
        return new GreatLakeSite(true, surface, floor, shore, riverFed, rampLength, outflow.OutflowX, outflow.OutflowZ,
            outflow.OutflowDirX, outflow.OutflowDirZ, outflow.OutflowStart, outflow.OutflowLength,
            lobeCount, reach, lobe0, lobe1, lobe2, island);
    }

    /// <summary>
    /// Whether a lake of this <paramref name="reach"/> round (<paramref name="centerX"/>,
    /// <paramref name="centerZ"/>) can carry a river ramp down from <paramref name="surface"/>:
    /// <paramref name="length"/> is (<paramref name="surface"/> - <see cref="WorldGenerationConfig.SeaLevel"/>)
    /// times <see cref="WorldGenerationConfig.GreatLakeRiverRunBlocks"/>, which must be at most
    /// <see cref="WorldGenerationConfig.GreatLakeRampMaxLength"/>; the reach grown by <see cref="RiverReach"/>
    /// must stay within half the site spacing, so two footprints never meet and the per-column cell range is
    /// unchanged; and it must stay out of the spawn clearing.
    /// </summary>
    private static bool RampFits(float reach, float centerX, float centerZ, int surface, out float length)
    {
        length = (surface - WorldGenerationConfig.SeaLevel) * WorldGenerationConfig.GreatLakeRiverRunBlocks;
        return length <= WorldGenerationConfig.GreatLakeRampMaxLength
               && reach + RiverReach <= MaxReach + Guard
               && MathF.Sqrt(centerX * centerX + centerZ * centerZ) - (reach + RiverReach)
               >= WorldGenerationConfig.GreatLakeSpawnClearRadius;
    }

    // How far to either side of a river sample its banks are read.
    private const float BankProbeBlocks = 2f;

    /// <summary>
    /// Whether every noise river on the ramp of <paramref name="site"/> stands in ground that can hold it
    /// at its stepped level: over each lobe, rings from a block past the edge out past the ramp (the
    /// rim walk's layout at <see cref="WaterBodies.RiverSampleSpacing"/>), and at every river sample more
    /// than a block past every lobe whose <see cref="RiverLevelAt"/> is above the sea, the sample and the
    /// points <see cref="BankProbeBlocks"/> to either side along x and z must pass <see cref="HoldsRiver"/>.
    /// </summary>
    private static bool RiversHoldOnRamp(in GreatLakeSite site)
    {
        const float spacing = WaterBodies.RiverSampleSpacing;
        for (var k = 0; k < site.LobeCount; k++)
        {
            var lobe = site.Lobe(k);
            var rIn = lobe.Radius + 1f;
            var rOut = lobe.Radius + site.RampLength + 1f;
            var rings = Math.Max(2, (int)MathF.Ceiling((rOut - rIn) / spacing) + 1);
            for (var ring = 0; ring < rings; ring++)
            {
                var r = rIn + (rOut - rIn) * ring / (rings - 1);
                var points = Math.Max(8, (int)MathF.Ceiling(MathF.Tau * r / spacing));
                for (var p = 0; p < points; p++)
                {
                    var angle = p * (MathF.Tau / points);
                    var x = lobe.X + MathF.Cos(angle) * r;
                    var z = lobe.Z + MathF.Sin(angle) * r;
                    var beyond = Beyond(site, x, z);
                    if (beyond <= 1f) continue;

                    var level = RiverLevelAt(site, beyond);
                    if (level <= WorldGenerationConfig.SeaLevel || WaterBodies.RiverWeight(x, z) <= 0f) continue;

                    if (!HoldsRiver(x, z, level, out _)
                        || !HoldsRiver(x - BankProbeBlocks, z, level, out _)
                        || !HoldsRiver(x + BankProbeBlocks, z, level, out _)
                        || !HoldsRiver(x, z - BankProbeBlocks, level, out _)
                        || !HoldsRiver(x, z + BankProbeBlocks, level, out _))
                        return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the ground at (<paramref name="x"/>, <paramref name="z"/>) can bank a river at
    /// <paramref name="level"/>: no ocean weight, a full desert gate, a biome that holds water and land at
    /// least <see cref="WorldGenerationConfig.GreatLakeRiverBankRise"/> over the level.
    /// <paramref name="forest"/> is the pre-blend forest profile there, for the lowland fade.
    /// </summary>
    private static bool HoldsRiver(float x, float z, int level, out float forest)
    {
        var land = TerrainGenerator.LandHeight(x, z, out var biome, out var ocean, out var desert, out forest);
        return ocean <= 0f && WaterBodies.SurfaceWaterGate(desert) >= 1f && WaterBodies.CanHoldSurfaceWater(biome)
               && land >= level + WorldGenerationConfig.GreatLakeRiverBankRise;
    }

    /// <summary>How many outflow candidates lie on <paramref name="lobe"/>'s ring a block past its edge:
    /// the rim walk's outer ring, at most <see cref="WorldGenerationConfig.GreatLakeRimSpacing"/> apart.</summary>
    internal static int OutflowRingCount(in GreatLakeLobe lobe) =>
        Math.Max(8, (int)MathF.Ceiling(MathF.Tau * (lobe.Radius + 1f) / WorldGenerationConfig.GreatLakeRimSpacing));

    /// <summary>Outflow candidate <paramref name="i"/> of <paramref name="count"/> on
    /// <paramref name="lobe"/>'s ring a block past its edge, starting on +X.</summary>
    internal static (float X, float Z) OutflowRingPoint(in GreatLakeLobe lobe, int i, int count)
    {
        var angle = i * (MathF.Tau / count);
        var r = lobe.Radius + 1f;
        return (lobe.X + MathF.Cos(angle) * r, lobe.Z + MathF.Sin(angle) * r);
    }

    /// <summary>
    /// Whether a point on lobe <paramref name="k"/>'s outflow ring may start the outflow: it lies outside
    /// every other lobe grown by a block, and the radial direction from lobe <paramref name="k"/>'s center
    /// through it moves away from every other lobe center, so a channel running that way only ever grows
    /// its distance past the lobes and its river level never rises.
    /// </summary>
    internal static bool IsOutflowCandidate(in GreatLakeSite site, int k, float x, float z)
    {
        var own = site.Lobe(k);
        var ox = x - own.X;
        var oz = z - own.Z;
        for (var j = 0; j < site.LobeCount; j++)
        {
            if (j == k) continue;

            var other = site.Lobe(j);
            var dx = x - other.X;
            var dz = z - other.Z;
            var within = other.Radius + 1f;
            if (dx * dx + dz * dz < within * within) return false;
            if (ox * dx + oz * dz < 0f) return false;
        }

        return true;
    }

    /// <summary>
    /// <paramref name="site"/> with its outflow set, or unchanged (no outflow) when none fits. The channel
    /// leaves from the lowest eligible outflow candidate (<see cref="IsOutflowCandidate"/>, the first on a
    /// tie) radially away from its lobe, reaches back into standing water past the lobe's shore annulus,
    /// and runs as far as its corridor holds (<see cref="CorridorLength"/>), at most to where its whole
    /// width stays inside the ramp's bank zone; shorter than
    /// <see cref="WorldGenerationConfig.GreatLakeOutflowMinLength"/> is none.
    /// </summary>
    private static GreatLakeSite TryOutflow(in GreatLakeSite site)
    {
        const float halfWidth = WorldGenerationConfig.GreatLakeOutflowHalfWidth;
        var cap = site.RampLength - halfWidth - 1f;
        if (cap < WorldGenerationConfig.GreatLakeOutflowMinLength) return site;

        var bestLand = float.PositiveInfinity;
        var bestLobe = -1;
        float bestX = 0f, bestZ = 0f;
        for (var k = 0; k < site.LobeCount; k++)
        {
            var lobe = site.Lobe(k);
            var count = OutflowRingCount(lobe);
            for (var i = 0; i < count; i++)
            {
                var (x, z) = OutflowRingPoint(lobe, i, count);
                if (!IsOutflowCandidate(site, k, x, z)) continue;

                var land = TerrainGenerator.LandHeight(x, z, out _, out _, out _, out _);
                if (land >= bestLand) continue;

                bestLand = land;
                bestLobe = k;
                bestX = x;
                bestZ = z;
            }
        }

        if (bestLobe < 0) return site;

        var from = site.Lobe(bestLobe);
        var ox = bestX - from.X;
        var oz = bestZ - from.Z;
        var distance = MathF.Sqrt(ox * ox + oz * oz);
        var dirX = ox / distance;
        var dirZ = oz / distance;
        var length = CorridorLength(site, bestX, bestZ, dirX, dirZ, cap);
        if (length < WorldGenerationConfig.GreatLakeOutflowMinLength) return site;

        return site with
        {
            OutflowX = bestX,
            OutflowZ = bestZ,
            OutflowDirX = dirX,
            OutflowDirZ = dirZ,
            OutflowStart = -(distance - RimInner(from.Radius)) - halfWidth,
            OutflowLength = length,
        };
    }

    /// <summary>
    /// How far from (<paramref name="x0"/>, <paramref name="z0"/>) along the unit direction the outflow's
    /// corridor holds, at most <paramref name="cap"/>, or -1 when it fails at once. Walked at most
    /// <see cref="WaterBodies.RiverSampleSpacing"/> apart along the axis and across it a block past the
    /// channel's half-width either side: every point more than a block past the lobes must pass
    /// <see cref="HoldsRiver"/> at its own <see cref="RiverLevelAt"/>, and the axis must cut a channel there
    /// (<see cref="WaterBodies.RiverLowland"/> above 0), so the outflow carries water rather than drying
    /// into a wash. Stops at the first step that fails.
    /// </summary>
    private static float CorridorLength(in GreatLakeSite site, float x0, float z0, float dirX, float dirZ, float cap)
    {
        const float spacing = WaterBodies.RiverSampleSpacing;
        const float side = WorldGenerationConfig.GreatLakeOutflowHalfWidth + 1f;
        var steps = Math.Max(1, (int)MathF.Ceiling(cap / spacing));
        var across = Math.Max(1, (int)MathF.Ceiling(2f * side / spacing));
        var length = -1f;
        for (var i = 0; i <= steps; i++)
        {
            var s = cap * i / steps;
            var ax = x0 + dirX * s;
            var az = z0 + dirZ * s;
            var axisBeyond = Beyond(site, ax, az);
            var axisLevel = RiverLevelAt(site, axisBeyond);
            var axisHolds = HoldsRiver(ax, az, axisLevel, out var forest);
            if ((axisBeyond > 1f && !axisHolds) || WaterBodies.RiverLowland(forest, axisLevel) <= 0f) break;

            if (!CrossHolds(site, ax, az, dirX, dirZ, side, across)) break;

            length = s;
        }

        return length;
    }

    // Whether the corridor's cross-section through (ax, az) holds: across + 1 points from -side to +side
    // perpendicular to the direction, each more than a block past the lobes passing HoldsRiver.
    private static bool CrossHolds(in GreatLakeSite site, float ax, float az, float dirX, float dirZ, float side,
        int across)
    {
        for (var j = 0; j <= across; j++)
        {
            var o = -side + 2f * side * j / across;
            var px = ax - dirZ * o;
            var pz = az + dirX * o;
            var beyond = Beyond(site, px, pz);
            if (beyond <= 1f) continue;
            if (!HoldsRiver(px, pz, RiverLevelAt(site, beyond), out _)) return false;
        }

        return true;
    }

    /// <summary>
    /// The island rolled into the main lobe, or <c>default</c> (radius 0) when the lake has none: the
    /// roll must pass, the island must fit inside the main lobe's dry-of-shore core, its ground must be
    /// lake-holding land at least <see cref="WorldGenerationConfig.GreatLakeBankDepth"/> above the
    /// <paramref name="surface"/> everywhere, and no river cutting a channel toward
    /// <paramref name="level"/> (the river level inside the lake: its surface on a ramp, else the sea
    /// level) may cross it.
    /// </summary>
    private static GreatLakeLobe TryIsland(ulong hash, float nominal, float centerX, float centerZ, int surface,
        int level)
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
        // Captures the level, so it allocates once per verdict, never per column.
        if (TerrainSites.AnyInAnnulus(x, z, 0f, radius + 1f, WaterBodies.RiverSampleSpacing,
                (rx, rz) => WaterBodies.RiverWeight(rx, rz) > 0f
                            && WaterBodies.RiverLowland(ForestHeight(rx, rz), level) > 0f))
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

    /// <summary>
    /// How far (<paramref name="x"/>, <paramref name="z"/>) lies past the nearest nominal lobe edge of
    /// <paramref name="site"/>: the least distance to a lobe center less that lobe's radius, negative
    /// inside a lobe disc. 1-Lipschitz, being a minimum of distances.
    /// </summary>
    internal static float Beyond(in GreatLakeSite site, float x, float z)
    {
        var beyond = float.PositiveInfinity;
        for (var k = 0; k < site.LobeCount; k++)
        {
            var lobe = site.Lobe(k);
            float dx = x - lobe.X, dz = z - lobe.Z;
            var b = MathF.Sqrt(dx * dx + dz * dz) - lobe.Radius;
            if (b < beyond) beyond = b;
        }

        return beyond;
    }

    /// <summary>
    /// The river level <paramref name="beyond"/> blocks past a lobe edge (<see cref="Beyond"/>): the
    /// lake's own <see cref="GreatLakeSite.Surface"/> at or inside the edge, then one block lower every
    /// <see cref="WorldGenerationConfig.GreatLakeRiverRunBlocks"/>, never below
    /// <see cref="WorldGenerationConfig.SeaLevel"/>. Never rises away from the lake, and two points a block
    /// apart differ by at most one level. Only meaningful with <see cref="GreatLakeSite.RampLength"/> above 0.
    /// </summary>
    internal static int RiverLevelAt(in GreatLakeSite site, float beyond)
    {
        if (beyond <= 0f) return site.Surface;
        var level = site.Surface - (int)MathF.Floor(beyond / WorldGenerationConfig.GreatLakeRiverRunBlocks);
        return Math.Max(WorldGenerationConfig.SeaLevel, level);
    }

    /// <summary>
    /// The outflow channel's weight at (<paramref name="x"/>, <paramref name="z"/>), 0 when the site has no
    /// outflow: a parabolic cross-section over <see cref="WorldGenerationConfig.GreatLakeOutflowHalfWidth"/>
    /// either side of its axis, from <see cref="GreatLakeSite.OutflowStart"/> to
    /// <see cref="GreatLakeSite.OutflowLength"/> along it, tapering shut over the last
    /// <see cref="WorldGenerationConfig.GreatLakeOutflowTaperBlocks"/>.
    /// </summary>
    internal static float OutflowWeight(in GreatLakeSite site, float x, float z)
    {
        if (!site.HasOutflow) return 0f;

        float dx = x - site.OutflowX, dz = z - site.OutflowZ;
        var s = dx * site.OutflowDirX + dz * site.OutflowDirZ;
        if (s < site.OutflowStart || s >= site.OutflowLength) return 0f;

        var perp = MathF.Abs(dx * site.OutflowDirZ - dz * site.OutflowDirX);
        if (perp >= WorldGenerationConfig.GreatLakeOutflowHalfWidth) return 0f;

        var t = perp / WorldGenerationConfig.GreatLakeOutflowHalfWidth;
        var taper = 1f - MathUtils.SmoothStep(site.OutflowLength - WorldGenerationConfig.GreatLakeOutflowTaperBlocks,
            site.OutflowLength, s);
        return (1f - t * t) * taper;
    }

    /// <summary>
    /// The great-lake fields at (x, z). The return value is the bowl weight: the max over the lobes of the
    /// winning site of SmoothStep(0, <see cref="GreatLakeSite.ShoreShare"/>, 1 - d / R), where R is the lobe
    /// radius pulled inward by the shape noise, dipped to 0 over the island. <paramref name="surface"/> and
    /// <paramref name="floor"/> are that site's (<see cref="TerrainGenerator.NoWater"/> and 0 where no
    /// site has weight) and <paramref name="cell"/> its cell. <paramref name="guard"/> is the one ravine
    /// guard: 1 inside any lobe outline, easing to 0 at <see cref="WorldGenerationConfig.GreatLakeRavineGuardBlocks"/>
    /// past it, and for a ramped site (<see cref="GreatLakeSite.RampLength"/> above 0) 1 out to the ramp's
    /// bank zone, easing to 0 over the same band past it. Inside a ramped site's footprint
    /// <paramref name="riverLevel"/> is <see cref="RiverLevelAt"/> of the column's distance past the lobes,
    /// <paramref name="riverBank"/> the level one block nearer the lake (inside the bank zone only) and
    /// <paramref name="outflow"/> the outflow channel's weight (<see cref="OutflowWeight"/>). Sites are
    /// spaced so at most one reaches a column. Every output is exactly 0,
    /// <see cref="WorldGenerationConfig.SeaLevel"/> or <see cref="TerrainGenerator.NoWater"/> within
    /// <see cref="WorldGenerationConfig.GreatLakeSpawnClearRadius"/> and outside every accepted footprint
    /// (the lobe discs grown by the guard, or by <see cref="RiverReach"/> for a ramped site); each candidate
    /// is culled by distance before its cached verdict is read.
    /// </summary>
    internal static float Sample(float x, float z, out int surface, out int floor, out float guard,
        out int riverLevel, out int riverBank, out float outflow, out (int X, int Z) cell)
    {
        surface = TerrainGenerator.NoWater;
        floor = 0;
        guard = 0f;
        riverLevel = WorldGenerationConfig.SeaLevel;
        riverBank = TerrainGenerator.NoWater;
        outflow = 0f;
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
            var d2c = cx * cx + cz * cz;
            var riverCull = RiverCull(nominal);
            if (d2c >= riverCull * riverCull) continue;

            var site = SiteAt(gx, gz);
            if (!site.Exists) continue;

            // River footprints never meet (a ramped site's reach plus RiverReach stays within half the
            // spacing), so at most one site writes these.
            if (site.RampLength > 0f)
            {
                var beyond = Beyond(site, x, z);
                riverLevel = RiverLevelAt(site, beyond);
                var bankZone = site.RampLength + 1f;
                if (beyond < bankZone) riverBank = RiverLevelAt(site, beyond - 1f);
                var rampGuard = 1f - MathUtils.SmoothStep(bankZone, bankZone + Guard, beyond);
                if (rampGuard > guard) guard = rampGuard;
                outflow = OutflowWeight(site, x, z);
            }

            var lakeCull = nominal * ReachFactor + Guard;
            if (d2c >= lakeCull * lakeCull) continue;

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

    // The farthest from its rolled center any output of a candidate with this nominal radius reaches: its
    // reach plus RiverReach when ramped, which a ramp's fit keeps within the spacing's half (MaxReach + Guard).
    private static float RiverCull(float nominal) => MathF.Min(nominal * ReachFactor + RiverReach, MaxReach + Guard);

    /// <summary>
    /// Whether a 4-neighbor of (x, z) holds a ramped great lake's river: a noise river
    /// (<see cref="WaterBodies.RiverWeight"/>) or the site's outflow (<see cref="OutflowWeight"/>) with any
    /// weight there. Scans the candidates the same way <see cref="Sample"/> does, restricted to ramped
    /// sites whose river footprint reaches the column. Only called on the rare bank path of
    /// <see cref="TerrainGenerator.Compute"/>, a dry column under its <c>riverBank</c>.
    /// </summary>
    internal static bool RampRiverBeside(float x, float z)
    {
        const int cellBlocks = WorldGenerationConfig.GreatLakeCellBlocks;
        TerrainSites.CellRange(x, cellBlocks, MaxReach + Guard, out var minX, out var maxX);
        TerrainSites.CellRange(z, cellBlocks, MaxReach + Guard, out var minZ, out var maxZ);
        for (var gx = minX; gx <= maxX; gx++)
        for (var gz = minZ; gz <= maxZ; gz++)
        {
            if (!TerrainSites.TryRoll(gx, gz, cellBlocks, Salt, Chance, WorldGenerationConfig.GreatLakeRadiusSkew,
                    out _, out var centerX, out var centerZ, out var size))
                continue;

            var nominal = MathUtils.Lerp(WorldGenerationConfig.GreatLakeMinRadius, WorldGenerationConfig.GreatLakeMaxRadius, size);
            var cx = x - centerX;
            var cz = z - centerZ;
            var riverCull = RiverCull(nominal);
            if (cx * cx + cz * cz >= riverCull * riverCull) continue;

            var site = SiteAt(gx, gz);
            if (!site.Exists || site.RampLength <= 0f) continue;

            if (RiverOrOutflowAt(site, x - 1f, z) || RiverOrOutflowAt(site, x + 1f, z)
                || RiverOrOutflowAt(site, x, z - 1f) || RiverOrOutflowAt(site, x, z + 1f))
                return true;
        }

        return false;
    }

    private static bool RiverOrOutflowAt(in GreatLakeSite site, float x, float z) =>
        WaterBodies.RiverWeight(x, z) > 0f || OutflowWeight(site, x, z) > 0f;

    /// <summary>
    /// Whether an accepted great lake's footprint comes within <paramref name="margin"/> of (x, z): a lobe
    /// whose center lies within its radius plus the guard (plus <see cref="RiverReach"/> instead for a
    /// ramped site, whose river ramp and outflow reach that far) plus <paramref name="margin"/>. What a pond
    /// asks so it yields to a great lake and its rivers. Each candidate is rolled and culled by distance
    /// before its cached verdict is read, and points that close to the origin are answered without a read,
    /// since no accepted footprint reaches the spawn clearing.
    /// </summary>
    internal static bool NearAnyFootprint(float x, float z, float margin)
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
            var cull = RiverCull(nominal) + margin;
            if (cx * cx + cz * cz >= cull * cull) continue;

            var site = SiteAt(gx, gz);
            if (!site.Exists) continue;

            var grow = (site.RampLength > 0f ? RiverReach : Guard) + margin;
            for (var k = 0; k < site.LobeCount; k++)
            {
                var lobe = site.Lobe(k);
                var dx = x - lobe.X;
                var dz = z - lobe.Z;
                var within = lobe.Radius + grow;
                if (dx * dx + dz * dz < within * within) return true;
            }
        }

        return false;
    }
}
