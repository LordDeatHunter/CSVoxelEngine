using System;

namespace silkgltriangle;

/// <summary>
/// The 2D half of world generation: terrain heights with ravines cut in, dirt depth and the
/// per-column cave inputs (entrance zones, cave intensity). Everything is a pure function of
/// (x, z) and the seed, so results are cached per chunk column by <see cref="Chunk.GetColumnData"/>
/// and shared by the vertical chunk stack and the far LOD. Noise instances are configured once
/// here and only read afterwards, which keeps this safe to call from any worker thread.
/// </summary>
public static class TerrainGenerator
{
    private static readonly FastNoiseLite ForestSwellNoise;
    private static readonly FastNoiseLite HillNoise;
    private static readonly FastNoiseLite HillMaskNoise;
    private static readonly FastNoiseLite ValleyRoughNoise;
    private static readonly FastNoiseLite HillWarp;
    private static readonly FastNoiseLite DirtNoise;
    private static readonly FastNoiseLite RavineRidge;
    private static readonly FastNoiseLite RavineWarp;
    private static readonly FastNoiseLite RavineMask;
    private static readonly FastNoiseLite EntranceNoise;
    private static readonly FastNoiseLite IntensityNoise;
    private static readonly FastNoiseLite GravelNoise;
    private static readonly FastNoiseLite ContinentNoise;
    private static readonly FastNoiseLite TemperatureNoise;
    private static readonly FastNoiseLite HumidityNoise;
    private static readonly FastNoiseLite DuneNoise;
    private static readonly FastNoiseLite TrenchMask;
    private static readonly FastNoiseLite TrenchRidge;
    private static readonly FastNoiseLite TrenchWarp;
    private static readonly FastNoiseLite RiverMask;
    private static readonly FastNoiseLite RiverRidge;
    private static readonly FastNoiseLite RiverWarp;
    private static readonly FastNoiseLite MountainMask;
    private static readonly FastNoiseLite MountainNoise;
    private static readonly FastNoiseLite MountainWarp;
    private static readonly FastNoiseLite OpennessNoise;
    private static readonly FastNoiseLite BiomeDitherNoise;
    private static readonly FastNoiseLite LakeShapeNoise;

    // The terrain palette: the generator owns the choice of which blocks natural ground is made
    // of, and Chunk only fills its columns with them. Resolved once here so the per-voxel fill
    // and the far LOD both read a byte instead of hitting the registry.

    /// <summary>Block the top of a dry column is made of.</summary>
    public static readonly byte SurfaceBlock;

    /// <summary>Block filling the soil band under the surface (and the top of a sea floor).</summary>
    public static readonly byte SoilBlock;

    /// <summary>Block everything below the soil band is made of.</summary>
    public static readonly byte RockBlock;

    /// <summary>Fluid the sea is filled with, up to and including sea level.</summary>
    public static readonly byte SeaBlock;

    /// <summary>Block dirt touching fresh sea water rots into on the spot.</summary>
    public static readonly byte MudBlock;

    /// <summary>Block beach columns (see <see cref="IsBeachHeight"/>) are built from.</summary>
    public static readonly byte SandBlock;

    /// <summary>Block capping beach columns where <see cref="IsGravelPatch"/> holds.</summary>
    public static readonly byte GravelBlock;

    /// <summary>Block a desert column's soil band is made of, under its surface sand.</summary>
    public static readonly byte SandstoneBlock;

    /// <summary>Frozen water surface and the frozen biome's ice; the ice-sheet block.</summary>
    public static readonly byte IceBlock;

    /// <summary>Layered snow block; the snow accumulation pass stamps it.</summary>
    public static readonly byte SnowBlock;

