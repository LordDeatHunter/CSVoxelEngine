using System;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Chunks;

/// <summary>
/// One chunk column's terrain beyond its blocks: everything a mesh build, the fluid-surface
/// query (<see cref="ChunkManager.GetFluidColumnTopGlobal"/>) and the mining pop read from the
/// terrain. Served by an <see cref="IColumnSource"/>, from worldgen
/// (<see cref="WorldgenColumnSource"/>) or streamed from a server. The arrays are immutable once
/// stored and are replaced, never edited, because pool mesh builds read them concurrently.
/// </summary>
public readonly struct ColumnTerrain
{
    /// <summary>The terrain height of each column, indexed (x &lt;&lt; Chunk.SizeShift) | z,
    /// ravines cut in (see <see cref="ColumnData.Heights"/>).</summary>
    public readonly int[] Heights;

    /// <summary>The y each column's water rises to, indexed like <see cref="Heights"/>, or
    /// <see cref="TerrainGenerator.NoWater"/> where the column holds none.</summary>
    public readonly int[] WaterLevel;

    /// <summary>The <see cref="TerrainGenerator.Climate"/> warmth at the column's minimum corner,
    /// i.e. biome-tint lattice point (chunkX, chunkZ).</summary>
    public readonly float Warmth;

    /// <summary>The <see cref="TerrainGenerator.Climate"/> wetness at the same corner.</summary>
    public readonly float Wetness;

    public ColumnTerrain(int[] heights, int[] waterLevel, float warmth, float wetness)
    {
        Heights = heights;
        WaterLevel = waterLevel;
        Warmth = warmth;
        Wetness = wetness;
    }

    /// <summary>True for <c>default</c>: no column was resolved.</summary>
    public bool IsEmpty => Heights == null;

    /// <summary>
    /// Whether <paramref name="other"/> carries the same terrain. The floats compare by their
    /// bits, so a NaN or a -0 never counts as a change twice.
    /// </summary>
    public bool ContentEquals(in ColumnTerrain other)
    {
        if (IsEmpty || other.IsEmpty) return IsEmpty == other.IsEmpty;
        return Heights.AsSpan().SequenceEqual(other.Heights)
               && WaterLevel.AsSpan().SequenceEqual(other.WaterLevel)
               && BitConverter.SingleToInt32Bits(Warmth) == BitConverter.SingleToInt32Bits(other.Warmth)
               && BitConverter.SingleToInt32Bits(Wetness) == BitConverter.SingleToInt32Bits(other.Wetness);
    }
}
