using System;
using System.Threading;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen;

/// <summary>Pure Dynamic desert field and the whole-column bare sand-sea verdict.</summary>
internal static class TerrainErg
{
    private static readonly GenerationalCache<(int X, int Z), bool> CoreCache =
        new(WorldGenerationConfig.ErgCoreCacheCapacity, WorldGenerationConfig.ErgCoreCacheGenerations);
    private static readonly Func<(int X, int Z), bool> BuildCoreFor = static key => BuildCore(key.X, key.Z);
    private static long _coreBuilds;
    private static long _storedBiomeReads;

    /// <summary>Diagnostic misses and classifier reads, not part of the core verdict.</summary>
    internal static (long Builds, long Biomes) CoreReadCounts =>
        (Volatile.Read(ref _coreBuilds), Volatile.Read(ref _storedBiomeReads));

    internal static void ResetCache()
    {
        CoreCache.Clear();
        Interlocked.Exchange(ref _coreBuilds, 0);
        Interlocked.Exchange(ref _storedBiomeReads, 0);
    }

    internal static float Weight(float x, float z, float desertWeight)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic || desertWeight <= 0f) return 0f;
        return desertWeight * MathUtils.SmoothStep(WorldGenerationConfig.ErgStart,
            WorldGenerationConfig.ErgFull, TerrainNoise.ErgNoise.GetNoise(x, z));
    }

    // The wind coordinate rises gently for most of a wavelength and drops rapidly on its slip
    // face. Warped ridges keep the face aligned locally without a traversed-random site state.
    internal static float DuneDelta(float x, float z, float weight)
    {
        if (weight <= 0f) return 0f;
        var wind = x * 0.8f + z * 0.6f;
        var warp = TerrainNoise.ErgRidgeNoise.GetNoise(x, z) * WorldGenerationConfig.ErgRidgeWarp;
        var phase = (wind + warp) / WorldGenerationConfig.ErgDuneWavelength;
        phase -= MathF.Floor(phase);
        var crest = phase < WorldGenerationConfig.ErgStossShare
            ? phase / WorldGenerationConfig.ErgStossShare
            : (1f - phase) / (1f - WorldGenerationConfig.ErgStossShare);
        return weight * TerrainRelief.DynamicSpawnClear(x, z)
            * WorldGenerationConfig.ErgDuneAmplitude * (crest - 0.5f);
    }

    internal static bool IsCore(int chunkX, int chunkZ) =>
        WorldSeed.Profile == WorldGenProfile.Dynamic && CoreCache.GetOrAdd((chunkX, chunkZ), BuildCoreFor);

    /// <summary>Whether any complete core chunk column touches an inclusive footprint.</summary>
    internal static bool Intersects(int minX, int minZ, int maxX, int maxZ)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic) return false;
        for (var cx = minX >> Chunk.SizeShift; cx <= maxX >> Chunk.SizeShift; cx++)
        for (var cz = minZ >> Chunk.SizeShift; cz <= maxZ >> Chunk.SizeShift; cz++)
            if (IsCore(cx, cz)) return true;
        return false;
    }

    /// <summary>Whether a circular surface site reaches any cell of a complete core column.</summary>
    internal static bool IntersectsDisc(int x, int z, int radius)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic) return false;
        for (var cx = (x - radius) >> Chunk.SizeShift; cx <= (x + radius) >> Chunk.SizeShift; cx++)
        for (var cz = (z - radius) >> Chunk.SizeShift; cz <= (z + radius) >> Chunk.SizeShift; cz++)
        {
            if (!IsCore(cx, cz)) continue;
            var minX = (long)cx << Chunk.SizeShift;
            var minZ = (long)cz << Chunk.SizeShift;
            var dx = Math.Max(minX - x, Math.Max(0, (long)x - minX - Chunk.Size + 1));
            var dz = Math.Max(minZ - z, Math.Max(0, (long)z - minZ - Chunk.Size + 1));
            if (dx * dx + dz * dz <= (long)radius * radius) return true;
        }
        return false;
    }

    private static bool BuildCore(int chunkX, int chunkZ)
    {
        Interlocked.Increment(ref _coreBuilds);
        var x0 = chunkX << Chunk.SizeShift;
        var z0 = chunkZ << Chunk.SizeShift;
        BiomeClassifier.BiomeWeights(x0 + Chunk.Size / 2, z0 + Chunk.Size / 2,
            out _, out var desert);
        if (Weight(x0 + Chunk.Size / 2, z0 + Chunk.Size / 2, desert) < WorldGenerationConfig.ErgCoreThreshold)
            return false;
        for (var x = x0; x < x0 + Chunk.Size; x++)
        for (var z = z0; z < z0 + Chunk.Size; z++)
        {
            Interlocked.Increment(ref _storedBiomeReads);
            var biome = BiomeClassifier.BiomeWeights(x, z, out var ocean, out var desertWeight);
            if (BiomeClassifier.DitherBiome(x, z, biome, ocean, desertWeight) != Biome.Desert)
                return false;
        }
        return true;
    }
}
