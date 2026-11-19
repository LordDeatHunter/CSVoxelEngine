using System;

namespace VoxelEngine.WorldGen.Ocean;

/// <summary>Stateless, submerged heightmap pinnacles in the uncut deep ocean.</summary>
internal static class OceanPinnacles
{
    private const int Salt = 0x50494E4E; // PINN; separate from sea stacks and other site streams

    /// <summary>Raises one Dynamic ocean column from jittered site discs, never changing Legacy.</summary>
    internal static float TopAt(int x, int z, float incoming, Biome biome, int waterLevel,
        float oceanWeight)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic || biome != Biome.Ocean
            || waterLevel != WorldGenerationConfig.SeaLevel || oceanWeight <= 0.5f
            || WorldGenerationConfig.SeaLevel - incoming <= WorldFeatureConfig.OceanPinnacleMinDepth)
            return incoming;

        var radius = WorldFeatureConfig.OceanPinnacleRadius;
        var shift = WorldFeatureConfig.OceanPinnacleCellShift;
        var minX = (x - radius) >> shift;
        var maxX = (x + radius) >> shift;
        var minZ = (z - radius) >> shift;
        var maxZ = (z + radius) >> shift;
        var best = incoming;
        for (var cx = minX; cx <= maxX; cx++)
        for (var cz = minZ; cz <= maxZ; cz++)
        {
            var h = SplitMix64.Hash(cx, cz, WorldGenerationConfig.Seed ^ Salt);
            if (SplitMix64.Unit(h, 0) >= WorldFeatureConfig.OceanPinnacleChance) continue;
            var span = 1 << shift;
            var centerX = (cx << shift) + (int)(SplitMix64.Mix(h + 1) % (ulong)span);
            var centerZ = (cz << shift) + (int)(SplitMix64.Mix(h + 2) % (ulong)span);
            var topRange = WorldFeatureConfig.OceanPinnacleMaxTop - WorldFeatureConfig.OceanPinnacleMinTop + 1;
            var peak = WorldFeatureConfig.OceanPinnacleMinTop
                       + (int)(SplitMix64.Mix(h + 3) % (ulong)topRange);
            var raised = RaiseForSite(true, true, waterLevel, oceanWeight, incoming,
                x, z, centerX, centerZ, radius, peak);
            if (raised > best) best = raised;
        }
        return best;
    }

    /// <summary>Pure contribution of one site; admission fades to zero before either hard boundary.</summary>
    internal static float RaiseForSite(bool dynamic, bool ocean, int waterLevel, float oceanWeight,
        float incoming, int x, int z, int centerX, int centerZ, int radius, int peak)
    {
        if (!dynamic || !ocean || waterLevel != WorldGenerationConfig.SeaLevel
            || oceanWeight <= 0.5f || radius <= 0)
            return incoming;

        var depth = WorldGenerationConfig.SeaLevel - incoming;
        if (depth <= WorldFeatureConfig.OceanPinnacleMinDepth) return incoming;
        var dx = (long)x - centerX;
        var dz = (long)z - centerZ;
        var squared = dx * dx + dz * dz;
        if (squared >= (long)radius * radius) return incoming;
        var cappedPeak = Math.Min(peak, WorldGenerationConfig.SeaLevel - 4);
        if (cappedPeak <= incoming) return incoming;

        var oceanFade = Smooth(0.5f, WorldFeatureConfig.OceanPinnacleFullOceanWeight, oceanWeight);
        var depthFade = Smooth(WorldFeatureConfig.OceanPinnacleMinDepth,
            WorldFeatureConfig.OceanPinnacleFullDepth, depth);
        var radial = 1f - MathF.Sqrt(squared) / radius;
        return incoming + (cappedPeak - incoming) * oceanFade * depthFade * radial;
    }

    private static float Smooth(float lo, float hi, float value)
    {
        var t = Math.Clamp((value - lo) / (hi - lo), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