    static TerrainGenerator()
    {
        SurfaceBlock = ContentRegistry.RequireBlock("grass").Id;
        SoilBlock = ContentRegistry.RequireBlock("dirt").Id;
        RockBlock = ContentRegistry.RequireBlock("stone").Id;
        SeaBlock = ContentRegistry.RequireBlock("water").Id;
        MudBlock = ContentRegistry.RequireBlock("mud").Id;
        SandBlock = ContentRegistry.RequireBlock("sand").Id;
        GravelBlock = ContentRegistry.RequireBlock("gravel").Id;
        SandstoneBlock = ContentRegistry.RequireBlock("sandstone").Id;
        IceBlock = ContentRegistry.RequireBlock("ice").Id;
        SnowBlock = ContentRegistry.RequireBlock("snow").Id;

        // The rolling base keeps the original seed and settings exactly, so natural terrain
        // is unchanged wherever the flat mask stays at zero. The mask is rare and
        // low-frequency on purpose: most of the world keeps its old look, and only large
        // patches go flat, with a wide soft blend between.
        ForestSwellNoise = MakeNoise(52, WorldGenerationConfig.ForestSwellFrequency, octaves: 2);
        HillNoise = MakeNoise(54, WorldGenerationConfig.HillFrequency, octaves: 3);
        HillMaskNoise = MakeNoise(55, WorldGenerationConfig.HillMaskFrequency, octaves: 2);
        ValleyRoughNoise = MakeNoise(56, WorldGenerationConfig.ValleyRoughFrequency);

        // Winding hill outlines, the same domain-warp trick the ravines and rivers use
        HillWarp = new FastNoiseLite(WorldGenerationConfig.Seed + 57);
        HillWarp.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
        HillWarp.SetFrequency(WorldGenerationConfig.HillWarpFrequency);
        HillWarp.SetDomainWarpAmp(WorldGenerationConfig.HillWarpAmplitude);

        DirtNoise = MakeNoise(11, WorldGenerationConfig.DirtDepthFrequency);
        RavineRidge = MakeNoise(21, WorldGenerationConfig.RavineRidgeFrequency);
        RavineMask = MakeNoise(23, WorldGenerationConfig.RavineMaskFrequency);
        EntranceNoise = MakeNoise(31, WorldGenerationConfig.EntranceFrequency);
        IntensityNoise = MakeNoise(41, WorldGenerationConfig.CaveIntensityFrequency);
        GravelNoise = MakeNoise(61, WorldGenerationConfig.GravelFrequency);
        ContinentNoise = MakeNoise(70, WorldGenerationConfig.ContinentFrequency, octaves: 2);
        TemperatureNoise = MakeNoise(71, WorldGenerationConfig.TemperatureFrequency, octaves: 2);
        HumidityNoise = MakeNoise(88, WorldGenerationConfig.HumidityFrequency, octaves: 2);
        DuneNoise = MakeNoise(72, WorldGenerationConfig.DuneFrequency, octaves: 3);
        TrenchMask = MakeNoise(73, WorldGenerationConfig.TrenchMaskFrequency);
        TrenchRidge = MakeNoise(74, WorldGenerationConfig.TrenchFrequency);

        // Displaces the ridge lookup so ravines wind instead of following straight noise contours
        RavineWarp = new FastNoiseLite(WorldGenerationConfig.Seed + 22);
        RavineWarp.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
        RavineWarp.SetFrequency(WorldGenerationConfig.RavineWarpFrequency);
        RavineWarp.SetDomainWarpAmp(WorldGenerationConfig.RavineWarpAmplitude);

        // Trench lines wind the same way, so a gash runs along the basin floor instead of
        // tracking a straight noise contour
        TrenchWarp = new FastNoiseLite(WorldGenerationConfig.Seed + 75);
        TrenchWarp.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
        TrenchWarp.SetFrequency(WorldGenerationConfig.TrenchWarpFrequency);
        TrenchWarp.SetDomainWarpAmp(WorldGenerationConfig.TrenchWarpAmplitude);

        RiverMask = MakeNoise(81, WorldGenerationConfig.RiverMaskFrequency);
        RiverRidge = MakeNoise(82, WorldGenerationConfig.RiverFrequency);

        // Like the trench, a river winds rather than tracking a straight noise contour
        RiverWarp = new FastNoiseLite(WorldGenerationConfig.Seed + 83);
        RiverWarp.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
        RiverWarp.SetFrequency(WorldGenerationConfig.RiverWarpFrequency);
        RiverWarp.SetDomainWarpAmp(WorldGenerationConfig.RiverWarpAmplitude);

        MountainMask = MakeNoise(84, WorldGenerationConfig.MountainMaskFrequency, octaves: 2);
        MountainNoise = MakeNoise(85, WorldGenerationConfig.MountainFrequency, octaves: 3);
        OpennessNoise = MakeNoise(86, WorldGenerationConfig.OpennessFrequency, octaves: 2);
        BiomeDitherNoise = MakeNoise(89, WorldGenerationConfig.BiomeDitherFrequency);
        LakeShapeNoise = MakeNoise(90, WorldGenerationConfig.LakeShapeFrequency);

        // Mountains wind like ravines and rivers, so a range snakes instead of tracking a contour
        MountainWarp = new FastNoiseLite(WorldGenerationConfig.Seed + 87);
        MountainWarp.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
        MountainWarp.SetFrequency(WorldGenerationConfig.MountainWarpFrequency);
        MountainWarp.SetDomainWarpAmp(WorldGenerationConfig.MountainWarpAmplitude);
    }

