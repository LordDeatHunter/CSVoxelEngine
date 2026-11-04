using System;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The hashed peaks of the land height, summed into the forest profile after the relief layer's
/// ranges (<see cref="TerrainRelief"/>): massifs (<see cref="MassifHeight(float, float)"/>), rare
/// large mountains each rising from the relief land at its center to an absolute summit it rolled,
/// a pointed body with spurred flanks inside a wobbled outline; and lone peaks
/// (<see cref="LonePeakHeight(float, float)"/>), small steep peaks summed on top of the massifs,
/// each standing only on land that is flat without them. Candidates come from the shared
/// hashed-site rule (<see cref="TerrainSites"/>), and each cell's verdict is cached, since it is a
/// pure function of (cell, seed). Every term is exactly 0 outside an accepted site's disc and
/// within the spawn clearing (<see cref="TerrainRelief.SpawnClear"/>), and <see cref="WithMassif"/>
/// and <see cref="WithLonePeak"/> are then the exact identity, so land outside every site is
/// bit-identical to the land without this layer. A massif's acceptance reads only the relief land
/// (<see cref="TerrainGenerator.ReliefLandHeight(float, float)"/>) and the climate, a lone peak's only
/// the land without lone peaks (<see cref="TerrainGenerator.PreFeatureLandHeight(float, float)"/>),
/// the massif term and the climate, never <see cref="TerrainGenerator.LandHeight"/>, so neither
/// recurses. A site is accepted all or nothing on the pre-island ocean weight
/// (<see cref="BiomeClassifier.PreIslandOceanWeight"/>): a continental site stands wholly where it is
/// 0, so its verdict is exactly the one from before islands existed, and an island site stands
/// wholly on saturated island land, its term scaled by the island land weight
/// (<see cref="TerrainIslands.LandWeight(float, float, float)"/>), which is exactly 0 wherever the
/// pre-island ocean weight is at or below <see cref="WorldGenerationConfig.OceanShoreWeight"/>. So
/// neither term ever raises continental or shore-band land through an island site. A pure function
/// of (x, z) and the seed, safe from any worker thread.
/// </summary>
internal static class TerrainPeaks
{
    // Salts the massif cell hash ("MASS") so it never correlates with another SplitMix64.Hash user
    private const int MassifSalt = 0x4D415353;

    // One verdict per massif cell, accepted or not. A pure function of (cell, seed), so it lives
    // in the GenerationalCache like the other worldgen caches; sizing knobs in WorldGenerationConfig.
    private static readonly GenerationalCache<(int X, int Z), TerrainSite> MassifCache =
        new(WorldGenerationConfig.MassifSiteCacheCapacity, WorldGenerationConfig.MassifSiteCacheGenerations);

    // Static delegate, not a lambda at the call site: a closure would allocate on every call,
    // hits included, on the generation workers and the LOD builder.
    private static readonly Func<(int X, int Z), TerrainSite> BuildMassifSiteFor = BuildMassifSite;

    // Salts the lone-peak cell hash ("PEAK"), apart from the massifs'. Internal so a test can replay
    // a cell's raw roll and check the verdict the cache gives it.
    internal const int PeakSalt = 0x5045414B;

    // One verdict per lone-peak cell, like MassifCache. Its factory samples the pre-feature land at
    // the center and eight rim points, which is exactly the cost the cache keeps off every column.
    private static readonly GenerationalCache<(int X, int Z), TerrainSite> PeakCache =
        new(WorldGenerationConfig.PeakSiteCacheCapacity, WorldGenerationConfig.PeakSiteCacheGenerations);

    private static readonly Func<(int X, int Z), TerrainSite> BuildPeakSiteFor = BuildPeakSite;

