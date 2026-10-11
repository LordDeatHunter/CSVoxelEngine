using System;

namespace silkgltriangle;

/// <summary>
/// Cold weather: freezes a cold column's surface water into a walkable ice sheet and lays layered
/// snow on top of whatever solid surface is left. Both read <see cref="TerrainGenerator.ColdWeight"/>,
/// a biome-generation input, so the snow line and the freeze line fade in as a column cools instead
/// of snapping at a biome border. A pure function of (x, z) like the other generators, so neighbours
/// agree without shared state; it runs after the trees and cacti so it only fills cells they left air.
/// </summary>
public static class SnowGenerator
{
    private static FastNoiseLite DriftNoise;

    static SnowGenerator()
    {
        Reseed();
    }

    /// <summary>
    /// Builds the noise from the current <see cref="WorldGenerationConfig.Seed"/>: once from the
    /// static constructor, and again by <see cref="WorldSeed.Apply"/> while no worker thread runs.
    /// </summary>
    internal static void Reseed() => DriftNoise = TerrainGenerator.MakeNoise(89, WorldGenerationConfig.SnowFrequency, octaves: 2);

    /// <summary>
    /// Natural snow depth of the column (x, z) in layers: 0 when it is too warm for snow, otherwise
    /// 1..<see cref="WorldGenerationConfig.SnowMaxDepth"/>, deeper where the drift noise runs high.
    /// This is the depth a bare (state 0) snow cell draws (see <see cref="SnowShape"/>), so a cell
    /// on a mountain cap reads its own height.
    /// </summary>
    public static int DepthAt(float x, float z, int height)
    {
        // SnowAndIceEnabled is a compile-time const, so an early return on it would read as
        // unreachable; a multiplier keeps the toggle a real expression (see BiomeWeights).
        var cold = ColdWeightAt(x, z, height)
                   * (WorldGenerationConfig.SnowAndIceEnabled ? 1f : 0f);
        if (cold <= WorldGenerationConfig.SnowColdThreshold) return 0;

        var amount = (cold - WorldGenerationConfig.SnowColdThreshold)
                     / (1f - WorldGenerationConfig.SnowColdThreshold);
        // Bias the drift upward so even a borderline cold column gets a visible layer or two.
        var drift = 0.4f + 0.6f * (0.5f + 0.5f * DriftNoise.GetNoise(x, z));
        var depth = 1 + (int)(amount * drift * (WorldGenerationConfig.SnowMaxDepth - 1));
        return depth < 1 ? 1 : depth;
    }

    /// <summary>
    /// The coldness the snow rules read at a column: the climate's own
    /// <see cref="TerrainGenerator.ColdWeight"/>, raised by altitude so a peak above
    /// <see cref="WorldGenerationConfig.MountainRockHeight"/> wears snow even in a warm biome —
    /// full snow at <see cref="WorldGenerationConfig.MountainSnowFullHeight"/>. The near stamp,
    /// the drawn depth and the far LOD all read this one function, so their lines agree.
    /// </summary>
    public static float ColdWeightAt(float x, float z, int height)
    {
        var cold = TerrainGenerator.ColdWeight(x, z);
        if (height <= WorldGenerationConfig.MountainRockHeight) return cold;
        var altitude = (height - WorldGenerationConfig.MountainRockHeight)
                       / (float)(WorldGenerationConfig.MountainSnowFullHeight
                                 - WorldGenerationConfig.MountainRockHeight);
        return MathF.Max(cold, MathUtils.Clamp01(altitude));
    }

    /// <summary>
    /// Freezes this chunk's cold surface water to ice and stamps one snow cell per cold column.
    /// Runs on a generation worker thread; writes only this chunk's own cells.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        // The caller gates this on SnowAndIceEnabled, so the body has no toggle of its own.
        var seaBlock = TerrainGenerator.SeaBlock;
        var iceBlock = TerrainGenerator.IceBlock;
        var snowBlock = TerrainGenerator.SnowBlock;

        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var worldX = worldX0 + x;
            var worldZ = worldZ0 + z;
            var local = VoxelCoords.ColumnIndex(x, z);
            var height = column.Heights[local];
            var level = column.WaterLevel[local];
            var cold = ColdWeightAt(worldX, worldZ, height);
            if (cold <= WorldGenerationConfig.SnowColdThreshold) continue;
            int surfaceLy;
            if (height < level)
            {
                // Open water only freezes below the freeze line; a merely cold lake keeps its surface.
                if (cold <= WorldGenerationConfig.FreezeColdThreshold) continue;
                var seaLy = level - worldY0;
                if ((uint)seaLy >= Chunk.Size) continue;
                var wi = Chunk.Idx(x, seaLy, z);
                if (blocks[wi] != seaBlock) continue;
                blocks[wi] = iceBlock;
                surfaceLy = seaLy;
            }
            else
            {
                surfaceLy = height - worldY0;
                if ((uint)surfaceLy >= Chunk.Size) continue;
                var ground = blocks[Chunk.Idx(x, surfaceLy, z)];
                if (ground == BlockType.Air || BlockType.IsFluid(ground)) continue;
            }

            var snowLy = surfaceLy + 1;
            if ((uint)snowLy >= Chunk.Size) continue;
            var si = Chunk.Idx(x, snowLy, z);
            if (blocks[si] != BlockType.Air) continue;
            blocks[si] = snowBlock;
        }

        // Taiga spruce crowns wear snow on their top leaves: a cold column's highest non-air cell
        // with air over it, when it is a leaf, takes a snow cell the way the ground does. The scan
        // stops at the first non-air cell, so a snowed ground (or any other cover) is left alone.
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var height = column.Heights[(x << Chunk.SizeShift) | z];
            if (ColdWeightAt(worldX0 + x, worldZ0 + z, height) <= WorldGenerationConfig.SnowColdThreshold)
                continue;
            for (var ly = Chunk.Size - 2; ly > 0; ly--)
            {
                var i = Chunk.Idx(x, ly, z);
                if (blocks[i] == BlockType.Air) continue;
                if (TreeGenerator.IsLeaf(blocks[i]) && blocks[i + Chunk.Size] == BlockType.Air)
                    blocks[i + Chunk.Size] = snowBlock;
                break;
            }
        }
    }
}
