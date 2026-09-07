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
    private static readonly FastNoiseLite BaseNoise;
    private static readonly FastNoiseLite FlatMask;
    private static readonly FastNoiseLite MeadowNoise;
    private static readonly FastNoiseLite SwellNoise;
    private static readonly FastNoiseLite DirtNoise;
    private static readonly FastNoiseLite RavineRidge;
    private static readonly FastNoiseLite RavineWarp;
    private static readonly FastNoiseLite RavineMask;
    private static readonly FastNoiseLite EntranceNoise;
    private static readonly FastNoiseLite IntensityNoise;

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

    static TerrainGenerator()
    {
        SurfaceBlock = ContentRegistry.RequireBlock("grass").Id;
        SoilBlock = ContentRegistry.RequireBlock("dirt").Id;
        RockBlock = ContentRegistry.RequireBlock("stone").Id;
        SeaBlock = ContentRegistry.RequireBlock("water").Id;

        // The rolling base keeps the original seed and settings exactly, so natural terrain
        // is unchanged wherever the flat mask stays at zero. The mask is rare and
        // low-frequency on purpose: most of the world keeps its old look, and only large
        // patches go flat, with a wide soft blend between.
        BaseNoise = MakeNoise(0, WorldGenerationConfig.BaseFrequency, octaves: 6);
        FlatMask = MakeNoise(50, WorldGenerationConfig.FlatMaskFrequency);
        MeadowNoise = MakeNoise(51, WorldGenerationConfig.MeadowFrequency, octaves: 3);
        SwellNoise = MakeNoise(52, WorldGenerationConfig.SwellFrequency, octaves: 2);

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

    /// <summary>
    /// Whether a column's terrain sits below sea level and is therefore flooded. A column
    /// whose height equals <see cref="WorldGenerationConfig.SeaLevel"/> stays dry: the water
    /// fills up to and including sea level, so its top block would be the shoreline itself.
    /// </summary>
    public static bool IsSubmerged(int height) =>
        WorldGenerationConfig.WaterEnabled && height < WorldGenerationConfig.SeaLevel;

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

                // Natural rolling base (the old terrain character), eased toward gentle meadow
                // rolling only where the rare flat mask rises. Both sides sit around the same
                // mean level, so the wide partial-mask band between them is just softer hills
                // instead of a cliff or a second biome.
                var baseHeight = BaseNoise.GetNoise(worldX, worldZ) * BaseNoise.GetNoise(worldZ, worldX)
                    * WorldGenerationConfig.BaseAmplitude;
                var flat = MathUtils.SmoothStep(
                    WorldGenerationConfig.FlatStart,
                    WorldGenerationConfig.FlatEnd,
                    FlatMask.GetNoise(worldX, worldZ));
                var heightF = baseHeight;
                if (flat > 0f)
                {
                    var plains = MeadowNoise.GetNoise(worldX, worldZ) * WorldGenerationConfig.MeadowAmplitude
                        + SwellNoise.GetNoise(worldX, worldZ) * WorldGenerationConfig.SwellAmplitude;
                    heightF = baseHeight + flat * (plains - baseHeight);
                }
                var height = (int)heightF;

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
