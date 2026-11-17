using System;
using VoxelEngine.WorldGen.Features.Underground;

namespace VoxelEngine.WorldGen.Features;

/// <summary>Roofless Dynamic heightmap canyons, cut only after surface-water planning.</summary>
internal static class TerrainCanyons
{
    internal static float RidgeAt(float x, float z)
    {
        var wx = x;
        var wz = z;
        TerrainNoise.CanyonWarp.DomainWarp(ref wx, ref wz);
        return MathF.Abs(TerrainNoise.CanyonRidgeNoise.GetNoise(wx, wz));
    }

    internal static float SupportAt(float x, float z, float oceanWeight)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic) return 0f;
        var shore = 1f - MathUtils.SmoothStep(WorldGenerationConfig.OceanShoreWeight
            - WorldGenerationConfig.EntranceShoreFade, WorldGenerationConfig.OceanShoreWeight, oceanWeight);
        if (shore <= 0f) return 0f;
        var clear = TerrainRelief.DynamicSpawnClear(x, z);
        if (clear <= 0f) return 0f;
        var highland = TerrainRelief.HighlandWeight(x, z);
        BiomeClassifier.WeightsForOcean(x, z, oceanWeight, out _, out var aridWeight);
        var land = MathF.Max(aridWeight * (1f - BiomeClassifier.ColdWeight(x, z)), highland);
        if (land <= 0f) return 0f;
        return land * shore * clear * MathUtils.SmoothStep(WorldGenerationConfig.CanyonMaskStart,
            WorldGenerationConfig.CanyonMaskEnd, TerrainNoise.CanyonMaskNoise.GetNoise(x, z));
    }

    /// <summary>Additional nonraising cut. The caller's water weight includes the great-lake bank guard.</summary>
    internal static int CutDepth(int x, int z, int height, int waterLevel, float oceanWeight,
        float waterFade)
    {
        if (waterFade <= 0f || height <= WorldGenerationConfig.SeaLevel + WorldGenerationConfig.CanyonMinFloorAboveSea)
            return 0;
        var support = SupportAt(x, z, oceanWeight) * waterFade;
        if (support <= 0f) return 0;
        var ridge = RidgeAt(x, z);
        if (ridge >= WorldGenerationConfig.CanyonOuterRidge) return 0;

        // The slow mask field sets a nearly level floor; the ridged channel's narrow core
        // reads that floor, while its continuous terraced sides return to the original height.
        var maskNoise = TerrainNoise.CanyonMaskNoise.GetNoise(x, z);
        var floor = WorldGenerationConfig.SeaLevel + WorldGenerationConfig.CanyonMinFloorAboveSea
            + 20f * (0.5f + 0.5f * maskNoise);
        floor = MathF.Max(floor, height - WorldGenerationConfig.CanyonMaxDepth);
        if (height - floor < 1f) return 0;
        var wall = MathUtils.SmoothStep(WorldGenerationConfig.CanyonFloorRidge,
            WorldGenerationConfig.CanyonOuterRidge, ridge);
        var rise = TerrainShelves.Terrace(wall * (height - floor),
            WorldGenerationConfig.CanyonWallStepHeight, WorldGenerationConfig.CanyonWallRiserShare);
        var target = floor + rise;
        target = MathUtils.Lerp(target, height, MathUtils.SmoothStep(0.85f, 1f, wall));
        var cut = (int)MathF.Floor(MathF.Max(0f, (height - target) * support));
        if (cut <= 0) return 0;

        // Oasis and spring ponds assign their own water levels in Compute. They do not
        // appear in WaterBodies.WaterLevelAt, so leave their full discs and face banks
        // alone before consulting the shared-water levels below.
        if (TouchesFeatureWater(x, z)) return 0;

        // A dry bank may stand above an elevated pond or ramp. Every face-adjacent
        // planned water level bounds the cut, so it cannot open a lower wall directly
        // into that water. The wider samples and existing weight/guard preserve banks.
        var water = waterLevel;
        water = Math.Max(water, WaterBodies.WaterLevelAt(x - 1, z));
        water = Math.Max(water, WaterBodies.WaterLevelAt(x + 1, z));
        water = Math.Max(water, WaterBodies.WaterLevelAt(x, z - 1));
        water = Math.Max(water, WaterBodies.WaterLevelAt(x, z + 1));
        var radius = WorldGenerationConfig.CanyonWaterGuardRadius;
        water = Math.Max(water, WaterBodies.WaterLevelAt(x - radius, z));
        water = Math.Max(water, WaterBodies.WaterLevelAt(x + radius, z));
        water = Math.Max(water, WaterBodies.WaterLevelAt(x, z - radius));
        water = Math.Max(water, WaterBodies.WaterLevelAt(x, z + radius));
        var dryFloor = WorldGenerationConfig.SeaLevel + WorldGenerationConfig.CanyonMinFloorAboveSea;
        if (water != WaterBodies.NoWater)
            dryFloor = Math.Max(dryFloor, water + WorldGenerationConfig.CanyonWaterBankMargin);
        return Math.Min(cut, Math.Max(0, height - dryFloor));
    }

    private static bool TouchesFeatureWater(int x, int z)
    {
        if (WorldFeatureConfig.OasesEnabled &&
            (OasisGenerator.IsDisc(x, z) || OasisGenerator.IsDisc(x - 1, z)
                || OasisGenerator.IsDisc(x + 1, z) || OasisGenerator.IsDisc(x, z - 1)
                || OasisGenerator.IsDisc(x, z + 1))) return true;

        return WorldFeatureConfig.HotSpringsEnabled &&
            (HotSpringGenerator.IsSite(x, z) || HotSpringGenerator.IsSite(x - 1, z)
                || HotSpringGenerator.IsSite(x + 1, z) || HotSpringGenerator.IsSite(x, z - 1)
                || HotSpringGenerator.IsSite(x, z + 1));
    }
}
