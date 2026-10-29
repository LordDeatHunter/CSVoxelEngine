using System;
using System.Numerics;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The ocean-gated island term of BIOMEGEN 13.2: a 0..1 island land weight that
/// <see cref="BiomeClassifier.BiomeWeights(float, float, out float, out float, out float)"/> folds into the
/// ocean weight as oceanWeight * (1 - island). Two noise channels rise out of deep water, a large one
/// (<see cref="TerrainNoise.IslandNoise"/>) and a small one (<see cref="TerrainNoise.IslandSmallNoise"/>), and a
/// stateless hashed archipelago footprint (one elongated ellipse per
/// <see cref="WorldGenerationConfig.ArchipelagoCellBlocks"/> cell) lowers the small channel's threshold and raises
/// its gain, so islets string out into chains. A pure function of (x, z) and the seed, thread-safe, and it reads
/// no height, so no site factory that reads the ocean weight can recurse through it. Exactly 0 wherever the
/// pre-island ocean weight is at or below <see cref="WorldGenerationConfig.OceanShoreWeight"/> or
/// <see cref="TerrainRelief.SpawnClear"/> is 0, so land, the coast band and the spawn keep their bits.
/// </summary>
internal static class TerrainIslands
{
    private const int ArchipelagoSalt = 0x41524348; // "ARCH"

    /// <summary>
    /// The archipelago candidate of cell (<paramref name="gx"/>, <paramref name="gz"/>): false when the cell's
    /// roll misses (every out zeroed), else an ellipse centered inside the cell with its half length along the
    /// unit <paramref name="direction"/> and its half width across it.
    /// </summary>
    internal static bool TryGetArchipelago(int gx, int gz, out float centerX, out float centerZ, out float halfLength,
        out float halfWidth, out Vector2 direction)
    {
        if (!TerrainSites.TryRoll(gx, gz, WorldGenerationConfig.ArchipelagoCellBlocks, ArchipelagoSalt,
                WorldGenerationConfig.ArchipelagoChance, WorldGenerationConfig.ArchipelagoSizeSkew,
                out var hash, out centerX, out centerZ, out var size))
        {
            halfLength = 0f;
            halfWidth = 0f;
            direction = Vector2.Zero;
            return false;
        }

        halfLength = MathUtils.Lerp(WorldGenerationConfig.ArchipelagoMinHalfLength,
            WorldGenerationConfig.ArchipelagoMaxHalfLength, size);
        var angle = SplitMix64.Unit(hash, 4) * MathF.Tau;
        direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        halfWidth = halfLength * MathUtils.Lerp(WorldGenerationConfig.ArchipelagoMinAspect,
            WorldGenerationConfig.ArchipelagoMaxAspect, SplitMix64.Unit(hash, 5));
        return true;
    }

    /// <summary>
    /// 0..1 how deep (x, z) lies in the archipelago footprint of cell (<paramref name="gx"/>,
    /// <paramref name="gz"/>): 1 inside the core share of the ellipse, easing to 0 at its rim, 0 outside it or
    /// when the cell rolls no archipelago.
    /// </summary>
    internal static float FootprintWeight(int gx, int gz, float x, float z)
    {
        if (!TryGetArchipelago(gx, gz, out var centerX, out var centerZ, out var halfLength, out var halfWidth,
                out var direction))
            return 0f;

        var dx = x - centerX;
        var dz = z - centerZ;
        var d2 = dx * dx + dz * dz;
        if (d2 >= halfLength * halfLength) return 0f;

        var along = dx * direction.X + dz * direction.Y;
        var across = -dx * direction.Y + dz * direction.X;
        var u = along / halfLength;
        var v = across / halfWidth;
        var e = MathF.Sqrt(u * u + v * v);
        if (e >= 1f) return 0f;

        return 1f - MathUtils.SmoothStep(WorldGenerationConfig.ArchipelagoCoreShare, 1f, e);
    }