    /// <summary>
    /// A soft ceiling for land a hashed site raised: the identity up to
    /// <see cref="WorldGenerationConfig.ReliefCeilingHeight"/> - <see cref="WorldGenerationConfig.ReliefCeilingKnee"/>,
    /// then an exponential approach to <see cref="WorldGenerationConfig.ReliefCeilingHeight"/> that never
    /// reaches it. Continuous with a continuous slope at the knee, and never decreasing.
    /// </summary>
    internal static float SoftCeiling(float height)
    {
        var knee0 = WorldGenerationConfig.ReliefCeilingHeight - WorldGenerationConfig.ReliefCeilingKnee;
        if (height <= knee0) return height;
        return WorldGenerationConfig.ReliefCeilingHeight
            - WorldGenerationConfig.ReliefCeilingKnee
            * MathF.Exp(-(height - knee0) / WorldGenerationConfig.ReliefCeilingKnee);
    }

    /// <summary>The cached verdict of massif cell (<paramref name="gx"/>, <paramref name="gz"/>):
    /// <see cref="BuildMassifSite"/>, computed once per cell.</summary>
    internal static TerrainSite MassifSiteAt(int gx, int gz) => MassifCache.GetOrAdd((gx, gz), BuildMassifSiteFor);

    /// <summary>
    /// The massif of a cell, or <c>default</c> when it has none: its roll must pass, its center lie
    /// outside the spawn clearing, it must win the lowest-hash spacing against its neighbors, its
    /// center and every rim point must lie on the forest profile of the center's kind
    /// (<see cref="IsForestProfile"/>: all continental, or all saturated island land), and its rolled
    /// summit must stand at least <see cref="WorldGenerationConfig.MassifMinRise"/> above both the
    /// relief land at its center and the highest relief land on its rim. The site's height is the rise
    /// from the center's relief land to the summit, and it is <see cref="TerrainSite.OnIsland"/> when
    /// its center is off the continent. Reads only the relief land and the climate.
    /// </summary>
    internal static TerrainSite BuildMassifSite((int X, int Z) cell)
    {
        if (!TerrainSites.TryRoll(cell.X, cell.Z, WorldGenerationConfig.MassifCellBlocks, MassifSalt,
                WorldGenerationConfig.MassifChance, WorldGenerationConfig.MassifSizeSkew,
                out var hash, out var centerX, out var centerZ, out var size))
            return default;
        if (TerrainRelief.SpawnClear(centerX, centerZ) <= 0f) return default;
        if (TerrainSites.YieldsToNeighbor(cell.X, cell.Z, hash, centerX, centerZ,
                WorldGenerationConfig.MassifMinSpacing, WorldGenerationConfig.MassifCellBlocks, MassifSalt,
                WorldGenerationConfig.MassifChance, WorldGenerationConfig.MassifSizeSkew))
            return default;

        var nominal = MathUtils.Lerp(WorldGenerationConfig.MassifMinRadius, WorldGenerationConfig.MassifMaxRadius, size);
        var continental = BiomeClassifier.PreIslandOceanWeight(centerX, centerZ) == 0f;
        if (!IsForestProfile(centerX, centerZ, continental)) return default;
        for (var k = 0; k < TerrainSites.RimPoints; k++)
        {
            TerrainSites.RimPoint(centerX, centerZ, nominal, k, out var rimX, out var rimZ);
            if (!IsForestProfile(rimX, rimZ, continental)) return default;
        }

        var summit = MathUtils.Lerp(WorldGenerationConfig.MassifMinSummit, WorldGenerationConfig.MassifMaxSummit,
            SplitMix64.Unit(hash, 4));
        var rise = summit - TerrainGenerator.ReliefLandHeight(centerX, centerZ);
        if (rise < WorldGenerationConfig.MassifMinRise) return default;

        TerrainSites.RimRange(centerX, centerZ, nominal, TerrainGenerator.ReliefHeightAt, out _, out var rimMax);
        if (rimMax > summit - WorldGenerationConfig.MassifMinRise) return default;

        return new TerrainSite(true, centerX, centerZ, nominal, rise, OnIsland: !continental);
    }

