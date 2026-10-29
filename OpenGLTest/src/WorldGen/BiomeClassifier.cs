using System;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The biome climate split of the 2D terrain half: continentalness, temperature and humidity turned into the smooth ocean,
/// desert and arid weights and the biome they classify to (<see cref="BiomeWeights(float, float, out float, out float, out float)"/>,
/// over the pure <see cref="Classify"/> / <see cref="ColdOf"/> / <see cref="WarmOf"/> / <see cref="DryOf"/> /
/// <see cref="SemiDryOf"/> seams), the cold and warm ramps, and the ragged desert material line (<see cref="DitherBiome"/>).
/// <see cref="SampleClimate"/> is the one reader of the temperature and humidity noises, which it reads at a domain-warped
/// position, so every consumer (the biome, the cold and warm weights, the tint climate) sees the same values. A pure function of (x, z) and the seed, read on any worker
/// thread; <see cref="TerrainGenerator"/> forwards its public queries here.
/// </summary>
internal static class BiomeClassifier
{
    /// <summary>The raw temperature and humidity noise at the domain-warped position of (x, z): the
    /// only reader of <see cref="TerrainNoise.TemperatureNoise"/>, <see cref="TerrainNoise.HumidityNoise"/>
    /// and <see cref="TerrainNoise.ClimateWarp"/>, so every climate consumer sees the same warped
    /// sample. The warp is exactly zero at the origin, so the origin's raw humidity stays exactly 0.</summary>
    internal static void SampleClimate(float x, float z, out float temperature, out float humidity)
    {
        var wx = x;
        var wz = z;
        TerrainNoise.ClimateWarp.DomainWarp(ref wx, ref wz);
        temperature = TerrainNoise.TemperatureNoise.GetNoise(wx, wz);
        humidity = TerrainNoise.HumidityNoise.GetNoise(wx, wz);
    }

    /// <summary>The ocean and desert weights and the biome they classify to; the arid weight is
    /// discarded (<see cref="BiomeWeights(float, float, out float, out float, out float)"/>).</summary>
    public static Biome BiomeWeights(float x, float z, out float oceanWeight, out float desertWeight) =>
        BiomeWeights(x, z, out oceanWeight, out desertWeight, out _);

    /// <summary>
    /// 0..1 weights of the land profiles <see cref="TerrainGenerator.Compute"/> needs, plus the biome they
    /// classify to. Ocean takes the share of continentalness that falls low; desert takes the
    /// warm+dry share of what is left, so a coastal blend never grows a desert out over the water.
    /// Desert needs firmly warm air (the midpoint of the warm ramp,
    /// <see cref="WorldGenerationConfig.TemperatureWarmStart"/>..<see cref="WorldGenerationConfig.TemperatureWarmEnd"/>)
    /// and the cold biomes firmly cold air (<see cref="WorldGenerationConfig.ColdBiomeMinCold"/> on
    /// the cold ramp), so the raw-noise band between the two ramps is temperate forest or steppe and
    /// dunes never touch snow. <paramref name="desertWeight"/> is min(warm, dry) scaled by the land
    /// share, so it is exactly 0.5 on the inland desert line, on the humid border and the temperate
    /// border alike. <paramref name="aridWeight"/> is the semi-dry weight scaled the same way: 1 on
    /// dry and semi-dry land alike (the ramps are exclusive, so any dryness means fully semi-dry), so
    /// it flattens the steppe, the savanna and the tundra into plains. The cold biomes ride the
    /// forest's height profile, so they need no weight of their own here. Every weight is smooth,
    /// which is what lets <see cref="TerrainGenerator.Compute"/> lerp the height across a border
    /// instead of stepping. <paramref name="oceanWeight"/> is the folded weight: the pre-island ocean
    /// weight (<see cref="PreIslandOceanWeight"/>) times one minus the island land weight
    /// (<see cref="TerrainIslands.LandWeight(float, float, float)"/>), so an island is ordinary land of
    /// its climate to every consumer, while land and the coast band, where the island term is exactly
    /// 0, keep their bits.
    /// </summary>
    public static Biome BiomeWeights(float x, float z, out float oceanWeight, out float desertWeight,
        out float aridWeight)
    {
        oceanWeight = PreIslandOceanWeight(x, z);
        var island = TerrainIslands.LandWeight(x, z, oceanWeight);
        // No multiply when the island term is 0, so land, the coast band and the spawn keep their bits.
        if (island > 0f) oceanWeight *= 1f - island;
        return WeightsForOcean(x, z, oceanWeight, out desertWeight, out aridWeight);
    }

