using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Shapes;
using VoxelEngine.WorldGen.Features.Underground;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Cold weather: freezes a cold column's surface water into a walkable ice sheet and lays layered
/// snow on top of whatever solid surface is left. Both read <see cref="TerrainGenerator.ColdWeight"/>,
/// a biome-generation input, so the snow line fades in as a column cools instead of snapping at a
/// biome border, and freezing runs over a pack-ice band (<see cref="IsFrozenWater"/>): ragged floes
/// and open leads from <see cref="WorldGenerationConfig.PackIceColdStart"/> that thicken into a
/// solid sheet past <see cref="WorldGenerationConfig.FreezeColdThreshold"/>. A pure function of
/// (x, z) like the other generators, so neighbors agree without shared state; it runs after the
/// trees and cacti so it only fills cells they left air.
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
        if (!HasSnow(x, z, cold)) return 0;

        var amount = (cold - WorldGenerationConfig.SnowColdThreshold)
                     / (1f - WorldGenerationConfig.SnowColdThreshold);
        // Bias the drift upward so even a borderline cold column gets a visible layer or two.
        var drift = 0.4f + 0.6f * (0.5f + 0.5f * DriftNoise.GetNoise(x, z));
        var depth = 1 + (int)(amount * drift * (WorldGenerationConfig.SnowMaxDepth - 1));
        return depth < 1 ? 1 : depth;
    }

    /// <summary>
    /// Whether snow settles on the column (x, z) at coldness <paramref name="cold"/>: never at or below
    /// <see cref="WorldGenerationConfig.SnowColdThreshold"/>, always from
    /// <see cref="WorldGenerationConfig.SnowFullColdThreshold"/> up, and in between a coherent noise against a
    /// smoothstep of the cold, so the snow line is a ragged band of drifts and bare patches rather than a hard
    /// edge, the way the pack-ice edge breaks into floes. Monotone in the cold, pure, safe on any thread; the
    /// near stamp, the drawn depth and the far LOD all call it.
    /// </summary>
    public static bool HasSnow(float x, float z, float cold)
    {
        // SnowAndIceEnabled is a compile-time const (see DepthAt): a multiplier keeps the toggle real
        var c = cold * (WorldGenerationConfig.SnowAndIceEnabled ? 1f : 0f);
        if (c <= WorldGenerationConfig.SnowColdThreshold) return false;
        if (c >= WorldGenerationConfig.SnowFullColdThreshold) return true;
        // Sampled far from the floe field's own coordinates so the two patterns never line up
        var noise = 0.5f + 0.5f * TerrainNoise.PackIceNoise.GetNoise(x + 4096f, z - 4096f);
        return noise < MathUtils.SmoothStep(WorldGenerationConfig.SnowColdThreshold,
            WorldGenerationConfig.SnowFullColdThreshold, c);
    }
    /// <summary>
    /// The coldness the snow rules read at a column: the climate's own
    /// <see cref="TerrainGenerator.ColdWeight"/>, raised by altitude so a peak above
    /// <see cref="WorldGenerationConfig.MountainRockHeight"/> wears snow even in a warm biome —
    /// full snow at <see cref="WorldGenerationConfig.MountainSnowFullHeight"/>. The near stamp,
    /// the drawn depth and the far LOD all read this one function, and both feed it to
    /// <see cref="IsFrozenWater"/>, so their snow and ice lines agree.
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
    /// Whether the surface water of the column (x, z) freezes, given that column's
    /// <see cref="ColdWeightAt"/>: open water at or below
    /// <see cref="WorldGenerationConfig.PackIceColdStart"/>, a solid sheet past
    /// <see cref="WorldGenerationConfig.FreezeColdThreshold"/>, and in between the coherent floe
    /// noise against a smoothstep of the cold, so the ice share rises with the cold. A hot spring
    /// never freezes. A pure, stateless function of (x, z, seed), safe on any thread: the near
    /// stamp and the generated far LOD both call it, so the two agree exactly.
    /// </summary>
    public static bool IsFrozenWater(float x, float z, float cold)
    {
        // SnowAndIceEnabled is a compile-time const, so an early return on it would read as
        // unreachable; a multiplier keeps the toggle a real expression (see DepthAt).
        if (cold * (WorldGenerationConfig.SnowAndIceEnabled ? 1f : 0f) <= WorldGenerationConfig.PackIceColdStart)
            return false;

        // For a fixed column the floe value is fixed and the smoothstep only rises with the cold,
        // so the result is monotone in cold: ice that forms stays as the column gets colder.
        var frozen = cold > WorldGenerationConfig.FreezeColdThreshold
                     || 0.5f + 0.5f * TerrainNoise.PackIceNoise.GetNoise(x, z)
                     < MathUtils.SmoothStep(WorldGenerationConfig.PackIceColdStart,
                         WorldGenerationConfig.FreezeColdThreshold, cold);
        if (!frozen) return false;

        // Last, and only where ice would form: the site test is the costly one (climate and land
        // height reads), and a hot spring keeps its own warm surface however cold it gets.
        return !(WorldFeatureConfig.HotSpringsEnabled && HotSpringGenerator.IsSite((int)x, (int)z));
    }

    /// <summary>
    /// Freezes this chunk's cold surface water to ice and stamps one snow cell per cold column on
    /// the ground or the ice. Runs on a generation worker thread; writes only this chunk's own
    /// cells. Runs before <see cref="StampCanopy"/>, which must see the snowed ground.
    /// </summary>
    public static void StampGround(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
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
            // Every test before a write is pure, so the order is free: the chunk-local cell tests
            // come first and the noise reads (the cold, the pack ice) wait for a column they admit.
            int surfaceLy;
            if (height < level)
            {
                var seaLy = level - worldY0;
                if ((uint)seaLy >= Chunk.Size) continue;
                var wi = Chunk.Idx(x, seaLy, z);
                if (blocks[wi] != seaBlock) continue;
                var cold = ColdWeightAt(worldX, worldZ, height);
                // Open water (a lead in the pack ice, or a hot spring) keeps its surface and no snow
                if (!IsFrozenWater(worldX, worldZ, cold)) continue;
                blocks[wi] = iceBlock;
                surfaceLy = seaLy;
            }
            else
            {
                surfaceLy = height - worldY0;
                if ((uint)surfaceLy >= Chunk.Size) continue;
                var ground = blocks[Chunk.Idx(x, surfaceLy, z)];
                if (ground == BlockType.Air || BlockType.IsFluid(ground)) continue;
                var groundSnowLy = surfaceLy + 1;
                if ((uint)groundSnowLy >= Chunk.Size) continue;
                if (blocks[Chunk.Idx(x, groundSnowLy, z)] != BlockType.Air) continue;
                if (!HasSnow(worldX, worldZ, ColdWeightAt(worldX, worldZ, height))) continue;
            }

            var snowLy = surfaceLy + 1;
            if ((uint)snowLy >= Chunk.Size) continue;
            var si = Chunk.Idx(x, snowLy, z);
            if (blocks[si] != BlockType.Air) continue;
            blocks[si] = snowBlock;
        }
    }

    /// <summary>
    /// The world-y band <see cref="StampGround"/> can write (see <see cref="StampBand"/>). In the
    /// water branch ice goes at <c>level</c> and snow at <c>level + 1</c>, where
    /// <c>level &gt; height &gt;= MinHeight</c> and <c>level &lt;= MaxWaterLevel</c>; in the land
    /// branch snow goes at <c>height + 1</c>, with <c>MinHeight &lt;= height &lt;= MaxHeight</c>.
    /// <c>MaxWaterLevel</c> may be <see cref="TerrainGenerator.NoWater"/> (int.MinValue); the max
    /// with <c>MaxHeight</c> keeps the band sane and never subtracts from it.
    /// </summary>
    internal static StampBand GroundWriteBand(ColumnData column) =>
        new(column.MinHeight + 1, Math.Max(column.MaxHeight, column.MaxWaterLevel) + 1);

    /// <summary>
    /// Taiga spruce crowns wear snow on their top leaves: a cold column's highest non-air cell
    /// with air over it, when it is a leaf, takes a snow cell the way the ground does. The scan
    /// stops at the first non-air cell, so a snowed ground (or any other cover) is left alone.
    /// Runs after <see cref="StampGround"/>; writes only this chunk's own cells. Never gated by a
    /// band: its leaves can come from trees rooted in neighbor columns.
    /// </summary>
    public static void StampCanopy(byte[] blocks, ColumnData column, int worldX0, int worldZ0)
    {
        var snowBlock = TerrainGenerator.SnowBlock;

        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            // The scan stops at the first non-air cell whatever the cold, and the cold read is
            // pure, so the noise read waits for a column whose top cell is a leaf under air.
            var top = -1;
            for (var ly = Chunk.Size - 2; ly > 0; ly--)
            {
                var i = Chunk.Idx(x, ly, z);
                if (blocks[i] == BlockType.Air) continue;
                top = i;
                break;
            }

            if (top < 0) continue;
            if (!TreeGenerator.IsLeaf(blocks[top]) || blocks[top + Chunk.Size] != BlockType.Air) continue;
            var height = column.Heights[(x << Chunk.SizeShift) | z];
            if (!HasSnow(worldX0 + x, worldZ0 + z, ColdWeightAt(worldX0 + x, worldZ0 + z, height)))
                continue;
            blocks[top + Chunk.Size] = snowBlock;
        }
    }
}