    /// <summary>0..1 the strongest archipelago footprint over (x, z): the max of
    /// <see cref="FootprintWeight"/> over every cell that can reach it, so overlapping footprints combine by
    /// max.</summary>
    internal static float ArchipelagoWeight(float x, float z)
    {
        // A multiplier, not an early return: the flag is a compile-time const.
        var enabled = WorldGenerationConfig.ArchipelagosEnabled ? 1f : 0f;
        var cellBlocks = WorldGenerationConfig.ArchipelagoCellBlocks;
        TerrainSites.CellRange(x, cellBlocks, WorldGenerationConfig.ArchipelagoMaxHalfLength, out var minX,
            out var maxX);
        TerrainSites.CellRange(z, cellBlocks, WorldGenerationConfig.ArchipelagoMaxHalfLength, out var minZ,
            out var maxZ);
        var best = 0f;
        for (var gx = minX; gx <= maxX; gx++)
        for (var gz = minZ; gz <= maxZ; gz++)
        {
            var w = FootprintWeight(gx, gz, x, z);
            if (w > best) best = w;
        }

        return best * enabled;
    }

    /// <summary>
    /// 0..1 how much island land rises at (x, z) out of an ocean of pre-island weight
    /// <paramref name="oceanWeight"/>. Exactly 0 at or below <see cref="WorldGenerationConfig.OceanShoreWeight"/>
    /// and wherever <see cref="TerrainRelief.SpawnClear"/> is 0, both checked before any noise read or hash, so
    /// land pays one compare. Nondecreasing in <paramref name="oceanWeight"/>.
    /// </summary>
    internal static float LandWeight(float x, float z, float oceanWeight)
    {
        if (oceanWeight <= WorldGenerationConfig.OceanShoreWeight) return 0f;
        var clear = TerrainRelief.SpawnClear(x, z);
        if (clear <= 0f) return 0f;
        return Land(x, z, oceanWeight, clear, ArchipelagoWeight(x, z));
    }

    /// <summary>
    /// <see cref="LandWeight(float, float, float)"/> with a given 0..1 archipelago weight in place of
    /// <see cref="ArchipelagoWeight"/>: the same guards, a seam for the tests. Nondecreasing in
    /// <paramref name="archipelago"/>.
    /// </summary>
    internal static float LandWeight(float x, float z, float oceanWeight, float archipelago)
    {
        if (oceanWeight <= WorldGenerationConfig.OceanShoreWeight) return 0f;
        var clear = TerrainRelief.SpawnClear(x, z);
        if (clear <= 0f) return 0f;
        return Land(x, z, oceanWeight, clear, archipelago);
    }

    /// <summary>The island weight past the guards: the stronger of the two channels, scaled by how far past
    /// the shore band the ocean lies and by the spawn clearing.</summary>
    private static float Land(float x, float z, float oceanWeight, float clear, float archipelago)
    {
        var enabled = WorldGenerationConfig.IslandsEnabled ? 1f : 0f;
        var deep = MathUtils.SmoothStep(WorldGenerationConfig.OceanShoreWeight, 1f, oceanWeight);
        var large = MathUtils.SmoothStep(WorldGenerationConfig.IslandLargeStart, WorldGenerationConfig.IslandLargeEnd,
            TerrainNoise.IslandNoise.GetNoise(x, z)) * WorldGenerationConfig.IslandLargeGain;
        var drop = WorldGenerationConfig.ArchipelagoThresholdDrop * archipelago;
        var small = MathUtils.SmoothStep(WorldGenerationConfig.IslandSmallStart - drop,
                        WorldGenerationConfig.IslandSmallEnd - drop, TerrainNoise.IslandSmallNoise.GetNoise(x, z))
                    * MathUtils.Lerp(WorldGenerationConfig.IslandSmallGain, WorldGenerationConfig.ArchipelagoSmallGain,
                        archipelago);
        return MathF.Min(1f, MathF.Max(large, small) * deep * clear * enabled);
    }
}