    /// <summary>The ocean weight before the island fold: the share of continentalness that falls
    /// low. The one reader of <see cref="TerrainNoise.ContinentNoise"/> for the biome weights.</summary>
    internal static float PreIslandOceanWeight(float x, float z)
    {
        // Not "if (!enabled) return": the flag is a compile-time const, and a dead branch would
        // read as unreachable. Zeroing the weights keeps the toggle a real expression.
        var gate = WorldGenerationConfig.BiomesEnabled ? 1f : 0f;
        var continent = TerrainNoise.ContinentNoise.GetNoise(x, z);
        return (1f - MathUtils.SmoothStep(WorldGenerationConfig.ContinentOceanStart,
            WorldGenerationConfig.ContinentOceanEnd, continent)) * gate;
    }

    /// <summary>The desert and arid weights and the biome of (x, z) for a given (already folded)
    /// ocean weight: the climate half of
    /// <see cref="BiomeWeights(float, float, out float, out float, out float)"/>.</summary>
    internal static Biome WeightsForOcean(float x, float z, float oceanWeight, out float desertWeight,
        out float aridWeight)
    {
        var gate = WorldGenerationConfig.BiomesEnabled ? 1f : 0f;

        // One climate sample feeds every ramp.
        SampleClimate(x, z, out var temperature, out var humidity);
        var cold = ColdOf(temperature);
        var warm = WarmOf(temperature);
        var dry = DryOf(humidity);
        var semiDry = SemiDryOf(humidity);
        // Min, not a product: the dune blend reaches 0.5 exactly where Classify turns desert.
        desertWeight = MathF.Min(warm, dry) * (1f - oceanWeight) * gate;
        aridWeight = semiDry * (1f - oceanWeight) * gate;

        return gate < 0.5f ? Biome.Forest : Classify(oceanWeight, cold, warm, dry, semiDry);
    }

    /// <summary>
    /// The pure classification rule behind <see cref="BiomeWeights(float, float, out float, out float, out float)"/>,
    /// a 3x3 climate grid: cold land is frozen tundra when dry and taiga otherwise; warm land is
    /// desert when dry, savanna when semi-dry and forest when wet; temperate land is steppe when dry
    /// or semi-dry and forest when wet. So the dry axis reads desert, steppe, tundra and the warm
    /// axis forest, savanna, desert, and a forest (which needs wet air) never separates sand from
    /// snow. Desert is still exactly min(warm, dry) >= 0.5. 0.5 is the midpoint of every smoothstep
    /// ramp in this file, and the same line the desert weight, the border dither and the
    /// surface-water fade are keyed to.
    /// </summary>
    internal static Biome Classify(float oceanWeight, float cold, float warm, float dry, float semiDry) =>
        oceanWeight >= WorldGenerationConfig.OceanShoreWeight ? Biome.Ocean
        : cold >= WorldGenerationConfig.ColdBiomeMinCold ? dry >= 0.5f ? Biome.Frozen : Biome.Taiga
        : warm >= 0.5f ? dry >= 0.5f ? Biome.Desert : semiDry >= 0.5f ? Biome.Savanna : Biome.Forest
        : semiDry >= 0.5f || dry >= 0.5f ? Biome.Steppe
        : Biome.Forest;

    /// <summary>The pure dry ramp: 1 at or below <see cref="WorldGenerationConfig.HumidityDryStart"/>,
    /// 0 at or above <see cref="WorldGenerationConfig.HumidityDryEnd"/>.</summary>
    internal static float DryOf(float humidity) =>
        1f - MathUtils.SmoothStep(WorldGenerationConfig.HumidityDryStart,
            WorldGenerationConfig.HumidityDryEnd, humidity);