    /// <summary>
    /// Whether (x, z) lies wholly on the forest profile (no ocean and no desert weight, whose lerps
    /// would otherwise dilute a site's rise) and on the side of the island fold the site's center
    /// chose. A continental site (<paramref name="continental"/>: its center's pre-island ocean weight
    /// is 0) needs that weight to be 0 at every point too; since a 0 pre-island weight makes the
    /// island term 0 and the folded weight bit-equal to it, its verdict is exactly the one from before
    /// islands existed. An off-continent site may stand only wholly on saturated island land, where
    /// the island term has folded the ocean weight to exactly 0.
    /// </summary>
    private static bool IsForestProfile(float x, float z, bool continental)
    {
        var pre = BiomeClassifier.PreIslandOceanWeight(x, z);
        if ((pre == 0f) != continental) return false;
        BiomeClassifier.BiomeWeights(x, z, out var oceanWeight, out var desertWeight);
        return oceanWeight == 0f && desertWeight == 0f;
    }

    /// <summary>The massif term at (x, z) (<see cref="MassifHeight(float, float)"/>)
    /// without the winning cell.</summary>
    internal static float MassifHeight(float x, float z) => MassifHeight(x, z, out _);

    /// <summary>
    /// The blocks the strongest massif adds at (x, z), and its cell in <paramref name="cell"/>
    /// (<c>default</c> when none reaches): a pointed body falling from the site's rise at its center
    /// to 0 at its inward-wobbled outline, plus ridged spurs on the flanks. An island site's term is
    /// scaled by the island land weight at (x, z), so it is 0 wherever the pre-island ocean weight is
    /// at or below <see cref="WorldGenerationConfig.OceanShoreWeight"/>. Exactly 0 within the
    /// spawn clearing (checked before any other read) and outside every accepted site's disc; each
    /// candidate is culled by distance before its cached verdict is read.
    /// </summary>
    internal static float MassifHeight(float x, float z, out (int X, int Z) cell)
    {
        var enabled = WorldGenerationConfig.MassifsEnabled ? 1f : 0f;
        cell = default;
        var clear = TerrainRelief.SpawnClear(x, z);
        if (clear <= 0f) return 0f;

        const int cellBlocks = WorldGenerationConfig.MassifCellBlocks;
        TerrainSites.CellRange(x, cellBlocks, WorldGenerationConfig.MassifMaxRadius, out var minX, out var maxX);
        TerrainSites.CellRange(z, cellBlocks, WorldGenerationConfig.MassifMaxRadius, out var minZ, out var maxZ);
        var best = 0f;
        for (var gx = minX; gx <= maxX; gx++)
        for (var gz = minZ; gz <= maxZ; gz++)
        {
            if (!TerrainSites.TryRoll(gx, gz, cellBlocks, MassifSalt, WorldGenerationConfig.MassifChance,
                    WorldGenerationConfig.MassifSizeSkew, out _, out var centerX, out var centerZ, out var size))
                continue;

            var nominal = MathUtils.Lerp(WorldGenerationConfig.MassifMinRadius, WorldGenerationConfig.MassifMaxRadius, size);
            var dx = x - centerX;
            var dz = z - centerZ;
            var d2 = dx * dx + dz * dz;
            if (d2 >= nominal * nominal) continue;

            var site = MassifSiteAt(gx, gz);
            if (!site.Exists) continue;

            var radius = TerrainSites.InwardWobble(nominal, WorldGenerationConfig.MassifShapeAmplitude,
                TerrainNoise.MassifShapeNoise, x, z);
            if (d2 >= radius * radius) continue;

            var body = MathF.Pow(MathUtils.SmoothStep(1f - MathF.Sqrt(d2) / radius), WorldGenerationConfig.MassifSharpness);
            var flank = 4f * body * (1f - body);
            var spur = MathF.Pow(1f - MathF.Abs(TerrainNoise.MassifSpurNoise.GetNoise(x, z)),
                WorldGenerationConfig.MountainSharpness);
            var h = site.Height * (body + WorldGenerationConfig.MassifSpurShare * flank * spur);
            // The island land weight is exactly 0 wherever the pre-island ocean weight is at or below
            // OceanShoreWeight, so an island site never raises continental or shore-band land; it is
            // continuous, so the massif fades where the island does, and it reads no height, so it
            // never recurses. Continental sites skip these reads entirely.
            if (site.OnIsland) h *= TerrainIslands.LandWeight(x, z, BiomeClassifier.PreIslandOceanWeight(x, z));
            if (h <= best) continue;

            best = h;
            cell = (gx, gz);
        }

        return best * clear * enabled;
    }

