using System;

namespace VoxelEngine.WorldGen.Features;

/// <summary>Broad Dynamic plateau lifts with narrow, continuous escarpment risers.</summary>
internal static class TerrainShelves
{
    /// <summary>Extra forest-profile lift before valley compression and the whole-profile ceiling.</summary>
    internal static float HeightDelta(float x, float z, float highland, float existingLift)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic || highland <= 0f) return 0f;
        var clear = TerrainRelief.DynamicSpawnClear(x, z);
        if (clear <= 0f) return 0f;
        var noise = TerrainNoise.ShelfNoise.GetNoise(x, z);
        var mask = highland * clear * MathUtils.SmoothStep(WorldGenerationConfig.ShelfMaskStart,
            WorldGenerationConfig.ShelfMaskEnd, noise);
        if (mask <= 0f) return 0f;

        var broadLift = existingLift + WorldGenerationConfig.ShelfPlateauLift * (0.5f + 0.5f * noise);
        var stepped = Terrace(broadLift, WorldGenerationConfig.ShelfStepHeight,
            WorldGenerationConfig.ShelfRiserShare);
        return mask * MathF.Max(0f, stepped - existingLift);
    }

    /// <summary>Continuous stair profile; each riser occupies the last share of its step.</summary>
    internal static float Terrace(float value, float stepHeight, float riserShare)
    {
        if (value <= 0f) return 0f;
        var scaled = value / stepHeight;
        var step = MathF.Floor(scaled);
        var fraction = scaled - step;
        return (step + MathUtils.SmoothStep(1f - riserShare, 1f, fraction)) * stepHeight;
    }
}
