using System;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The biome climate split of the 2D terrain half: continentalness, temperature and humidity turned into the smooth ocean and
/// desert weights and the biome they classify to (<see cref="BiomeWeights"/>, over the pure <see cref="Classify"/> /
/// <see cref="ColdOf"/> / <see cref="WarmOf"/> seams), the cold and warm ramps, and the ragged forest/desert material line
/// (<see cref="DitherBiome"/>). A pure function of (x, z) and the seed, read on any worker thread; <see cref="TerrainGenerator"/>
/// forwards its public queries here.
/// </summary>
internal static class BiomeClassifier
{
    /// <summary>
    /// 0..1 weights of the two land profiles <see cref="TerrainGenerator.Compute"/> needs, plus the biome they
    /// classify to. Ocean takes the share of continentalness that falls low; desert takes the
    /// warm+dry share of what is left, so a coastal blend never grows a desert out over the water.
    /// Desert needs firmly warm air (the midpoint of the warm ramp,
    /// <see cref="WorldGenerationConfig.TemperatureWarmStart"/>..<see cref="WorldGenerationConfig.TemperatureWarmEnd"/>)
    /// and the cold biomes firmly cold air (<see cref="WorldGenerationConfig.ColdBiomeMinCold"/> on
    /// the cold ramp), so the whole raw-noise band between the two ramps is temperate forest and
    /// dunes never touch snow. <paramref name="desertWeight"/> is min(warm, dry) scaled by the land
    /// share, so it is exactly 0.5 on the inland desert line, on the humid border and the temperate
    /// border alike. The cold biomes ride the forest's height profile, so they need no weight of
    /// their own here. Every weight is smooth, which is what lets <see cref="TerrainGenerator.Compute"/> lerp the
    /// height across a border instead of stepping.
    /// </summary>
    public static Biome BiomeWeights(float x, float z, out float oceanWeight, out float desertWeight)
    {
        // Not "if (!enabled) return": the flag is a compile-time const, and a dead branch would
        // read as unreachable. Zeroing the weights keeps the toggle a real expression.
        var gate = WorldGenerationConfig.BiomesEnabled ? 1f : 0f;
        var continent = TerrainNoise.ContinentNoise.GetNoise(x, z);
        oceanWeight = (1f - MathUtils.SmoothStep(WorldGenerationConfig.ContinentOceanStart,
            WorldGenerationConfig.ContinentOceanEnd, continent)) * gate;

        // One temperature eval feeds both ramps.
        var temperature = TerrainNoise.TemperatureNoise.GetNoise(x, z);
        var cold = ColdOf(temperature);
        var warm = WarmOf(temperature);
        var dry = 1f - MathUtils.SmoothStep(WorldGenerationConfig.HumidityDryStart,
            WorldGenerationConfig.HumidityDryEnd, TerrainNoise.HumidityNoise.GetNoise(x, z));
        // Min, not a product: the dune blend reaches 0.5 exactly where Classify turns desert.
        desertWeight = MathF.Min(warm, dry) * (1f - oceanWeight) * gate;

        return gate < 0.5f ? Biome.Forest : Classify(oceanWeight, cold, warm, dry);
    }

    /// <summary>
    /// The pure classification rule behind <see cref="BiomeWeights"/>. 0.5 is the midpoint of
    /// every smoothstep ramp in this file, and the same line the desert weight, the border dither
    /// and the surface-water fade are keyed to.
    /// </summary>
    internal static Biome Classify(float oceanWeight, float cold, float warm, float dry) =>
        oceanWeight >= WorldGenerationConfig.OceanShoreWeight ? Biome.Ocean
        : cold >= WorldGenerationConfig.ColdBiomeMinCold ? dry >= 0.5f ? Biome.Frozen : Biome.Taiga
        : MathF.Min(warm, dry) >= 0.5f ? Biome.Desert
        : Biome.Forest;

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
    public static float ColdWeight(float x, float z) => ColdOf(TerrainNoise.TemperatureNoise.GetNoise(x, z));

    /// <summary>0..1 how warm (x, z) is: 1 in the desert band, 0 through the temperate band and
    /// the whole cold ramp. A biome-generation input like <see cref="ColdWeight"/>.</summary>
    public static float WarmWeight(float x, float z) => WarmOf(TerrainNoise.TemperatureNoise.GetNoise(x, z));

    /// <summary>
    /// Nudges a column's biome across the forest/desert material line inside the blend band, so
    /// the grass-to-sand edge reads as a ragged patchwork instead of a drawn contour. Only the
    /// surface material flips; the height already came from the smooth desert weight, so the
    /// ground stays continuous.
    /// </summary>
    internal static Biome DitherBiome(float x, float z, Biome biome, float desertWeight)
    {
        if (biome is not (Biome.Forest or Biome.Desert)) return biome;
        if (MathF.Abs(desertWeight - 0.5f) >= WorldGenerationConfig.BiomeBorderDither) return biome;

        // The dither only raggeds the material line; it must never push sand below the warm line
        // into the temperate band, so a desert column is always fully on the warm ramp.
        if (biome == Biome.Forest && WarmWeight(x, z) < 0.5f)
            return biome;

        // A coherent field, so whole patches flip: a per-column hash turned the line into a
        // checkerboard instead of a ragged edge.
        if (TerrainNoise.BiomeDitherNoise.GetNoise(x, z) <= 0f) return biome;
        return biome == Biome.Forest ? Biome.Desert : Biome.Forest;
    }
}
