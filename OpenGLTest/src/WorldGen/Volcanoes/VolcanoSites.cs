using System;

namespace VoxelEngine.WorldGen.Volcanoes;

/// <summary>Cached, pure Dynamic volcano verdicts. A factory reads only the Q0 relief,
/// biome/province fields and massif sites; it never reads final columns or later sites.</summary>
internal static class VolcanoSites
{
    internal const int Salt = 0x564F4C43; // VOLC
    private static readonly GenerationalCache<(int X, int Z), VolcanoSite> Sites =
        new(WorldGenerationConfig.VolcanoSiteCacheCapacity, WorldGenerationConfig.VolcanoSiteCacheGenerations);
    private static readonly Func<(int X, int Z), VolcanoSite> BuildFor = static key => Build(key);

    internal static void ResetCache() => Sites.Clear();

    internal static VolcanoSite SiteAt(int gx, int gz) => WorldSeed.Profile == WorldGenProfile.Dynamic
        ? Sites.GetOrAdd((gx, gz), BuildFor) : default;

    internal static VolcanoSite Build((int X, int Z) cell)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic
            || !TerrainSites.TryRoll(cell.X, cell.Z, WorldGenerationConfig.VolcanoCellBlocks, Salt,
                WorldGenerationConfig.VolcanoChance, WorldGenerationConfig.VolcanoSizeSkew,
                out var hash, out var x, out var z, out var size)) return default;

        var radius = MathUtils.Lerp(WorldGenerationConfig.VolcanoMinRadius,
            WorldGenerationConfig.VolcanoMaxRadius, size);
        if (MathF.Sqrt(x * x + z * z) < WorldGenerationConfig.DynamicReliefSpawnClearEnd + radius)
            return default;
        if (TerrainSites.YieldsToNeighbor(cell.X, cell.Z, hash, x, z,
                WorldGenerationConfig.VolcanoMinSpacing, WorldGenerationConfig.VolcanoCellBlocks,
                Salt, WorldGenerationConfig.VolcanoChance, WorldGenerationConfig.VolcanoSizeSkew))
            return default;

        var crater = MathUtils.Lerp(WorldGenerationConfig.VolcanoMinCraterRadius,
            WorldGenerationConfig.VolcanoMaxCraterRadius, SplitMix64.Unit(hash, 5));
        var summit = MathUtils.Lerp(WorldGenerationConfig.VolcanoMinSummit,
            WorldGenerationConfig.VolcanoMaxSummit, SplitMix64.Unit(hash, 4));
        var floor = (int)summit - WorldGenerationConfig.VolcanoCraterDrop;
        if (!EligibleSample(x, z, floor)) return default;
        for (var k = 0; k < TerrainSites.RimPoints; k++)
        {
            TerrainSites.RimPoint(x, z, radius, k, out var rimX, out var rimZ);
            if (!EligibleSample(rimX, rimZ, floor)) return default;
        }

        // The nine outer points establish the intended setting, not a whole-disc theorem.
        // The caldera is the owned volume: inspect every integer column of its guard disc.
        // This is deliberately a cold cache-factory cost: the fractional center and
        // inclusive floor/ceil loop need at most 118^2 bounding-square visits.
        var guard = crater + WorldGenerationConfig.VolcanoGuard;
        for (var ix = (int)MathF.Floor(x - guard); ix <= (int)MathF.Ceiling(x + guard); ix++)
        for (var iz = (int)MathF.Floor(z - guard); iz <= (int)MathF.Ceiling(z + guard); iz++)
        {
            var dx = ix - x;
            var dz = iz - z;
            if (dx * dx + dz * dz >= guard * guard) continue;
            var biome = BiomeClassifier.BiomeWeights(ix, iz, out var ocean, out var desert);
            if (ocean != 0f || desert != 0f
                || BiomeClassifier.VolcanicProvince(ix, iz, ocean)
                    < WorldGenerationConfig.VolcanoSiteProvinceMin
                || BiomeClassifier.DitherBiome(ix, iz, biome, ocean, desert) != Biome.Charred)
                return default;
        }

        TerrainSites.CellRange(x, WorldGenerationConfig.MassifCellBlocks,
            radius + WorldGenerationConfig.MassifMaxRadius + 8f, out var minX, out var maxX);
        TerrainSites.CellRange(z, WorldGenerationConfig.MassifCellBlocks,
            radius + WorldGenerationConfig.MassifMaxRadius + 8f, out var minZ, out var maxZ);
        for (var mx = minX; mx <= maxX; mx++)
        for (var mz = minZ; mz <= maxZ; mz++)
        {
            var massif = TerrainPeaks.MassifSiteAt(mx, mz);
            if (!massif.Exists) continue;
            var dx = x - massif.CenterX;
            var dz = z - massif.CenterZ;
            var apart = radius + massif.Radius + 8f;
            if (dx * dx + dz * dz < apart * apart) return default;
        }

        var rise = summit - TerrainGenerator.ReliefLandHeight(x, z);
        if (rise <= 0f) return default;
        return new VolcanoSite(true, x, z, radius, crater, floor,
            WorldGenerationConfig.VolcanoLakeDepth, rise);
    }

    private static bool EligibleSample(float x, float z, int floor)
    {
        if (BiomeClassifier.PreIslandOceanWeight(x, z) != 0f) return false;
        BiomeClassifier.BiomeWeights(x, z, out var ocean, out var desert);
        return ocean == 0f && desert == 0f
            && BiomeClassifier.VolcanicProvince(x, z, ocean) >= WorldGenerationConfig.VolcanoSiteProvinceMin
            && TerrainGenerator.ReliefLandHeight(x, z) <= floor - 4f;
    }

    /// <summary>The accepted cone reaching a point, or default. Candidate rolls are distance-
    /// culled before the cached verdict is requested.</summary>
    internal static VolcanoSite At(float x, float z)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic) return default;
        TerrainSites.CellRange(x, WorldGenerationConfig.VolcanoCellBlocks,
            WorldGenerationConfig.VolcanoMaxRadius, out var minX, out var maxX);
        TerrainSites.CellRange(z, WorldGenerationConfig.VolcanoCellBlocks,
            WorldGenerationConfig.VolcanoMaxRadius, out var minZ, out var maxZ);
        for (var gx = minX; gx <= maxX; gx++)
        for (var gz = minZ; gz <= maxZ; gz++)
        {
            if (!TerrainSites.TryRoll(gx, gz, WorldGenerationConfig.VolcanoCellBlocks, Salt,
                    WorldGenerationConfig.VolcanoChance, WorldGenerationConfig.VolcanoSizeSkew,
                    out _, out var centerX, out var centerZ, out var size)) continue;
            var radius = MathUtils.Lerp(WorldGenerationConfig.VolcanoMinRadius,
                WorldGenerationConfig.VolcanoMaxRadius, size);
            var dx = x - centerX;
            var dz = z - centerZ;
            if (dx * dx + dz * dz >= radius * radius) continue;
            var site = SiteAt(gx, gz);
            if (site.Exists) return site;
        }
        return default;
    }

    internal static bool InGuard(float x, float z) => VolcanoShape.InGuard(At(x, z), x, z);

    /// <summary>Conservative footprint rejection for surface features. It leaves the cone
    /// flanks available and rejects any writer whose box touches the owned caldera guard.</summary>
    internal static bool IntersectsGuard(int minX, int minZ, int maxX, int maxZ)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic) return false;
        TerrainSites.CellRange(minX, WorldGenerationConfig.VolcanoCellBlocks,
            WorldGenerationConfig.VolcanoMaxRadius, out var gx0, out _);
        TerrainSites.CellRange(maxX, WorldGenerationConfig.VolcanoCellBlocks,
            WorldGenerationConfig.VolcanoMaxRadius, out _, out var gx1);
        TerrainSites.CellRange(minZ, WorldGenerationConfig.VolcanoCellBlocks,
            WorldGenerationConfig.VolcanoMaxRadius, out var gz0, out _);
        TerrainSites.CellRange(maxZ, WorldGenerationConfig.VolcanoCellBlocks,
            WorldGenerationConfig.VolcanoMaxRadius, out _, out var gz1);
        for (var gx = gx0; gx <= gx1; gx++)
        for (var gz = gz0; gz <= gz1; gz++)
        {
            if (!TerrainSites.TryRoll(gx, gz, WorldGenerationConfig.VolcanoCellBlocks, Salt,
                    WorldGenerationConfig.VolcanoChance, WorldGenerationConfig.VolcanoSizeSkew,
                    out _, out var centerX, out var centerZ, out _)) continue;
            var cx = MathF.Max(minX - centerX, MathF.Max(0f, centerX - maxX));
            var cz = MathF.Max(minZ - centerZ, MathF.Max(0f, centerZ - maxZ));
            var maximum = WorldGenerationConfig.VolcanoMaxCraterRadius + WorldGenerationConfig.VolcanoGuard;
            if (cx * cx + cz * cz >= maximum * maximum) continue;
            var site = SiteAt(gx, gz);
            if (!site.Exists) continue;
            var dx = MathF.Max(minX - site.X, MathF.Max(0f, site.X - maxX));
            var dz = MathF.Max(minZ - site.Z, MathF.Max(0f, site.Z - maxZ));
            var guard = site.CraterRadius + WorldGenerationConfig.VolcanoGuard;
            if (dx * dx + dz * dz < guard * guard) return true;
        }
        return false;
    }

    internal static bool IntersectsGuardDisc(int x, int z, int radius)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic) return false;
        TerrainSites.CellRange(x, WorldGenerationConfig.VolcanoCellBlocks,
            WorldGenerationConfig.VolcanoMaxRadius + radius, out var gx0, out var gx1);
        TerrainSites.CellRange(z, WorldGenerationConfig.VolcanoCellBlocks,
            WorldGenerationConfig.VolcanoMaxRadius + radius, out var gz0, out var gz1);
        for (var gx = gx0; gx <= gx1; gx++)
        for (var gz = gz0; gz <= gz1; gz++)
        {
            if (!CandidateNearDisc(gx, gz, x, z,
                    radius + WorldGenerationConfig.VolcanoMaxCraterRadius + WorldGenerationConfig.VolcanoGuard))
                continue;
            var site = SiteAt(gx, gz);
            if (!site.Exists) continue;
            var dx = site.X - x;
            var dz = site.Z - z;
            var apart = radius + site.CraterRadius + WorldGenerationConfig.VolcanoGuard;
            if (dx * dx + dz * dz < apart * apart) return true;
        }
        return false;
    }

    internal static bool IntersectsConeDisc(float x, float z, float radius)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic) return false;
        TerrainSites.CellRange(x, WorldGenerationConfig.VolcanoCellBlocks,
            WorldGenerationConfig.VolcanoMaxRadius + radius, out var gx0, out var gx1);
        TerrainSites.CellRange(z, WorldGenerationConfig.VolcanoCellBlocks,
            WorldGenerationConfig.VolcanoMaxRadius + radius, out var gz0, out var gz1);
        for (var gx = gx0; gx <= gx1; gx++)
        for (var gz = gz0; gz <= gz1; gz++)
        {
            if (!CandidateNearDisc(gx, gz, x, z, radius + WorldGenerationConfig.VolcanoMaxRadius))
                continue;
            var site = SiteAt(gx, gz);
            if (!site.Exists) continue;
            var dx = site.X - x;
            var dz = site.Z - z;
            var apart = radius + site.Radius;
            if (dx * dx + dz * dz < apart * apart) return true;
        }
        return false;
    }

    private static bool CandidateNearDisc(int gx, int gz, float x, float z, float reach)
    {
        if (!TerrainSites.TryRoll(gx, gz, WorldGenerationConfig.VolcanoCellBlocks, Salt,
                WorldGenerationConfig.VolcanoChance, WorldGenerationConfig.VolcanoSizeSkew,
                out _, out var centerX, out var centerZ, out _)) return false;
        var dx = centerX - x;
        var dz = centerZ - z;
        return dx * dx + dz * dz < reach * reach;
    }

    internal static bool IntersectsOwnedBox(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
    {
        if (!IntersectsGuard(minX, minZ, maxX, maxZ)) return false;
        // The S5a owned band is never below the lowest possible floor and never above the
        // highest; the XZ guard is conservative. S5b will extend the vertical predicate.
        var lowest = (int)WorldGenerationConfig.VolcanoMinSummit - WorldGenerationConfig.VolcanoCraterDrop
            - WorldGenerationConfig.VolcanoLakeDepth - WorldGenerationConfig.VolcanoLiningThickness;
        var highest = (int)WorldGenerationConfig.VolcanoMaxSummit - WorldGenerationConfig.VolcanoCraterDrop;
        return maxY >= lowest && minY <= highest;
    }
}