    /// <summary>The pure semi-dry ramp, wholly on the wet side of <see cref="DryOf"/>: 1 at or below
    /// <see cref="WorldGenerationConfig.HumiditySemiDryStart"/>, 0 at or above
    /// <see cref="WorldGenerationConfig.HumiditySemiDryEnd"/>.</summary>
    internal static float SemiDryOf(float humidity) =>
        1f - MathUtils.SmoothStep(WorldGenerationConfig.HumiditySemiDryStart,
            WorldGenerationConfig.HumiditySemiDryEnd, humidity);

    /// <summary>The two humidity ramps as one 0..1 axis, the way the tint climate reads warmth: 0
    /// fully dry, exactly 0.5 between the dry and the semi-dry ramp, 1 wet.</summary>
    internal static float WetnessOf(float humidity) =>
        0.5f * (1f - DryOf(humidity)) + 0.5f * (1f - SemiDryOf(humidity));

    /// <summary>The pure cold ramp behind <see cref="ColdWeight"/>: 1 at or below
    /// <see cref="WorldGenerationConfig.TemperatureColdStart"/>, 0 at or above
    /// <see cref="WorldGenerationConfig.TemperatureColdEnd"/>.</summary>
    internal static float ColdOf(float temperature) =>
        1f - MathUtils.SmoothStep(WorldGenerationConfig.TemperatureColdStart,
            WorldGenerationConfig.TemperatureColdEnd, temperature);

    /// <summary>The pure warm ramp behind <see cref="WarmWeight"/>: 0 at or below
    /// <see cref="WorldGenerationConfig.TemperatureWarmStart"/>, 1 at or above
    /// <see cref="WorldGenerationConfig.TemperatureWarmEnd"/>.</summary>
    internal static float WarmOf(float temperature) =>
        MathUtils.SmoothStep(WorldGenerationConfig.TemperatureWarmStart,
            WorldGenerationConfig.TemperatureWarmEnd, temperature);

    /// <summary>0..1 how cold (x, z) is: 1 in the taiga/frozen band, 0 through the temperate band
    /// and the warm ramp. A biome-generation input, read by the snow and freezing passes to place
    /// their features.</summary>
    public static float ColdWeight(float x, float z)
    {
        SampleClimate(x, z, out var temperature, out _);
        return ColdOf(temperature);
    }

    /// <summary>0..1 how warm (x, z) is: 1 in the desert band, 0 through the temperate band and
    /// the whole cold ramp. A biome-generation input like <see cref="ColdWeight"/>.</summary>
    public static float WarmWeight(float x, float z)
    {
        SampleClimate(x, z, out var temperature, out _);
        return WarmOf(temperature);
    }

    /// <summary>
    /// Nudges a column's biome across the desert's material line inside the blend band, so the
    /// grass-to-sand edge reads as a ragged patchwork instead of a drawn contour. Only the surface
    /// material flips; the height already came from the smooth desert weight, so the ground stays
    /// continuous. A savanna column may take sand (it is always on the warm ramp, so sand never
    /// enters the temperate band); a steppe column never does (it is below the warm line). A desert
    /// column takes the grass biome across the line it actually borders: of the two ramps that bind
    /// desertWeight = min(warm, dry), a binding warmth means the temperate steppe, a binding dryness
    /// the savanna. Forest never borders the desert, so it and every other biome are left alone.
    /// </summary>
    internal static Biome DitherBiome(float x, float z, Biome biome, float desertWeight)
    {
        if (biome is not (Biome.Savanna or Biome.Desert)) return biome;
        if (MathF.Abs(desertWeight - 0.5f) >= WorldGenerationConfig.BiomeBorderDither) return biome;

        // A coherent field, so whole patches flip: a per-column hash turned the line into a
        // checkerboard instead of a ragged edge.
        if (TerrainNoise.BiomeDitherNoise.GetNoise(x, z) <= 0f) return biome;
        if (biome == Biome.Savanna) return Biome.Desert;

        SampleClimate(x, z, out var temperature, out var humidity);
        return WarmOf(temperature) <= DryOf(humidity) ? Biome.Steppe : Biome.Savanna;
    }
}
