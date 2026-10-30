using System;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The surface-water model of the 2D terrain half: where water stands in a column (<see cref="WaterLevelAt"/>: sea level for
/// the ocean and rivers, a lake's own rim-chosen surface for a lake, <see cref="NoWater"/> elsewhere), the hashed lake bowls
/// and warped river channels <see cref="TerrainGenerator.Compute"/> carves, and the desert gate that closes them. Lakes come
/// in two tiers, the small hashed ponds here and the rare great lakes of <see cref="GreatLakes"/>, composed in one place,
/// <see cref="SampleSurfaceWater"/>, which every reader of the water fields goes through. A pure
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
            SampleSurfaceWater(x, z, out lakeWeight, out lakeSurface, out _, out riverWeight, out _);

        return WaterLevelFor(biome, gate, riverWeight, lakeWeight, lakeSurface);
    }

    /// <summary>
    /// The lake and river fields at (x, z), with both lake tiers composed: the pond bowl of
    /// <see cref="LakeSample"/>, the great lake of <see cref="GreatLakes.Sample(float, float, out int, out int, out float)"/>
    /// and the river channel of <see cref="RiverWeight"/>. Ponds never reach a great lake
    /// (<see cref="TryLakeSite"/> refuses a pond near one), so wherever the great lake has weight it
    /// is the only lake and its surface and floor replace the pond's; the river yields to it,
    /// scaled by <see cref="GreatLakes.RiverShare"/>, which is 0 wherever lake water stands, so a
    /// channel ends at the shore without a step. Wherever the great-lake weight is 0, every out
    /// equals the pond and river values exactly. <paramref name="lakeGuard"/> is the great lake's
    /// ravine guard (1 inside an outline, easing to 0 across the guard band). The biome is not
    /// tested here: callers gate on <see cref="CanHoldSurfaceWater"/> as before.
    /// </summary>
    internal static void SampleSurfaceWater(float x, float z, out float lakeWeight, out int lakeSurface,
        out int lakeFloor, out float riverWeight, out float lakeGuard)
    {
        lakeWeight = LakeSample(x, z, out lakeSurface, out lakeFloor);
        riverWeight = RiverWeight(x, z);
        var great = GreatLakes.Sample(x, z, out var greatSurface, out var greatFloor, out lakeGuard);
        if (great <= 0f) return;

        lakeWeight = great;
        lakeSurface = greatSurface;
        lakeFloor = greatFloor;
        riverWeight *= GreatLakes.RiverShare(great);
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
    /// Candidates near the origin are skipped so the spawn is always on dry land. This is the pond
    /// tier only: a candidate near a great lake yields to it (<see cref="TryLakeSite"/>), and
    /// <see cref="SampleSurfaceWater"/> composes the two tiers.
    /// </summary>
    internal static float LakeSample(float x, float z, out int surface, out int floor)
    {
        const int cell = WorldGenerationConfig.LakeCellBlocks;
        TerrainSites.CellRange(x, cell, WorldGenerationConfig.LakeMaxRadius, out var gxMin, out var gxMax);
        TerrainSites.CellRange(z, cell, WorldGenerationConfig.LakeMaxRadius, out var gzMin, out var gzMax);
        var best = 0f;
        surface = NoWater;
        floor = 0;

        for (var gx = gxMin; gx <= gxMax; gx++)
        for (var gz = gzMin; gz <= gzMax; gz++)
        {
            if (!TerrainSites.TryRoll(gx, gz, cell, LakeSalt, WorldGenerationConfig.LakeChance,
                    WorldGenerationConfig.LakeRadiusSkew, out _, out var centerX, out var centerZ, out var size))
                continue;
            if (MathF.Abs(centerX) < WorldGenerationConfig.LakeSpawnClearRadius
                && MathF.Abs(centerZ) < WorldGenerationConfig.LakeSpawnClearRadius)
                continue;

            var nominal = MathUtils.Lerp(WorldGenerationConfig.LakeMinRadius,
                WorldGenerationConfig.LakeMaxRadius, size);
            var ox = x - centerX;
            var oz = z - centerZ;
            var d2 = ox * ox + oz * oz;

            // A column past `nominal` is out (this cull clips any outward wobble there) without
            // paying for the rim samples that choosing a water level costs.
            if (d2 >= nominal * nominal) continue;
            if (!TryLakeSite(centerX, centerZ, nominal, size, out var siteSurface, out var siteFloor))
                continue;

            var radius = TerrainSites.Wobble(nominal, WorldGenerationConfig.LakeShapeAmplitude,
                TerrainNoise.LakeShapeNoise, x, z);
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
    /// A candidate within <see cref="WorldGenerationConfig.GreatLakePondClearance"/> of an accepted
    /// great lake's guarded outline (<see cref="GreatLakes.NearAnyLobe"/>) is dropped first, so the
    /// two lake tiers never share a column and never split a surface.
    /// </summary>
    private static bool TryLakeSite(float centerX, float centerZ, float radius, float size,
        out int surface, out int floor)
    {
        // A pond yields to a great lake, so no column belongs to both and no surface is split.
        // Tested first, which also skips the rim samples near a lake.
        if (GreatLakes.NearAnyLobe(centerX, centerZ, radius + WorldGenerationConfig.GreatLakePondClearance))
        {
            surface = 0;
            floor = 0;
            return false;
        }

        TerrainSites.RimRange(centerX, centerZ, radius, TerrainGenerator.LandHeightAt, out var rimMin, out var rimMax);

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
    internal const float RiverSampleSpacing = 6f;

    /// <summary>Whether a river channel has any weight at (x, z), as a static delegate for the ring samplers.</summary>
    private static readonly Func<float, float, bool> RiverAt = static (x, z) => RiverWeight(x, z) > 0f;

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
            if (TerrainSites.AnyOnRing(centerX, centerZ, radius * ring / rings, RiverSampleSpacing, RiverAt))
                return true;

        return false;
    }

    /// <summary>
    /// How much of its channel a river cuts over land of this forest-profile height: 1 in the lowlands,
    /// where it cuts its full channel, easing to 0 above <see cref="WorldGenerationConfig.RiverLowEnd"/>,
    /// where it is a dry wash.
    /// </summary>
    internal static float RiverLowland(float forestHeight) =>
        1f - MathUtils.SmoothStep(WorldGenerationConfig.RiverLowStart, WorldGenerationConfig.RiverLowEnd, forestHeight);

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
    /// 0..1 how strongly a lake (either tier) or river covers (x, z): the max of the two masks of
    /// <see cref="SampleSurfaceWater"/>, gated toward a
    /// desert the same way the carve is. The forest uses it to mark shores, so sand and gravel
    /// appear only where a water body actually is.
    /// </summary>
    public static float SurfaceWaterWeight(float x, float z)
    {
        BiomeClassifier.BiomeWeights(x, z, out _, out var desertWeight);
        SampleSurfaceWater(x, z, out var lake, out _, out _, out var river, out _);
        return MathF.Max(lake, river) * SurfaceWaterGate(desertWeight);
    }
}
