using System;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The surface-water model of the 2D terrain half: where water stands in a column (<see cref="WaterLevelAt"/>: sea level for
/// the ocean and rivers, a lake's own rim-chosen surface for a lake, <see cref="NoWater"/> elsewhere), the hashed lake bowls
/// and warped river channels <see cref="TerrainGenerator.Compute"/> carves, and the desert gate that closes them. A pure
/// function of (x, z) and the seed, read on any worker thread; <see cref="TerrainGenerator"/> forwards its public queries here.
/// </summary>
internal static class WaterBodies
{
    /// <summary>Water level of a column that holds no water at all.</summary>
    public const int NoWater = int.MinValue;

    /// <summary>
    /// The y water in column (x, z) rises to: <see cref="WorldGenerationConfig.SeaLevel"/> for the
    /// ocean and for rivers, the lake's own surface for a lake, and <see cref="NoWater"/> where the
    /// column holds none. This is the one definition of where water sits, and it is what lets a pond
    /// rest on the ground it was dug into instead of on one global sea level.
    ///
    /// Callers measuring a column's own terrain must pass <see cref="ColumnData.Bed"/>, not
    /// <see cref="ColumnData.Heights"/>: the bed is the profile before a ravine cut it, so a ravine
    /// that merely deepens an otherwise-dry column stays dry instead of flooding as a narrow spike,
    /// while a ravine breaching a lake or river is prevented outright by <see cref="TerrainGenerator.RavineCutFade"/>.
    /// </summary>
    public static int WaterLevelAt(float x, float z)
    {
        var biome = BiomeClassifier.BiomeWeights(x, z, out _, out var desertWeight);
        var gate = SurfaceWaterGate(desertWeight);
        var lakeWeight = 0f;
        var lakeSurface = NoWater;
        var riverWeight = 0f;
        if (CanHoldSurfaceWater(biome))
        {
            lakeWeight = LakeSample(x, z, out lakeSurface, out _);
            riverWeight = RiverWeight(x, z);
        }

        return WaterLevelFor(biome, gate, riverWeight, lakeWeight, lakeSurface);
    }

    /// <summary>
    /// Whether a biome is one a lake or a river may stand in: every land biome but the desert
    /// (forest, taiga, frozen, steppe and savanna). The ocean is excluded too, because its water is
    /// the sea itself, which <see cref="WaterLevelFor"/> answers before it asks this. A dry steppe or
    /// savanna still closes its bodies toward a desert through <see cref="SurfaceWaterGate"/>.
    /// </summary>
    internal static bool CanHoldSurfaceWater(Biome biome) =>
        WorldGenerationConfig.LakesAndRiversEnabled && biome is not (Biome.Ocean or Biome.Desert);

    /// <summary>
    /// The level a column with these surface-water weights floods to. Shared by
    /// <see cref="WaterLevelAt"/> and by <see cref="TerrainGenerator.Compute"/>, which evaluates the same weights
    /// itself and stores the result per column, so a stored level is by construction the level the
    /// carve that made the bowl was working toward.
    /// </summary>
    internal static int WaterLevelFor(Biome biome, float gate, float riverWeight, float lakeWeight,
        int lakeSurface)
    {
        // The master water toggle is a compile-time const, so it is folded into these runtime
        // tests: a branch of its own would read as unreachable code (see BiomeWeights).
        if (biome == Biome.Ocean)
            return WorldGenerationConfig.WaterEnabled ? WorldGenerationConfig.SeaLevel : NoWater;
        if (!WorldGenerationConfig.WaterEnabled || !CanHoldSurfaceWater(biome)) return NoWater;

        var level = NoWater;
        if (riverWeight * gate > WorldGenerationConfig.WaterBodyThreshold)
            level = WorldGenerationConfig.SeaLevel;
        if (lakeWeight * gate > WorldGenerationConfig.WaterBodyThreshold && lakeSurface > level)
            level = lakeSurface;
        return level;
    }

    /// <summary>Whether the column (x, z) at this height is wet: it sits below the level
    /// <see cref="WaterLevelAt"/> reports. A hot desert stays dry, and a cold lake or river freezes
    /// over instead (see the freezing pass), both because their level is <see cref="NoWater"/>.</summary>
    public static bool IsWaterColumn(int x, int z, int height) => height < WaterLevelAt(x, z);

    /// <summary>
    /// 0..1 multiplicative gate on a surface water body at (x, z): 1 away from a desert, fading to
    /// 0 as the column reaches the desert line (desert weight 0.5). Both the carve in <see cref="TerrainGenerator.Compute"/> and
    /// the flood test in <see cref="IsWaterColumn"/> read it, so a lake or river tapers out and
    /// closes on steppe or savanna ground just before the desert instead of being cut off in a wall.
    /// The fade only covers the last stretch before the line, so lakes elsewhere keep their depth.
    /// </summary>
    internal static float SurfaceWaterGate(float desertWeight) =>
        1f - MathUtils.SmoothStep(WorldGenerationConfig.WaterDesertFadeStart,
            WorldGenerationConfig.WaterDesertFadeEnd, desertWeight);

    private const int LakeSalt = 0x4C414B45;

    /// <summary>
    /// 0..1 lake bowl at (x, z), plus the water level and floor of the lake that reaches the column.
    /// One hashed candidate per <see cref="WorldGenerationConfig.LakeCellBlocks"/> cell, offset inside
    /// the cell and sized by a skewed roll, so the world is mostly little ponds with the occasional
    /// broad lake rather than one uniform size. The bowl falls from 1 at the center to 0 at the rim,
    /// so the carve blends back into the ground it was dug from, and the shoreline is the candidate's
    /// radius wobbled by <see cref="TerrainNoise.LakeShapeNoise"/> so it is ragged rather than a circle.
    /// Candidates near the origin are skipped so the spawn is always on dry land.
    /// </summary>
    internal static float LakeSample(float x, float z, out int surface, out int floor)
    {
        const int cell = WorldGenerationConfig.LakeCellBlocks;
        var gx0 = MathUtils.FloorToInt(x / cell);
        var gz0 = MathUtils.FloorToInt(z / cell);
        var best = 0f;
        surface = NoWater;
        floor = 0;

        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            var gx = gx0 + dx;
            var gz = gz0 + dz;
            var h = SplitMix64.Hash(gx, gz, WorldGenerationConfig.Seed ^ LakeSalt);
            if (Unit(h, 0) >= WorldGenerationConfig.LakeChance) continue;

            var centerX = gx * cell + Unit(h, 1) * cell;
            var centerZ = gz * cell + Unit(h, 2) * cell;
            if (MathF.Abs(centerX) < WorldGenerationConfig.LakeSpawnClearRadius
                && MathF.Abs(centerZ) < WorldGenerationConfig.LakeSpawnClearRadius)
                continue;

            var size = MathF.Pow(Unit(h, 3), WorldGenerationConfig.LakeRadiusSkew);
            var nominal = MathUtils.Lerp(WorldGenerationConfig.LakeMinRadius,
                WorldGenerationConfig.LakeMaxRadius, size);
            var ox = x - centerX;
            var oz = z - centerZ;
            var d2 = ox * ox + oz * oz;

            // The wobble below only ever pulls the shoreline inside `nominal`, so a column past it
            // is out without paying for the rim samples that choosing a water level costs.
            if (d2 >= nominal * nominal) continue;
            if (!TryLakeSite(centerX, centerZ, nominal, size, out var siteSurface, out var siteFloor))
                continue;

            var radius = nominal * (1f + WorldGenerationConfig.LakeShapeAmplitude
                * TerrainNoise.LakeShapeNoise.GetNoise(x, z));
            if (d2 >= radius * radius) continue;

            var w = MathUtils.SmoothStep(1f - MathF.Sqrt(d2) / radius);
            if (w > best)
            {
                best = w;
                surface = siteSurface;
                floor = siteFloor;
            }
        }

        return best;
    }

    /// <summary>
    /// Where a lake candidate of this radius puts its water and its floor, or false when the ground
    /// it would stand on is too uneven to hold a pond. Sampling the rim is what keeps the water
    /// enclosed: the surface sits <see cref="WorldGenerationConfig.LakeBankDepth"/> under the lowest
    /// ground on the rim, so every bank is above the water, and the floor is a drop below that which
    /// grows with the lake. A rim that climbs more than
    /// <see cref="WorldGenerationConfig.LakeSiteFlatness"/> is a hillside, and a pond there would
    /// tower over its downhill side, so the candidate is dropped and the terrain keeps the slope.
    /// The rim is read from <see cref="TerrainGenerator.LandHeight"/>, the profile before a river cuts it, so a pond
    /// standing above the waterline is also dropped when a river channel crosses its footprint
    /// (<see cref="RiverCrossesSite"/>): the channel would cut a bank below the pond's surface.
    /// </summary>
    private static bool TryLakeSite(float centerX, float centerZ, float radius, float size,
        out int surface, out int floor)
    {
        var rimMin = TerrainGenerator.LandHeight(centerX, centerZ, out _, out _, out _, out _);
        var rimMax = rimMin;
        for (var k = 0; k < 8; k++)
        {
            var angle = k * (MathF.Tau / 8f);
            var land = TerrainGenerator.LandHeight(centerX + MathF.Cos(angle) * radius,
                centerZ + MathF.Sin(angle) * radius, out _, out _, out _, out _);
            if (land < rimMin) rimMin = land;
            if (land > rimMax) rimMax = land;
        }

        surface = 0;
        floor = 0;
        if (rimMax - rimMin > WorldGenerationConfig.LakeSiteFlatness) return false;

        var depth = MathUtils.Lerp(WorldGenerationConfig.LakeMinDepth,
            WorldGenerationConfig.LakeMaxDepth, size * size);
        surface = (int)MathF.Round(rimMin - WorldGenerationConfig.LakeBankDepth);
        floor = (int)MathF.Round(surface - depth);
        // A pond at or below the river's own waterline cannot spill into the channel, so only an
        // elevated one pays for the river samples.
        if (surface > WorldGenerationConfig.SeaLevel && RiverCrossesSite(centerX, centerZ, radius))
            return false;
        return true;
    }

    /// <summary>Blocks between river samples across a lake candidate's footprint, ring to ring and along a ring.</summary>
    private const float RiverSampleSpacing = 6f;

    /// <summary>
    /// Whether a river channel reaches a lake candidate's footprint. The disc is sampled with
    /// concentric rings every <see cref="RiverSampleSpacing"/> blocks of radius (the rim ring
    /// included), each ring holding points no more than that far apart, so neighboring samples sit
    /// closer together than a river channel is wide and a channel cannot slip between them,
    /// whatever the lake's size.
    /// </summary>
    private static bool RiverCrossesSite(float centerX, float centerZ, float radius)
    {
        if (RiverWeight(centerX, centerZ) > 0f) return true;
        var rings = Math.Max(1, (int)MathF.Ceiling(radius / RiverSampleSpacing));
        for (var ring = 1; ring <= rings; ring++)
        {
            var r = radius * ring / rings;
            var points = Math.Max(1, (int)MathF.Ceiling(MathF.Tau * r / RiverSampleSpacing));
            for (var k = 0; k < points; k++)
            {
                var angle = k * (MathF.Tau / points);
                if (RiverWeight(centerX + MathF.Cos(angle) * r, centerZ + MathF.Sin(angle) * r) > 0f)
                    return true;
            }
        }

        return false;
    }

    /// <summary>One 0..1 hash value for a candidate, re-mixed by <paramref name="index"/>.</summary>
    private static float Unit(ulong h, int index) =>
        (SplitMix64.Mix(h + (ulong)index * 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));

    /// <summary>0..1 river weight: a warped ridge band (|noise| near zero) gated by a rarity mask.</summary>
    internal static float RiverWeight(float x, float z)
    {
        var mask = MathUtils.SmoothStep(WorldGenerationConfig.RiverMaskStart,
            WorldGenerationConfig.RiverMaskEnd, TerrainNoise.RiverMask.GetNoise(x, z));
        if (mask <= 0f) return 0f;

        var warpedX = x;
        var warpedZ = z;
        TerrainNoise.RiverWarp.DomainWarp(ref warpedX, ref warpedZ);

        var ridge = MathF.Abs(TerrainNoise.RiverRidge.GetNoise(warpedX, warpedZ));
        if (ridge >= WorldGenerationConfig.RiverWidth) return 0f;

        var t = ridge / WorldGenerationConfig.RiverWidth;
        return mask * (1f - t * t);
    }

    /// <summary>
    /// 0..1 how strongly a lake or river covers (x, z): the max of the two masks, gated toward a
    /// desert the same way the carve is. The forest uses it to mark shores, so sand and gravel
    /// appear only where a water body actually is.
    /// </summary>
    public static float SurfaceWaterWeight(float x, float z)
    {
        BiomeClassifier.BiomeWeights(x, z, out _, out var desertWeight);
        return MathF.Max(LakeSample(x, z, out _, out _), RiverWeight(x, z))
               * SurfaceWaterGate(desertWeight);
    }
}
