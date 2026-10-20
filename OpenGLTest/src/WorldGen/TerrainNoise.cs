namespace VoxelEngine.WorldGen;

/// <summary>
/// Every noise channel of the 2D terrain half, read by <see cref="TerrainGenerator"/>, <see cref="BiomeClassifier"/> and <see cref="WaterBodies"/>. Each channel's seed is
/// <see cref="WorldGenerationConfig.Seed"/> plus a fixed per-channel offset. The channels are built by <see cref="Reseed"/>,
/// once by the static constructor and again only by <see cref="WorldSeed.Apply"/> while no worker runs, and are only read
/// otherwise, which keeps every reader safe to call from any worker thread.
/// </summary>
internal static class TerrainNoise
{
    internal static FastNoiseLite ForestSwellNoise;
    internal static FastNoiseLite HillNoise;
    internal static FastNoiseLite HillMaskNoise;
    internal static FastNoiseLite ValleyRoughNoise;
    internal static FastNoiseLite HillWarp;
    internal static FastNoiseLite DirtNoise;
    internal static FastNoiseLite RavineRidge;
    internal static FastNoiseLite RavineWarp;
    internal static FastNoiseLite RavineMask;
    internal static FastNoiseLite EntranceNoise;
    internal static FastNoiseLite IntensityNoise;
    internal static FastNoiseLite GravelNoise;
    internal static FastNoiseLite ContinentNoise;
    internal static FastNoiseLite TemperatureNoise;
    internal static FastNoiseLite HumidityNoise;
    internal static FastNoiseLite DuneNoise;
    internal static FastNoiseLite TrenchMask;
    internal static FastNoiseLite TrenchRidge;
    internal static FastNoiseLite TrenchWarp;
    internal static FastNoiseLite RiverMask;
    internal static FastNoiseLite RiverRidge;
    internal static FastNoiseLite RiverWarp;
    internal static FastNoiseLite MountainMask;
    internal static FastNoiseLite MountainNoise;
    internal static FastNoiseLite MountainWarp;
    internal static FastNoiseLite OpennessNoise;
    internal static FastNoiseLite BiomeDitherNoise;
    internal static FastNoiseLite LakeShapeNoise;

    static TerrainNoise()
    {
        Reseed();
    }

    /// <summary>
    /// Builds every noise channel from the current <see cref="WorldGenerationConfig.Seed"/>. The
    /// static constructor runs it once; <see cref="WorldSeed.Apply"/> runs it again between worlds,
    /// only while no worker thread can read these fields.
    /// </summary>
    internal static void Reseed()
    {
        // The rolling base keeps the original seed and settings exactly, so natural terrain
        // is unchanged wherever the flat mask stays at zero. The mask is rare and
        // low-frequency on purpose: most of the world keeps its old look, and only large
        // patches go flat, with a wide soft blend between.
        ForestSwellNoise = MakeNoise(52, WorldGenerationConfig.ForestSwellFrequency, octaves: 2);
        HillNoise = MakeNoise(54, WorldGenerationConfig.HillFrequency, octaves: 3);
        HillMaskNoise = MakeNoise(55, WorldGenerationConfig.HillMaskFrequency, octaves: 2);
        ValleyRoughNoise = MakeNoise(56, WorldGenerationConfig.ValleyRoughFrequency);

        // Winding hill outlines, the same domain-warp trick the ravines and rivers use
        HillWarp = MakeWarp(57, WorldGenerationConfig.HillWarpFrequency, WorldGenerationConfig.HillWarpAmplitude);

        DirtNoise = MakeNoise(11, WorldGenerationConfig.DirtDepthFrequency);
        RavineRidge = MakeNoise(21, WorldGenerationConfig.RavineRidgeFrequency);
        RavineMask = MakeNoise(23, WorldGenerationConfig.RavineMaskFrequency);
        EntranceNoise = MakeNoise(31, WorldGenerationConfig.EntranceFrequency);
        IntensityNoise = MakeNoise(41, WorldGenerationConfig.CaveIntensityFrequency);
        GravelNoise = MakeNoise(61, WorldGenerationConfig.GravelFrequency);
        ContinentNoise = MakeNoise(70, WorldGenerationConfig.ContinentFrequency, octaves: 2);
        // Temperature is a single octave on purpose: the fine octave only steepened the gradient,
        // which is what squeezes the temperate band between snow and sand. Single-octave output
        // spans the raw [-1, 1] where two-octave FBm was scaled by 1/1.5, so this choice also set
        // the cold ramp's share (taiga, frozen, snow) and re-laid every temperature value.
        TemperatureNoise = MakeNoise(71, WorldGenerationConfig.TemperatureFrequency);
        HumidityNoise = MakeNoise(88, WorldGenerationConfig.HumidityFrequency, octaves: 2);
        DuneNoise = MakeNoise(72, WorldGenerationConfig.DuneFrequency, octaves: 3);
        TrenchMask = MakeNoise(73, WorldGenerationConfig.TrenchMaskFrequency);
        TrenchRidge = MakeNoise(74, WorldGenerationConfig.TrenchFrequency);

        // Displaces the ridge lookup so ravines wind instead of following straight noise contours
        RavineWarp = MakeWarp(22, WorldGenerationConfig.RavineWarpFrequency, WorldGenerationConfig.RavineWarpAmplitude);

        // Trench lines wind the same way, so a gash runs along the basin floor instead of
        // tracking a straight noise contour
        TrenchWarp = MakeWarp(75, WorldGenerationConfig.TrenchWarpFrequency, WorldGenerationConfig.TrenchWarpAmplitude);

        RiverMask = MakeNoise(81, WorldGenerationConfig.RiverMaskFrequency);
        RiverRidge = MakeNoise(82, WorldGenerationConfig.RiverFrequency);

        // Like the trench, a river winds rather than tracking a straight noise contour
        RiverWarp = MakeWarp(83, WorldGenerationConfig.RiverWarpFrequency, WorldGenerationConfig.RiverWarpAmplitude);

        MountainMask = MakeNoise(84, WorldGenerationConfig.MountainMaskFrequency, octaves: 2);
        MountainNoise = MakeNoise(85, WorldGenerationConfig.MountainFrequency, octaves: 3);
        OpennessNoise = MakeNoise(86, WorldGenerationConfig.OpennessFrequency, octaves: 2);
        BiomeDitherNoise = MakeNoise(89, WorldGenerationConfig.BiomeDitherFrequency);
        LakeShapeNoise = MakeNoise(90, WorldGenerationConfig.LakeShapeFrequency);

        // Mountains wind like ravines and rivers, so a range snakes instead of tracking a contour
        MountainWarp = MakeWarp(87, WorldGenerationConfig.MountainWarpFrequency, WorldGenerationConfig.MountainWarpAmplitude);
    }

    /// <summary>An OpenSimplex2 domain warp seeded from the world seed plus <paramref name="seedOffset"/>.</summary>
    private static FastNoiseLite MakeWarp(int seedOffset, float frequency, float amplitude)
    {
        var warp = new FastNoiseLite(WorldGenerationConfig.Seed + seedOffset);
        warp.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
        warp.SetFrequency(frequency);
        warp.SetDomainWarpAmp(amplitude);
        return warp;
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
}