    /// <summary>
    /// <paramref name="forest"/> with the massif at (x, z) added under the <see cref="SoftCeiling"/>,
    /// never lowered. The exact identity (the same float, sign of zero included) wherever the massif
    /// term is 0.
    /// </summary>
    internal static float WithMassif(float x, float z, float forest)
    {
        var add = MassifHeight(x, z);
        if (add <= 0f) return forest;
        return MathF.Max(forest, SoftCeiling(forest + add));
    }

    /// <summary>The cached verdict of lone-peak cell (<paramref name="gx"/>, <paramref name="gz"/>):
    /// <see cref="BuildPeakSite"/>, computed once per cell.</summary>
    internal static TerrainSite PeakSiteAt(int gx, int gz) => PeakCache.GetOrAdd((gx, gz), BuildPeakSiteFor);

    /// <summary>
    /// The lone peak of a cell, or <c>default</c> when it has none: its roll must pass, its center lie
    /// outside the spawn clearing and off every massif, it must win the lowest-hash spacing against its
    /// neighbors, its center and every rim point must lie on the forest profile of the center's kind
    /// (<see cref="IsForestProfile"/>: all continental, or all saturated island land), the land
    /// without lone peaks (<see cref="TerrainGenerator.PreFeatureLandHeight(float, float)"/>) over its
    /// center and rim must be flat within <see cref="WorldGenerationConfig.PeakSiteFlatness"/> and
    /// above the sea, and its rolled height on that center land must stay at or under the soft
    /// ceiling's knee. It is <see cref="TerrainSite.OnIsland"/> when its center is off the continent.
    /// Reads only the pre-feature land, the massif term (already scaled for an island massif) and the
    /// climate, never a lone peak, so it never recurses.
    /// </summary>
    internal static TerrainSite BuildPeakSite((int X, int Z) cell)
    {
        if (!TerrainSites.TryRoll(cell.X, cell.Z, WorldGenerationConfig.PeakCellBlocks, PeakSalt,
                WorldGenerationConfig.PeakChance, WorldGenerationConfig.PeakSizeSkew,
                out var hash, out var centerX, out var centerZ, out var size))
            return default;
        if (TerrainRelief.SpawnClear(centerX, centerZ) <= 0f) return default;
        if (TerrainSites.YieldsToNeighbor(cell.X, cell.Z, hash, centerX, centerZ,
                WorldGenerationConfig.PeakMinSpacing, WorldGenerationConfig.PeakCellBlocks, PeakSalt,
                WorldGenerationConfig.PeakChance, WorldGenerationConfig.PeakSizeSkew))
            return default;
        if (MassifHeight(centerX, centerZ) != 0f) return default;

        var nominal = MathUtils.Lerp(WorldGenerationConfig.PeakMinRadius, WorldGenerationConfig.PeakMaxRadius, size);
        var continental = BiomeClassifier.PreIslandOceanWeight(centerX, centerZ) == 0f;
        if (!IsForestProfile(centerX, centerZ, continental)) return default;
        for (var k = 0; k < TerrainSites.RimPoints; k++)
        {
            TerrainSites.RimPoint(centerX, centerZ, nominal, k, out var rimX, out var rimZ);
            if (!IsForestProfile(rimX, rimZ, continental)) return default;
        }

        TerrainSites.RimRange(centerX, centerZ, nominal, TerrainGenerator.PreFeatureHeightAt,
            out var rimMin, out var rimMax);
        if (rimMax - rimMin > WorldGenerationConfig.PeakSiteFlatness) return default;
        if (rimMin <= WorldGenerationConfig.SeaLevel) return default;

        var height = MathUtils.Lerp(WorldGenerationConfig.PeakMinHeight, WorldGenerationConfig.PeakMaxHeight,
            SplitMix64.Unit(hash, 4));
        var knee0 = WorldGenerationConfig.ReliefCeilingHeight - WorldGenerationConfig.ReliefCeilingKnee;
        if (TerrainGenerator.PreFeatureLandHeight(centerX, centerZ) + height > knee0) return default;

        return new TerrainSite(true, centerX, centerZ, nominal, height, OnIsland: !continental);
    }

