using System;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Caves;

/// <summary>Overlapping ellipsoids seeded by frozen and desert surface columns, extending the echo material
/// through solid rock in three dimensions. Sites depend only on position and seed; no chunk reads
/// or writes cross a seam. The point probe and chunk map share the exact containment rule.</summary>
internal static class CaveEchoVolumes
{
    private const int Cell = WorldGenerationConfig.CaveEchoVolumeCellBlocks;
    private const int Radius = WorldGenerationConfig.CaveEchoVolumeRadius;
    private const int Vertical = WorldGenerationConfig.CaveEchoVolumeVerticalRadius;
    private readonly record struct Site(int X, int Y, int Z, CaveBiome Biome);
    private static readonly GenerationalCache<(int X, int Y, int Z, int Seed), Site> Sites = new(
        WorldGenerationConfig.CaveEchoVolumeCacheCapacity, WorldGenerationConfig.CaveEchoVolumeCacheGenerations);
    private static readonly Func<(int X, int Y, int Z, int Seed), Site> BuildFor = Build;

    private static Site Build((int X, int Y, int Z, int Seed) key)
    {
        var hash = SplitMix64.Hash(key.X ^ (key.Y * 0x9E37), key.Z, key.Seed ^ 0x5E80);
        var x = key.X * Cell + Cell / 4 + (int)(SplitMix64.Mix(hash) % (Cell / 2));
        var y = key.Y * Cell + Cell / 4 + (int)(SplitMix64.Mix(hash + 1) % (Cell / 2));
        var z = key.Z * Cell + Cell / 4 + (int)(SplitMix64.Mix(hash + 2) % (Cell / 2));
        if (y + Vertical <= WorldGenerationConfig.CaveEchoVolumeBottomY)
            return new Site(x, y, z, CaveBiome.Plain);
        var biome = TerrainGenerator.BiomeAt(x, z) switch
        {
            Biome.Frozen => CaveBiome.FrozenEcho,
            Biome.Desert => CaveBiome.DesertEcho,
            _ => CaveBiome.Plain,
        };
        return new Site(x, y, z, biome);
    }

    private static Site Get(int x, int y, int z) => Sites.GetOrAdd((x, y, z, WorldGenerationConfig.Seed), BuildFor);
    private static int Low(int value, int radius) => MathUtils.FloorToInt((value - radius) / (float)Cell);
    private static int High(int value, int radius) => MathUtils.FloorToInt((value + radius) / (float)Cell);

    private static bool Eligible(int y, int height) => y > WorldGenerationConfig.BarrierTopY
        && y > WorldGenerationConfig.CaveEchoVolumeBottomY
        && y <= height - WorldGenerationConfig.CaveEchoCoverBlocks;

    private static bool Contains(Site site, int x, int y, int z)
    {
        var dx = (x - site.X) / (float)Radius;
        var dy = (y - site.Y) / (float)Vertical;
        var dz = (z - site.Z) / (float)Radius;
        return dx * dx + dy * dy + dz * dz < 1f;
    }

    /// <summary>Applies the expanded volume to one base cave-biome result.</summary>
    internal static CaveBiome At(int x, int y, int z, int height, CaveBiome original)
    {
        if (!Eligible(y, height)) return original;
        for (var cx = Low(x, Radius); cx <= High(x, Radius); cx++)
        for (var cy = Low(y, Vertical); cy <= High(y, Vertical); cy++)
        for (var cz = Low(z, Radius); cz <= High(z, Radius); cz++)
        {
            var site = Get(cx, cy, cz);
            if (site.Biome != CaveBiome.Plain && Contains(site, x, y, z)) return site.Biome;
        }
        return original;
    }

    /// <summary>Expands a chunk's map, enumerating each overlapping site just once.</summary>
    internal static bool Expand(CaveBiomeMap map, ColumnData column, int x0, int y0, int z0)
    {
        if (y0 + Chunk.SizeMask <= WorldGenerationConfig.CaveEchoVolumeBottomY
            || y0 > column.MaxHeight - WorldGenerationConfig.CaveEchoCoverBlocks)
            return map.Present != 1 << (int)CaveBiome.Plain;
        for (var cx = High(x0 + Chunk.SizeMask, Radius); cx >= Low(x0, Radius); cx--)
        for (var cy = High(y0 + Chunk.SizeMask, Vertical); cy >= Low(y0, Vertical); cy--)
        for (var cz = High(z0 + Chunk.SizeMask, Radius); cz >= Low(z0, Radius); cz--)
        {
            var site = Get(cx, cy, cz);
            if (site.Biome == CaveBiome.Plain) continue;
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var height = column.Heights[VoxelCoords.ColumnIndex(x, z)];
                if (Eligible(y0 + y, height) && Contains(site, x0 + x, y0 + y, z0 + z))
                    map.Set(Chunk.Idx(x, y, z), site.Biome);
            }
        }
        map.Recount();
        return map.Present != 1 << (int)CaveBiome.Plain;
    }
}