    /// <summary>
    /// Whether a column's terrain sits below the waterline. This is only the height test: since
    /// land floods only in a lake or river bed, a below-waterline column is not necessarily wet —
    /// ask <see cref="IsWaterColumn"/> for that. A column whose height equals
    /// <see cref="WorldGenerationConfig.SeaLevel"/> is at the line itself and is not below it.
    /// </summary>
    public static bool IsSubmerged(int height) =>
        WorldGenerationConfig.WaterEnabled && height < WorldGenerationConfig.SeaLevel;

    /// <summary>Whether a column height sits in the shoreline band that builds from sand.</summary>
    public static bool IsBeachHeight(int height) =>
        height >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.BeachBelowSeaLevel
        && height <= WorldGenerationConfig.SeaLevel + WorldGenerationConfig.BeachAboveSeaLevel;

    /// <summary>Whether the beach column at (x, z) is capped with gravel. Pure function of
    /// world position like everything else here, so neighbours agree without shared state.</summary>
    public static bool IsGravelPatch(float x, float z) =>
        GravelNoise.GetNoise(x, z) > WorldGenerationConfig.GravelThreshold;

    /// <summary>Biome at a world column; a pure function of (x, z) like the rest of the 2D half.</summary>
    public static Biome BiomeAt(float x, float z) => BiomeWeights(x, z, out _, out _);

    /// <summary>
    /// 0..1 weights of the two land profiles <see cref="Compute"/> needs, plus the biome they
    /// classify to. Ocean takes the share of continentalness that falls low; desert takes the
    /// warm+dry share of what is left, so a coastal blend never grows a desert out over the water.
    /// Desert needs firmly warm air (<see cref="WorldGenerationConfig.DesertMaxCold"/>) and the
    /// cold biomes firmly cold air (<see cref="WorldGenerationConfig.ColdBiomeMinCold"/>), so the
    /// band between them is always temperate forest and dunes never touch snow. The cold biomes
    /// ride the forest's height profile, so they need no weight of their own here. Every weight is
    /// smooth, which is what lets <see cref="Compute"/> lerp the height across a border instead of
    /// stepping.
    /// </summary>
    public static Biome BiomeWeights(float x, float z, out float oceanWeight, out float desertWeight)
    {
        // Not "if (!enabled) return": the flag is a compile-time const, and a dead branch would
        // read as unreachable. Zeroing the weights keeps the toggle a real expression.
        var gate = WorldGenerationConfig.BiomesEnabled ? 1f : 0f;
        var continent = ContinentNoise.GetNoise(x, z);
        oceanWeight = (1f - MathUtils.SmoothStep(WorldGenerationConfig.ContinentOceanStart,
            WorldGenerationConfig.ContinentOceanEnd, continent)) * gate;

        var cold = 1f - MathUtils.SmoothStep(WorldGenerationConfig.TemperatureColdStart,
            WorldGenerationConfig.TemperatureColdEnd, TemperatureNoise.GetNoise(x, z));
        var dry = 1f - MathUtils.SmoothStep(WorldGenerationConfig.HumidityDryStart,
            WorldGenerationConfig.HumidityDryEnd, HumidityNoise.GetNoise(x, z));
        desertWeight = (1f - cold) * dry * (1f - oceanWeight) * gate;

        var biome = oceanWeight >= WorldGenerationConfig.OceanShoreWeight ? Biome.Ocean
            : cold >= WorldGenerationConfig.ColdBiomeMinCold ? dry >= 0.5f ? Biome.Frozen : Biome.Taiga
            : cold <= WorldGenerationConfig.DesertMaxCold && dry >= 0.5f ? Biome.Desert
            : Biome.Forest;
        return gate < 0.5f ? Biome.Forest : biome;
    }

