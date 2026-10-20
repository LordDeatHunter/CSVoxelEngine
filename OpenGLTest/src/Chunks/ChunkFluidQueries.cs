using System;
using System.Numerics;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Chunks;

/// <summary>World fluid-surface and flow queries over a <see cref="ChunkManager"/>'s loaded cells, which forwards them; they read only its cells and <see cref="ChunkManager.ColumnSource"/>. Main thread, like every chunk-table read.</summary>
internal static class ChunkFluidQueries
{
    /// <summary>
    /// World Y of the fluid surface in the cell (x, y, z): <c>y + 1</c> when another fluid cell
    /// sits directly above (stacked water has no seam), otherwise <c>y</c> plus the cell's own
    /// <see cref="FluidState.SurfaceHeight"/>. Returns <see cref="float.NegativeInfinity"/>
    /// when the cell holds no fluid, so callers can compare against it unconditionally.
    /// </summary>
    public static float GetFluidSurfaceGlobal(ChunkManager manager, int x, int y, int z)
    {
        if (!manager.TryGetCell(x, y, z, out var id, out var state) || !BlockType.IsFluid(id))
            return float.NegativeInfinity;

        if (manager.TryGetCell(x, y + 1, z, out var above, out _) && BlockType.IsFluid(above))
            return y + 1f;

        return y + FluidState.SurfaceHeight(state);
    }

    /// <summary>
    /// World Y of the real surface of the fluid column the cell (x, y, z) belongs to, or
    /// <see cref="float.NegativeInfinity"/> when the cell is not a loaded fluid cell.
    /// <see cref="GetFluidSurfaceGlobal"/> only resolves one cell up; this walks the column to its
    /// top (at most <see cref="WaterRenderConfig.FluidSurfaceSearchCells"/> cells, reading the
    /// cap's upper edge beyond that) and, when the walk ends under a solid ceiling (an overhang,
    /// a placed block, a flooded sea cave), sees through it to the column's water level from
    /// <see cref="ChunkManager.ColumnSource"/> (generated, or streamed from the server), so the rays under a
    /// ledge still march against the sea or lake above it. A client whose column has not arrived
    /// sees only what it walked.
    /// </summary>
    public static float GetFluidColumnTopGlobal(ChunkManager manager, int x, int y, int z)
    {
        if (!manager.TryGetCell(x, y, z, out var id, out _) || !BlockType.IsFluid(id))
            return float.NegativeInfinity;

        var top = y;
        var hitCap = true;
        var ceiling = false;
        for (var step = 0; step < WaterRenderConfig.FluidSurfaceSearchCells; step++)
        {
            if (manager.TryGetCell(x, top + 1, z, out var above, out _))
            {
                if (BlockType.IsFluid(above))
                {
                    top++;
                    continue;
                }

                ceiling = BlockType.IsSolid(above);
            }

            hitCap = false;
            break;
        }

        var walked = hitCap ? top + 1f : GetFluidSurfaceGlobal(manager, x, top, z);
        if (!ceiling)
            return walked;

        if (!manager.ColumnSource.TryGet(x >> Chunk.SizeShift, z >> Chunk.SizeShift, out var column))
            return walked;
        var level = column.WaterLevel[((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];
        if (level == TerrainGenerator.NoWater)
            return walked;

        return MathF.Max(walked, level + FluidState.SourceHeight);
    }

    // The live world as a FluidFlow cell reader: unloaded chunks report no cell
    private readonly struct LiveCells(ChunkManager m) : IFluidCellReader
    {
        public bool TryGetCell(int x, int y, int z, out byte id, out byte state) =>
            m.TryGetCell(x, y, z, out id, out state);
    }

    /// <summary>
    /// Unit-length horizontal direction the fluid in the cell (x, y, z) flows, or
    /// <see cref="Vector3.Zero"/> when the cell holds no fluid or the fluid is still: the
    /// normalized <see cref="FluidFlow.Raw"/> (Minecraft's <c>FlowingFluid.getFlow</c>).
    /// </summary>
    public static Vector3 GetFluidFlowGlobal(ChunkManager manager, int x, int y, int z) =>
        FluidFlow.Raw(new LiveCells(manager), x, y, z).NormalizeSafe();

    /// <summary>The un-normalized <see cref="FluidFlow.Raw"/> flow at (x, y, z).</summary>
    internal static Vector3 GetFluidFlowRawGlobal(ChunkManager manager, int x, int y, int z) =>
        FluidFlow.Raw(new LiveCells(manager), x, y, z);
}
