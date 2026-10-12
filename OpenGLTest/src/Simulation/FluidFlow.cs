using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Simulation;

/// <summary>
/// Reads one cell's block id and state byte for <see cref="FluidFlow"/>. Returns false when
/// the cell is not available (an unloaded chunk), which the flow treats like a dam. Implemented
/// by small structs so the generic flow code is specialized per reader and never boxes.
/// </summary>
internal interface IFluidCellReader
{
    bool TryGetCell(int x, int y, int z, out byte id, out byte state);
}

/// <summary>
/// The one definition of which way a fluid flows, shared by the live world
/// (<see cref="ChunkManager.GetFluidFlowGlobal"/>, which pushes bodies and drops) and the chunk
/// mesher (which bakes it into fluid vertices from its padded snapshot). Pure: everything it
/// knows comes through the <see cref="IFluidCellReader"/>, so it is safe on worker threads.
/// </summary>
internal static class FluidFlow
{
    // The four horizontal neighbors a flow can be pulled toward, as (dx, dz)
    private static readonly (int X, int Z)[] FlowNeighbors = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>
    /// How strongly the fluid <paramref name="id"/> at (x, y, z), whose own surface sits at
    /// <paramref name="ownHeight"/>, is pulled toward its neighbor (x+dx, y, z+dz): the height
    /// difference to a neighbor of the same fluid, or to a whole source lower still when the
    /// neighbor is replaceable and the fluid would drop off into it. False (no term) past a
    /// solid, a different block, a replaceable cell with no drop, or an unavailable cell.
    /// </summary>
    private static bool PairTerm<T>(in T cells, int x, int y, int z, byte id, float ownHeight,
        int dx, int dz, out float term) where T : struct, IFluidCellReader
    {
        term = 0f;
        if (!cells.TryGetCell(x + dx, y, z + dz, out var neighborId, out var neighborState))
            return false;

        float neighborHeight;
        if (neighborId == id)
        {
            neighborHeight = FluidState.SurfaceHeight(neighborState);
        }
        else if (BlockType.IsReplaceable(neighborId))
        {
            // Air or a fragile block: the fluid only gets pulled that way if it would drop
            // off there, and a drop counts as a full source lower than any level could be
            if (cells.TryGetCell(x + dx, y - 1, z + dz, out var belowId, out var belowState) && belowId == id)
                neighborHeight = FluidState.SurfaceHeight(belowState) - FluidState.SourceHeight;
            else
                return false;
        }
        else
        {
            return false; // a solid neighbor dams the flow instead of steering it
        }

        term = ownHeight - neighborHeight;
        return true;
    }

    /// <summary>
    /// THE flow definition: the un-normalized horizontal flow of the fluid in the cell
    /// (x, y, z), or <see cref="Vector3.Zero"/> when the cell holds no fluid. Mirrors
    /// Minecraft's <c>FlowingFluid.getFlow</c>: every horizontal neighbor pulls the flow toward
    /// itself in proportion to how much lower its own surface sits, a replaceable cell the fluid
    /// can drop off into counting as a whole source lower still, and a solid neighbor
    /// contributing nothing. A pool of sources therefore cancels out to zero, while a skirt
    /// points away from its source and toward the edge it pours over.
    /// </summary>
    internal static Vector3 Raw<T>(in T cells, int x, int y, int z) where T : struct, IFluidCellReader
    {
        if (!cells.TryGetCell(x, y, z, out var id, out var state) || !BlockType.IsFluid(id))
            return Vector3.Zero;

        // The cell's own level, deliberately not the column surface: pinning a cell with
        // fluid above it to a full block would flatten a falling column's gradient to nothing
        var ownHeight = FluidState.SurfaceHeight(state);
        var flow = Vector3.Zero;

        foreach (var (dx, dz) in FlowNeighbors)
        {
            if (PairTerm(cells, x, y, z, id, ownHeight, dx, dz, out var term))
                flow += new Vector3(dx, 0f, dz) * term;
        }

        return flow;
    }

    /// <summary>
    /// The flow at one top corner of the fluid cell (x, y, z): the same pair term as
    /// <see cref="Raw"/>, summed over the four edges of the 2x2 cell block that meets at the
    /// corner (x + max(sx, 0), z + max(sz, 0)); <paramref name="sx"/> and <paramref name="sz"/>
    /// are each ±1. Zero when the cell holds no fluid. Each edge is evaluated from its lower
    /// endpoint when that cell holds this fluid, else from its upper endpoint with the direction
    /// flipped (a fluid-fluid edge gives the same vector from either side), and the block is
    /// walked in a fixed order from its minimum cell, so every same-fluid cell of the block
    /// computes the bit-identical value: shared corners agree and interpolated flow has no seam.
    /// Reads stay within ±1 horizontally of (x, z), at y and y - 1, so the mesher's padded
    /// snapshot covers every own cell.
    /// </summary>
    internal static Vector3 Corner<T>(in T cells, int x, int y, int z, int sx, int sz) where T : struct, IFluidCellReader
    {
        if (!cells.TryGetCell(x, y, z, out var id, out _) || !BlockType.IsFluid(id))
            return Vector3.Zero;

        var x0 = sx < 0 ? x - 1 : x;
        var z0 = sz < 0 ? z - 1 : z;
        var flow = Vector3.Zero;
        flow += EdgeTerm(cells, x0, y, z0, id, 1, 0);         // (x0,z0) - (x0+1,z0)
        flow += EdgeTerm(cells, x0, y, z0 + 1, id, 1, 0);     // (x0,z0+1) - (x0+1,z0+1)
        flow += EdgeTerm(cells, x0, y, z0, id, 0, 1);         // (x0,z0) - (x0,z0+1)
        flow += EdgeTerm(cells, x0 + 1, y, z0, id, 0, 1);     // (x0+1,z0) - (x0+1,z0+1)
        return flow;
    }

    // One edge of a corner block, from (x, z) toward (x+dx, z+dz), as a vector along (dx, dz)
    private static Vector3 EdgeTerm<T>(in T cells, int x, int y, int z, byte id, int dx, int dz)
        where T : struct, IFluidCellReader
    {
        if (cells.TryGetCell(x, y, z, out var firstId, out var firstState) && firstId == id)
        {
            return PairTerm(cells, x, y, z, id, FluidState.SurfaceHeight(firstState), dx, dz, out var term)
                ? new Vector3(dx, 0f, dz) * term
                : Vector3.Zero;
        }

        if (cells.TryGetCell(x + dx, y, z + dz, out var secondId, out var secondState) && secondId == id)
        {
            return PairTerm(cells, x + dx, y, z + dz, id, FluidState.SurfaceHeight(secondState), -dx, -dz, out var term)
                ? new Vector3(-dx, 0f, -dz) * term
                : Vector3.Zero;
        }

        return Vector3.Zero;
    }
}