    /// <summary>The lone-peak term at (x, z) (<see cref="LonePeakHeight(float, float)"/>)
    /// without the winning cell.</summary>
    internal static float LonePeakHeight(float x, float z) => LonePeakHeight(x, z, out _);

    /// <summary>
    /// The blocks the strongest lone peak adds at (x, z), and its cell in <paramref name="cell"/>
    /// (<c>default</c> when none reaches): a pointed cone falling from the site's height at its
    /// center to 0 at its inward-wobbled outline. An island site's term is scaled by the island land
    /// weight at (x, z), as in <see cref="MassifHeight(float, float)"/>. Exactly 0
    /// within the spawn clearing (checked before any other read) and outside every accepted site's
    /// disc; each candidate is culled by distance before its cached verdict is read.
    /// </summary>
    internal static float LonePeakHeight(float x, float z, out (int X, int Z) cell)
    {
        var enabled = WorldGenerationConfig.LonePeaksEnabled ? 1f : 0f;
        cell = default;
        var clear = TerrainRelief.SpawnClear(x, z);
        if (clear <= 0f) return 0f;

        const int cellBlocks = WorldGenerationConfig.PeakCellBlocks;
        TerrainSites.CellRange(x, cellBlocks, WorldGenerationConfig.PeakMaxRadius, out var minX, out var maxX);
        TerrainSites.CellRange(z, cellBlocks, WorldGenerationConfig.PeakMaxRadius, out var minZ, out var maxZ);
        var best = 0f;
        for (var gx = minX; gx <= maxX; gx++)
        for (var gz = minZ; gz <= maxZ; gz++)
        {
            if (!TerrainSites.TryRoll(gx, gz, cellBlocks, PeakSalt, WorldGenerationConfig.PeakChance,
                    WorldGenerationConfig.PeakSizeSkew, out _, out var centerX, out var centerZ, out var size))
                continue;

            var nominal = MathUtils.Lerp(WorldGenerationConfig.PeakMinRadius, WorldGenerationConfig.PeakMaxRadius, size);
            var dx = x - centerX;
            var dz = z - centerZ;
            var d2 = dx * dx + dz * dz;
            if (d2 >= nominal * nominal) continue;

            var site = PeakSiteAt(gx, gz);
            if (!site.Exists) continue;

            var radius = TerrainSites.InwardWobble(nominal, WorldGenerationConfig.PeakShapeAmplitude,
                TerrainNoise.PeakShapeNoise, x, z);
            if (d2 >= radius * radius) continue;

            var h = site.Height * MathF.Pow(MathUtils.SmoothStep(1f - MathF.Sqrt(d2) / radius),
                WorldGenerationConfig.PeakSharpness);
            // As in MassifHeight: an island site is scaled by the island land weight, exactly 0 at or
            // inside the shore band, so it never raises continental or shore-band land.
            if (site.OnIsland) h *= TerrainIslands.LandWeight(x, z, BiomeClassifier.PreIslandOceanWeight(x, z));
            if (h <= best) continue;

            best = h;
            cell = (gx, gz);
        }

        return best * clear * enabled;
    }

    /// <summary>
    /// <paramref name="forest"/> with the lone peak at (x, z) added under the <see cref="SoftCeiling"/>,
    /// never lowered. The exact identity (the same float, sign of zero included) wherever the
    /// lone-peak term is 0.
    /// </summary>
    internal static float WithLonePeak(float x, float z, float forest)
    {
        var add = LonePeakHeight(x, z);
        if (add <= 0f) return forest;
        return MathF.Max(forest, SoftCeiling(forest + add));
    }

    /// <summary>Drops every cached site verdict. Only for <see cref="WorldSeed.Apply"/>, while no
    /// worldgen thread runs: every verdict is a function of the seed.</summary>
    internal static void ResetCaches()
    {
        MassifCache.Clear();
        PeakCache.Clear();
    }
}
