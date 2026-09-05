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
    private static readonly FastNoiseLite HeightNoise;
    private static readonly FastNoiseLite DirtNoise;
    private static readonly FastNoiseLite RavineRidge;
    private static readonly FastNoiseLite RavineWarp;
    private static readonly FastNoiseLite RavineMask;
    private static readonly FastNoiseLite EntranceNoise;
    private static readonly FastNoiseLite IntensityNoise;

    static TerrainGenerator()
    {
        // Base terrain keeps the original seed and settings exactly, so existing terrain
        // is unchanged wherever no ravine touches it
        HeightNoise = MakeNoise(0, 0.001f, octaves: 6);

        DirtNoise = MakeNoise(11, WorldGenerationConfig.DirtDepthFrequency);
        RavineRidge = MakeNoise(21, WorldGenerationConfig.RavineRidgeFrequency);
        RavineMask = MakeNoise(23, WorldGenerationConfig.RavineMaskFrequency);
        EntranceNoise = MakeNoise(31, WorldGenerationConfig.EntranceFrequency);
        IntensityNoise = MakeNoise(41, WorldGenerationConfig.CaveIntensityFrequency);

        // Displaces the ridge lookup so ravines wind instead of following straight noise contours
        RavineWarp = new FastNoiseLite(WorldGenerationConfig.Seed + 22);
        RavineWarp.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
        RavineWarp.SetFrequency(WorldGenerationConfig.RavineWarpFrequency);
        RavineWarp.SetDomainWarpAmp(WorldGenerationConfig.RavineWarpAmplitude);
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

    /// <summary>Computes every 2D input for the chunk column at (chunkX, chunkZ).</summary>
    public static ColumnData Compute(int chunkX, int chunkZ)
    {
        const int count = Chunk.Size * Chunk.Size;
        var heights = new int[count];
        var dirtDepth = new byte[count];
        var entrance = new float[count];
        var intensity = new float[count];

        var baseX = (float)chunkX * Chunk.Size;
        var baseZ = (float)chunkZ * Chunk.Size;

        for (var x = 0; x < Chunk.Size; x++)
        {
            var worldX = x + baseX;
            for (var z = 0; z < Chunk.Size; z++)
            {
                var worldZ = z + baseZ;
                var i = (x << Chunk.SizeShift) | z;

                var heightNoise = HeightNoise.GetNoise(worldX, worldZ) * HeightNoise.GetNoise(worldZ, worldX);
                var height = (int)(heightNoise * 256f);

                // Sampled once and reused: the same value cuts the ravine into the heightmap and
                // decides how much soil is left on the column
                var ravineDepth = WorldGenerationConfig.RavinesEnabled ? RavineDepthAt(worldX, worldZ) : 0;

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

                dirtDepth[i] = (byte)soil;

                entrance[i] = MathUtils.SmoothStep(
                    WorldGenerationConfig.EntranceStart,
                    WorldGenerationConfig.EntranceEnd,
                    EntranceNoise.GetNoise(worldX, worldZ));

                intensity[i] = IntensityNoise.GetNoise(worldX, worldZ) * WorldGenerationConfig.CaveIntensityRange;
            }
        }

        return new ColumnData(heights, dirtDepth, entrance, intensity);
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
