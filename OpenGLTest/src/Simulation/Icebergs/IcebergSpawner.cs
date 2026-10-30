using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;

namespace VoxelEngine.Simulation.Icebergs;

/// <summary>
/// The climate an iceberg spawn reads at world column (<paramref name="x"/>, <paramref name="z"/>)
/// whose ground stands at <paramref name="height"/>: its cold weight and whether its top water is
/// frozen. False means the column has no climate to read, and no berg spawns there.
/// </summary>
public delegate bool IcebergClimate(ChunkManager chunks, int x, int z, int height, out float cold, out bool frozen);

/// <summary>
/// Where a new iceberg may spawn: a ring point around a target over open sea (the stored column is
/// <see cref="Biome.Ocean"/> at <see cref="WorldGenerationConfig.SeaLevel"/>, which rules out rivers
/// and lakes), inside the cold band at the pack-ice edge (<see cref="IcebergSpawnRule"/>), on
/// loaded water deep and wide enough for the whole berg, and clear of every target. Main thread.
/// </summary>
public static class IcebergSpawner
{
    /// <summary>
    /// The default climate: worldgen's cold weight and pack-ice rule
    /// (<see cref="SnowGenerator.ColdWeightAt"/>, <see cref="SnowGenerator.IsFrozenWater"/>). A
    /// column a world source supplies has no worldgen climate, so it reads none.
    /// </summary>
    public static bool WorldgenClimate(ChunkManager chunks, int x, int z, int height, out float cold, out bool frozen)
    {
        cold = 0f;
        frozen = false;
        if (chunks.WorldSource.Supplies(x >> Chunk.SizeShift, z >> Chunk.SizeShift))
            return false;
        cold = SnowGenerator.ColdWeightAt(x, z, height);
        frozen = SnowGenerator.IsFrozenWater(x, z, cold);
        return true;
    }

    /// <summary>
    /// Tries <see cref="IcebergConfig.SpawnTries"/> random points on the spawn ring around
    /// <paramref name="around"/> for a berg of <paramref name="shape"/> at (<paramref name="turns"/>,
    /// <paramref name="scale"/>), returning its waterline <paramref name="center"/> on success.
    /// </summary>
    public static bool TryFindSite(ChunkManager chunks, Vector3 around, IcebergShape shape, int turns, float scale,
        IReadOnlyList<IMobTarget> targets, Random random, out Vector3 center, IcebergClimate? climate = null)
    {
        climate ??= WorldgenClimate;
        for (var attempt = 0; attempt < IcebergConfig.SpawnTries; attempt++)
        {
            var angle = random.NextSingle() * MathF.Tau;
            var radius = IcebergConfig.SpawnMinRadiusBlocks
                         + random.NextSingle() * (IcebergConfig.SpawnMaxRadiusBlocks - IcebergConfig.SpawnMinRadiusBlocks);
            var x = MathUtils.FloorToInt(around.X + MathF.Cos(angle) * radius);
            var z = MathUtils.FloorToInt(around.Z + MathF.Sin(angle) * radius);
            if (TrySiteAt(chunks, x, z, shape, turns, scale, targets, climate, out center))
                return true;
        }

        center = default;
        return false;
    }

    /// <summary>Whether the column (<paramref name="x"/>, <paramref name="z"/>) is a site, and the berg's center there.</summary>
    public static bool TrySiteAt(ChunkManager chunks, int x, int z, IcebergShape shape, int turns, float scale,
        IReadOnlyList<IMobTarget> targets, IcebergClimate climate, out Vector3 center)
    {
        center = default;
        var column = chunks.WorldSource.GetColumn(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        var i = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
        var level = column.WaterLevel[i];
        if (column.Biomes[i] != Biome.Ocean || level != WorldGenerationConfig.SeaLevel || column.Heights[i] >= level)
            return false;
        if (!climate(chunks, x, z, column.Heights[i], out var cold, out var frozen)
            || !IcebergSpawnRule.InBand(cold, frozen))
            return false;
        if (!TryWaterline(chunks, x, level, z, out var waterline))
            return false;

        center = new Vector3(x + 0.5f, waterline, z + 0.5f);
        var (min, max) = shape.PlacedBounds(turns, scale, center);
        if (!IcebergManager.CellsClear(chunks, min, max))
            return false;
        foreach (var target in targets)
            if (HorizontalDistance(target.Eye, min, max) < IcebergConfig.SpawnClearRadiusBlocks)
                return false;
        return true;
    }

    /// <summary>
    /// The water surface of the loaded fluid cell at (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>), or false when that cell is unloaded or holds no fluid.
    /// </summary>
    public static bool TryWaterline(ChunkManager chunks, int x, int y, int z, out float waterline)
    {
        waterline = 0f;
        if (!chunks.TryGetCell(x, y, z, out var id, out _) || !BlockType.IsFluid(id))
            return false;
        waterline = chunks.GetFluidColumnTopGlobal(x, y, z);
        return !float.IsNegativeInfinity(waterline);
    }

    /// <summary>The horizontal distance from <paramref name="point"/> to the box's footprint (0 inside it).</summary>
    public static float HorizontalDistance(Vector3 point, Vector3 min, Vector3 max)
    {
        var dx = MathF.Max(0f, MathF.Max(min.X - point.X, point.X - max.X));
        var dz = MathF.Max(0f, MathF.Max(min.Z - point.Z, point.Z - max.Z));
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