    /// <summary>0..1 how cold (x, z) is: 1 in the taiga/frozen band, 0 in the warm band. A
    /// biome-generation input, read by the snow and freezing passes to place their features.</summary>
    public static float ColdWeight(float x, float z) =>
        1f - MathUtils.SmoothStep(WorldGenerationConfig.TemperatureColdStart,
            WorldGenerationConfig.TemperatureColdEnd, TemperatureNoise.GetNoise(x, z));

    /// <summary>Whether world column (x, z) is forest; vegetation and spawn rules ask here.</summary>
    public static bool IsForest(int x, int z) => BiomeAt(x, z) == Biome.Forest;

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
    /// while a ravine under a real water body only deepens that body.
    /// </summary>
    public static int WaterLevelAt(float x, float z)
    {
        var biome = BiomeWeights(x, z, out _, out var desertWeight);
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

    /// <summary>Whether a biome is one a lake or a river may stand in.</summary>
    private static bool CanHoldSurfaceWater(Biome biome) =>
        WorldGenerationConfig.LakesAndRiversEnabled && biome is Biome.Forest or Biome.Taiga or Biome.Frozen;

    /// <summary>
    /// The level a column with these surface-water weights floods to. Shared by
    /// <see cref="WaterLevelAt"/> and by <see cref="Compute"/>, which evaluates the same weights
    /// itself and stores the result per column, so a stored level is by construction the level the
    /// carve that made the bowl was working toward.
    /// </summary>
    private static int WaterLevelFor(Biome biome, float gate, float riverWeight, float lakeWeight,
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
    /// 0 as the column reaches the forest/desert line. Both the carve in <see cref="Compute"/> and
    /// the flood test in <see cref="IsWaterColumn"/> read it, so a lake or river tapers out and
    /// closes on forest ground just before the desert instead of being cut off in a wall. The fade
    /// only covers the last stretch before the line, so forest lakes elsewhere keep their depth.
    /// </summary>
    private static float SurfaceWaterGate(float desertWeight) =>
        1f - MathUtils.SmoothStep(WorldGenerationConfig.WaterDesertFadeStart,
            WorldGenerationConfig.WaterDesertFadeEnd, desertWeight);

    private const int LakeSalt = 0x4C414B45;

    /// <summary>
    /// 0..1 lake bowl at (x, z), plus the water level and floor of the lake that reaches the column.
    /// One hashed candidate per <see cref="WorldGenerationConfig.LakeCellBlocks"/> cell, offset inside
    /// the cell and sized by a skewed roll, so the world is mostly little ponds with the occasional
    /// broad lake rather than one uniform size. The bowl falls from 1 at the centre to 0 at the rim,
    /// so the carve blends back into the ground it was dug from, and the shoreline is the candidate's
    /// radius wobbled by <see cref="LakeShapeNoise"/> so it is ragged rather than a circle.
    /// Candidates near the origin are skipped so the spawn is always on dry land.
    /// </summary>
    private static float LakeSample(float x, float z, out int surface, out int floor)
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

            var centreX = gx * cell + Unit(h, 1) * cell;
            var centreZ = gz * cell + Unit(h, 2) * cell;
            if (MathF.Abs(centreX) < WorldGenerationConfig.LakeSpawnClearRadius
                && MathF.Abs(centreZ) < WorldGenerationConfig.LakeSpawnClearRadius)
                continue;

            var size = MathF.Pow(Unit(h, 3), WorldGenerationConfig.LakeRadiusSkew);
            var nominal = MathUtils.Lerp(WorldGenerationConfig.LakeMinRadius,
                WorldGenerationConfig.LakeMaxRadius, size);
            var ox = x - centreX;
            var oz = z - centreZ;
            var d2 = ox * ox + oz * oz;

            // The wobble below only ever pulls the shoreline inside `nominal`, so a column past it
            // is out without paying for the rim samples that choosing a water level costs.
            if (d2 >= nominal * nominal) continue;
            if (!TryLakeSite(centreX, centreZ, nominal, size, out var siteSurface, out var siteFloor))
                continue;

            var radius = nominal * (1f + WorldGenerationConfig.LakeShapeAmplitude
                * LakeShapeNoise.GetNoise(x, z));
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
    /// </summary>
    private static bool TryLakeSite(float centreX, float centreZ, float radius, float size,
        out int surface, out int floor)
    {
        var rimMin = LandHeight(centreX, centreZ, out _, out _, out _, out _);
        var rimMax = rimMin;
        for (var k = 0; k < 8; k++)
        {
            var angle = k * (MathF.Tau / 8f);
            var land = LandHeight(centreX + MathF.Cos(angle) * radius,
                centreZ + MathF.Sin(angle) * radius, out _, out _, out _, out _);
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
        return true;
    }

    /// <summary>One 0..1 hash value for a candidate, re-mixed by <paramref name="index"/>.</summary>
    private static float Unit(ulong h, int index) =>
        (SplitMix64.Mix(h + (ulong)index * 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));

    /// <summary>0..1 river weight: a warped ridge band (|noise| near zero) gated by a rarity mask.</summary>
    private static float RiverWeight(float x, float z)
    {
        var mask = MathUtils.SmoothStep(WorldGenerationConfig.RiverMaskStart,
            WorldGenerationConfig.RiverMaskEnd, RiverMask.GetNoise(x, z));
        if (mask <= 0f) return 0f;

        var warpedX = x;
        var warpedZ = z;
        RiverWarp.DomainWarp(ref warpedX, ref warpedZ);

        var ridge = MathF.Abs(RiverRidge.GetNoise(warpedX, warpedZ));
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
        BiomeWeights(x, z, out _, out var desertWeight);
        return MathF.Max(LakeSample(x, z, out _, out _), RiverWeight(x, z))
               * SurfaceWaterGate(desertWeight);
    }

    /// <summary>
    /// 0..1 how open the forest at (x, z) is: a clearing or grassy plain rather than woodland.
    /// Trees thin out to none as it rises and the hills flatten a little, so the forest alternates
    /// between rolling woodland and open regions instead of one uniform canopy. Pure function of
    /// (x, z) like the rest of the 2D half, so the tree stamper and any cache agree.
    /// </summary>
    public static float ForestOpenness(float x, float z) =>
        MathUtils.SmoothStep(WorldGenerationConfig.OpennessStart, WorldGenerationConfig.OpennessEnd,
            OpennessNoise.GetNoise(x, z));

    /// <summary>
    /// Blocks an occasional mountain range raises the forest floor at (x, z): a warped ridged
    /// field (a peak where |noise| is near zero) gated by a rare mask, sharpened so only the crest
    /// climbs. Fades out near the origin so the spawn stays a forest valley.
    /// </summary>
    private static float MountainHeight(float x, float z)
    {
        // MountainsEnabled is a compile-time const, so an early return on it would read as
        // unreachable (see BiomeWeights); a multiplier keeps the toggle a real expression.
        var enabled = WorldGenerationConfig.MountainsEnabled ? 1f : 0f;
        var mask = MathUtils.SmoothStep(WorldGenerationConfig.MountainMaskStart,
            WorldGenerationConfig.MountainMaskEnd, MountainMask.GetNoise(x, z)) * enabled;
        if (mask <= 0f) return 0f;

        var warpedX = x;
        var warpedZ = z;
        MountainWarp.DomainWarp(ref warpedX, ref warpedZ);
        var ridge = 1f - MathF.Abs(MountainNoise.GetNoise(warpedX, warpedZ));
        ridge = MathF.Pow(ridge, WorldGenerationConfig.MountainSharpness);

        var clear = MathUtils.SmoothStep(WorldGenerationConfig.MountainSpawnClearStart,
            WorldGenerationConfig.MountainSpawnClearEnd, MathF.Sqrt(x * x + z * z));
        return ridge * mask * WorldGenerationConfig.MountainAmplitude * clear;
    }

    /// <summary>
    /// How many blocks a trench lowers the ocean floor at (x, z): a warped ridged band (|n| near
    /// zero) gives the gash line, a rarity mask makes trenches finite, and the parabolic profile
    /// gives steep walls like <see cref="RavineDepthAt"/>. The caller weights it by the ocean
    /// share so a trench only ever cuts underwater.
    /// </summary>
    private static float TrenchDepthAt(float x, float z)
    {
        var mask = MathUtils.SmoothStep(WorldGenerationConfig.TrenchMaskStart,
            WorldGenerationConfig.TrenchMaskEnd, TrenchMask.GetNoise(x, z));
        if (mask <= 0f) return 0f;

        var warpedX = x;
        var warpedZ = z;
        TrenchWarp.DomainWarp(ref warpedX, ref warpedZ);

        var ridge = MathF.Abs(TrenchRidge.GetNoise(warpedX, warpedZ));
        if (ridge >= WorldGenerationConfig.TrenchWidth) return 0f;

        var t = ridge / WorldGenerationConfig.TrenchWidth;
        return WorldGenerationConfig.TrenchDepth * mask * (1f - t * t);
    }

    /// <summary>
    /// Nudges a column's biome across the forest/desert material line inside the blend band, so
    /// the grass-to-sand edge reads as a ragged patchwork instead of a drawn contour. Only the
    /// surface material flips; the height already came from the smooth desert weight, so the
    /// ground stays continuous.
    /// </summary>
    private static Biome DitherBiome(float x, float z, Biome biome, float desertWeight)
    {
        if (biome is not (Biome.Forest or Biome.Desert)) return biome;
        if (MathF.Abs(desertWeight - 0.5f) >= WorldGenerationConfig.BiomeBorderDither) return biome;

        // The dither only raggeds the material line; it must not push sand past the temperate
        // buffer into the cold band, or dunes would again sit against snow.
        if (biome == Biome.Forest && ColdWeight(x, z) > WorldGenerationConfig.DesertMaxCold)
            return biome;

        // A coherent field, so whole patches flip: a per-column hash turned the line into a
        // checkerboard instead of a ragged edge.
        if (BiomeDitherNoise.GetNoise(x, z) <= 0f) return biome;
        return biome == Biome.Forest ? Biome.Desert : Biome.Forest;
    }

    /// <summary>OpenSimplex2 noise seeded from the world seed; more than one octave turns on FBm.</summary>
    internal static FastNoiseLite MakeNoise(int seedOffset, float frequency, int octaves = 1)
    {
        var noise = new FastNoiseLite(WorldGenerationConfig.Seed + seedOffset);
        noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        noise.SetFrequency(frequency);

        if (octaves > 1)
        {
            noise.SetFractalType(FastNoiseLite.FractalType.FBm);
            noise.SetFractalOctaves(octaves);
        }

        return noise;
    }

    /// <summary>
    /// One column's land height before any water or ravine is cut into it: the forest, desert-dune
    /// and ocean-ramp profiles blended by the biome weights and clamped above the waterline. A lake
    /// measures this at its own rim to choose where its water sits, so it must stay the exact value
    /// <see cref="Compute"/> carves into. <paramref name="forestHeight"/> is the pre-blend forest
    /// profile, which the river carve reads to dry out as the land climbs.
    /// </summary>
    private static float LandHeight(float worldX, float worldZ, out Biome biome,
        out float oceanWeight, out float desertWeight, out float forestHeight)
    {
        // Forest floor: a broad swell plus a hill mass (a positive-only field gated by the
        // hill mask) so hills rise in patches and open flat valleys run between them. Open
        // regions (see ForestOpenness) flatten the hills and drop their trees, so the
        // forest alternates between rolling woodland and open grass instead of one mass.
        // An occasional mountain range lifts the floor well above the hills. The floor
        // stays above sea level, which is what makes a carved lake or river enclosed.
        var swell = ForestSwellNoise.GetNoise(worldX, worldZ) * WorldGenerationConfig.ForestSwellAmplitude;
        var hillMask = MathUtils.SmoothStep(WorldGenerationConfig.HillMaskStart,
            WorldGenerationConfig.HillMaskEnd, HillMaskNoise.GetNoise(worldX, worldZ));
        var warpedX = worldX;
        var warpedZ = worldZ;
        HillWarp.DomainWarp(ref warpedX, ref warpedZ);
        var hill = 0.5f + 0.5f * HillNoise.GetNoise(warpedX, warpedZ);
        var rough = ValleyRoughNoise.GetNoise(worldX, worldZ) * WorldGenerationConfig.ValleyRoughAmplitude;
        var openness = ForestOpenness(worldX, worldZ);
        var hillScale = 1f - WorldGenerationConfig.OpennessHillReduction * openness;
        forestHeight = WorldGenerationConfig.ForestBaseHeight + swell
            + hill * WorldGenerationConfig.HillAmplitude * hillMask * hillScale
            + rough
            + MountainHeight(worldX, worldZ);
        var heightF = forestHeight;

        // Biome blend: dunes lift the desert share of the land, then the ocean ramp empties
        // the basins. Both are weighted 0..1, so a border lerps between the profiles and
        // comes out as a slope, never a cliff.
        biome = BiomeWeights(worldX, worldZ, out oceanWeight, out desertWeight);
        if (desertWeight > 0f)
        {
            // Smooth dunes on the swell, not the forest hills: a desert is its own profile
            var dune = WorldGenerationConfig.ForestBaseHeight + swell
                + DuneNoise.GetNoise(worldX, worldZ) * WorldGenerationConfig.DuneAmplitude;
            heightF = MathUtils.Lerp(heightF, dune, desertWeight);
        }

        if (oceanWeight > 0f)
        {
            var depth = MathF.Pow(oceanWeight, WorldGenerationConfig.OceanDepthExponent);
            var oceanHeight = MathUtils.Lerp(WorldGenerationConfig.OceanShoreHeight,
                WorldGenerationConfig.OceanFloorHeight, depth);
            oceanHeight -= TrenchDepthAt(worldX, worldZ) * oceanWeight;
            heightF = MathUtils.Lerp(heightF, oceanHeight, oceanWeight);
        }

        // Keep the land above the waterline, but ease the clamp out as the ocean blend
        // takes over. Clamping only while the biome was land put a hard step at the ocean
        // line, because the ocean side never clamped; fading it makes the coast a slope.
        // Lakes and rivers are cut after this, so they still reach below the waterline.
        var shoreClamp = 1f - MathUtils.SmoothStep(WorldGenerationConfig.OceanShoreWeight,
            0.5f, oceanWeight);
        if (shoreClamp > 0f)
            heightF = MathUtils.Lerp(heightF,
                MathF.Max(heightF, WorldGenerationConfig.SeaLevel + 1f), shoreClamp);

        return heightF;
    }

    /// <summary>Computes every 2D input for the chunk column at (chunkX, chunkZ).</summary>
    public static ColumnData Compute(int chunkX, int chunkZ)
    {
        const int count = Chunk.Size * Chunk.Size;
        var heights = new int[count];
        var bed = new int[count];
        var dirtDepth = new byte[count];
        var entrance = new float[count];
        var intensity = new float[count];
        var biomes = new Biome[count];
        var waterLevel = new int[count];

        var baseX = (float)chunkX * Chunk.Size;
        var baseZ = (float)chunkZ * Chunk.Size;

        for (var x = 0; x < Chunk.Size; x++)
        {
            var worldX = x + baseX;
            for (var z = 0; z < Chunk.Size; z++)
            {
                var worldZ = z + baseZ;
                var i = (x << Chunk.SizeShift) | z;

                var heightF = LandHeight(worldX, worldZ, out var biome, out var oceanWeight,
                    out var desertWeight, out var forestHeight);

                // Surface water: a lake is a bowl around its own water level and a river a channel
                // down to the waterline, each returning to un-carved ground at its edge, so the
                // water is enclosed by its own banks. A river also fades out as the local land
                // climbs, so it dries into a wash uphill instead of gorging a hillside. Every land
                // biome but the desert, gated toward a desert so a body closes before the biome
                // line instead of ending in a wall.
                var gate = SurfaceWaterGate(desertWeight);
                var lakeWeight = 0f;
                var lakeSurface = NoWater;
                var lakeFloor = 0;
                var riverWeight = 0f;
                if (CanHoldSurfaceWater(biome))
                {
                    lakeWeight = LakeSample(worldX, worldZ, out lakeSurface, out lakeFloor);
                    riverWeight = RiverWeight(worldX, worldZ);
                }

                if (lakeWeight > 0f)
                {
                    // Never fills ground the lake already sits below: on a slope the bowl only
                    // ever digs, and where the land is lower the water simply covers it, the
                    // same way an ocean would.
                    var target = MathF.Min(lakeFloor, heightF);
                    heightF = MathUtils.Lerp(heightF, target, lakeWeight * gate);
                }

                if (riverWeight > 0f)
                {
                    var lowland = 1f - MathUtils.SmoothStep(WorldGenerationConfig.RiverLowStart,
                        WorldGenerationConfig.RiverLowEnd, forestHeight);
                    var river = riverWeight * gate * lowland;
                    if (river > 0f)
                        heightF = MathUtils.Lerp(heightF,
                            WorldGenerationConfig.SeaLevel - WorldGenerationConfig.RiverBedDepth, river);
                }

                waterLevel[i] = WaterLevelFor(biome, gate, riverWeight, lakeWeight, lakeSurface);

                var height = (int)heightF;

                // Sampled once and reused: the same value cuts the ravine into the heightmap and
                // decides how much soil is left on the column. The cut fades out across the shore
                // band — a ravine is a land feature, and one running into the sea used to leave a
                // dry trench slicing the water's edge, making the ocean look cut off (the ocean's
                // own gashes are trenches, not ravines).
                var ravineDepth = WorldGenerationConfig.RavinesEnabled
                    ? (int)(RavineDepthAt(worldX, worldZ) * (1f - MathUtils.SmoothStep(
                        WorldGenerationConfig.OceanShoreWeight, 0.5f, oceanWeight)))
                    : 0;

                bed[i] = height;
                heights[i] = height - ravineDepth;

                var dirt = WorldGenerationConfig.DirtDepthBase
                           + WorldGenerationConfig.DirtDepthVariation * DirtNoise.GetNoise(worldX, worldZ);
                var soil = Math.Clamp((int)MathF.Round(dirt), 1, 8);

                // Ravine walls are exposed rock, not stacked grass/dirt terraces: strip the soil
                // entirely once the column is cut deep enough, and thin it toward the rim so the
                // transition fades in. Above the cutoff this stays >= 1, so 0 only ever means
                // "bare stone column" to Chunk.GenerateColumn.
                if (ravineDepth >= WorldGenerationConfig.RavineBareStoneDepth)
                    soil = 0;
                else if (ravineDepth > 0)
                    soil = Math.Min(soil, WorldGenerationConfig.RavineBareStoneDepth - ravineDepth);

                // A forest peak above the rock line is bare stone too, so a high crest reads as a
                // rocky mountain instead of another tree-covered hill. Read off the final heightmap
                // (heights[i], ravines included) so the far LOD's rock cap agrees.
                if (biome == Biome.Forest && heights[i] >= WorldGenerationConfig.MountainRockHeight)
                    soil = 0;

                dirtDepth[i] = (byte)soil;

                // Surface cave entrances fade out before the shore band too: an entrance on a beach
                // let a cave breach the sand into a water-filled hole (see the ravine fade above).
                var entranceShore = 1f - MathUtils.SmoothStep(
                    WorldGenerationConfig.OceanShoreWeight - WorldGenerationConfig.EntranceShoreFade,
                    WorldGenerationConfig.OceanShoreWeight, oceanWeight);
                entrance[i] = MathUtils.SmoothStep(
                    WorldGenerationConfig.EntranceStart,
                    WorldGenerationConfig.EntranceEnd,
                    EntranceNoise.GetNoise(worldX, worldZ)) * entranceShore;

                intensity[i] = IntensityNoise.GetNoise(worldX, worldZ) * WorldGenerationConfig.CaveIntensityRange;
                biomes[i] = DitherBiome(worldX, worldZ, biome, desertWeight);
            }
        }

        return new ColumnData(heights, dirtDepth, entrance, intensity, biomes, bed, waterLevel);
    }

    /// <summary>
    /// How many blocks a ravine lowers the terrain at (x, z): a ridged noise band (|n| close to
    /// zero) gives the canyon line, a rarity mask makes ravines finite segments and fades their
    /// ends out, and the parabolic profile gives steep walls with a rounded floor.
    /// </summary>
    private static int RavineDepthAt(float x, float z)
    {
        var mask = MathUtils.SmoothStep(
            WorldGenerationConfig.RavineMaskStart,
            WorldGenerationConfig.RavineMaskEnd,
            RavineMask.GetNoise(x, z));
        if (mask <= 0f) return 0;

        var warpedX = x;
        var warpedZ = z;
        RavineWarp.DomainWarp(ref warpedX, ref warpedZ);

        var ridge = MathF.Abs(RavineRidge.GetNoise(warpedX, warpedZ));
        if (ridge >= WorldGenerationConfig.RavineWidth) return 0;

        var t = ridge / WorldGenerationConfig.RavineWidth;
        return (int)(WorldGenerationConfig.RavineDepth * mask * (1f - t * t));
    }
}
