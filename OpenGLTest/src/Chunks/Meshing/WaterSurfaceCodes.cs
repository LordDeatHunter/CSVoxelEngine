using System;
using VoxelEngine.Data;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.WorldGen;
using static VoxelEngine.Chunks.Chunk;

namespace VoxelEngine.Chunks.Meshing;

/// <summary>
/// GL-free water-surface codes a submerged face keys on: the two-regime 7-bit code the greedy
/// key carries (<see cref="SubmergedSurfaceCode"/>), its decode, and the per-voxel baked
/// <see cref="Vertex.WaterSurface"/> twin.
/// </summary>
internal static class WaterSurfaceCodes
{
    // Two-regime 7-bit code of a submerged face's water surface (SubmergedSurfaceCode):
    // 1..68 quarter blocks inside the padded snapshot, 69..127 whole blocks above its top.
    private const int SurfaceCodeInsideMax = 68;
    private const int SurfaceCodeAboveBase = 69;
    private const int SurfaceCodeMax = 127;

    /// <summary>
    /// The water surface above a submerged face, as the 7-bit code that joins the greedy key
    /// (bits 25-31), so a merge never crosses two different surfaces. It is the same surface
    /// <see cref="ChunkManager.GetFluidColumnTopGlobal"/> finds, resolved from the padded snapshot
    /// alone: the fluid column is walked up from <paramref name="fluidPIdx"/> (the fluid cell the
    /// face looks into). A column that tops out inside the snapshot codes its surface in quarter
    /// blocks (1..68). One that leaves the snapshot's top row still in fluid reads the column's
    /// <c>WaterLevel</c> from the build's resolved <see cref="ColumnTerrain"/>
    /// (<paramref name="columns"/>, the 3x3 in <c>dx * 3 + dz</c> order, worldgen or streamed) and
    /// codes whole blocks above the snapshot top (69..127, clamped ~58 blocks above it, where the
    /// caustic fade is already ~0.01); a column whose level is lower than that (a player-built
    /// tower) caps at the snapshot top. <see cref="DecodeSurfaceCode"/> turns the code back into
    /// a world Y.
    /// </summary>
    internal static int SubmergedSurfaceCode(byte[] padded, byte[] paddedState, int fluidPIdx,
        int chunkBaseY, ColumnTerrain[] columns)
    {
        // ChunkMeshBuilder.FillPaddedSnapshot's layout: px * PaddedSq + py * Padded + pz (z innermost)
        var px = fluidPIdx / PaddedSq;
        var py = fluidPIdx % PaddedSq / Padded;
        var pz = fluidPIdx % Padded;

        var top = fluidPIdx;
        while (py < Padded - 1 && BlockType.IsFluid(padded[top + Padded]))
        {
            top += Padded;
            py++;
        }

        if (py < Padded - 1)
        {
            // The column's top cell lies inside the snapshot: its surface in chunk-local Y
            var ls = (py - 1) + FluidState.SurfaceHeight(paddedState[top]);
            return Math.Clamp((int)MathF.Round((ls + 1f) * 4f), 1, SurfaceCodeInsideMax);
        }

        // Still fluid in the snapshot's top row: the surface lies above what this build sees.
        // Arithmetic shift: padded 0 is the -X/-Z neighbor column (-1 >> 4 = -1), 1..16 this
        // one, 17 the +X/+Z one; the mask then picks the cell inside that column.
        var dx = ((px - 1) >> SizeShift) + 1;
        var dz = ((pz - 1) >> SizeShift) + 1;
        var level = columns[dx * 3 + dz].WaterLevel[(((px - 1) & SizeMask) << SizeShift) | ((pz - 1) & SizeMask)];
        var cap = chunkBaseY + Padded - 1;
        var surface = level != TerrainGenerator.NoWater && level + FluidState.SourceHeight > cap
            ? level + FluidState.SourceHeight
            : cap;
        return SurfaceCodeAboveBase
               + Math.Clamp((int)MathF.Round(surface - cap), 0, SurfaceCodeMax - SurfaceCodeAboveBase);
    }

    /// <summary>World Y of the water surface a <see cref="SubmergedSurfaceCode"/> code stands
    /// for, in a chunk whose lowest block row is <paramref name="chunkBaseY"/>.</summary>
    internal static float DecodeSurfaceCode(int chunkBaseY, int code) =>
        code <= SurfaceCodeInsideMax
            ? chunkBaseY + code / 4f - 1f
            : chunkBaseY + Padded - 1 + (code - SurfaceCodeAboveBase);

    /// <summary>The baked <see cref="Vertex.WaterSurface"/> of a face looking into the fluid
    /// cell at <paramref name="fluidPIdx"/>; the per-voxel twin of the greedy key's code.</summary>
    internal static ushort BakedWaterSurface(byte[] padded, byte[] paddedState, int fluidPIdx,
        int chunkBaseY, ColumnTerrain[] columns) =>
        Vertex.EncodeWaterSurface(DecodeSurfaceCode(chunkBaseY,
            SubmergedSurfaceCode(padded, paddedState, fluidPIdx, chunkBaseY, columns)));
}
