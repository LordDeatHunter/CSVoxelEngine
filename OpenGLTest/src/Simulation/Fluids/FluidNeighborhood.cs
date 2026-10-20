namespace VoxelEngine.Simulation.Fluids;

/// <summary>The neighbor reads the fluid tick's rules are made of: the level a non-source cell should hold (<see cref="ComputeState"/>), contact with a hardening fluid and adjacent sources, all through <see cref="FluidCells.TryGetCell"/>. Owned by <see cref="FluidManager"/>; main thread only.</summary>
internal sealed class FluidNeighborhood(FluidCells cells)
{
    private readonly FluidCells _cells = cells;

    // ComputeState's "this cell holds no fluid any more" answer
    internal const int StateNone = -1;

    internal static readonly (int X, int Y, int Z)[] Horizontals =
        [(1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1)];

    /// <summary>
    /// The state a non-source fluid cell should hold, given its surroundings (Minecraft's
    /// <c>getNewLiquid</c>): <see cref="StateNone"/> when nothing feeds it any more,
    /// <see cref="FluidState.Source"/> when two sources meet over solid ground (the infinite
    /// 2x2 pool), <see cref="FluidState.Falling"/> when fed from above, else the thinnest
    /// flowing level its neighbors can support.
    /// </summary>
    internal int ComputeState(int x, int y, int z, byte fluid)
    {
        var maxAmount = 0;
        var sources = 0;

        foreach (var (dx, dy, dz) in Horizontals)
        {
            if (!_cells.TryGetCell(x + dx, y + dy, z + dz, out var id, out var state)) continue;
            if (id != fluid) continue;

            if (FluidState.IsSource(state)) sources++;
            var amount = FluidState.Amount(state);
            if (amount > maxAmount) maxAmount = amount;
        }

        if (sources >= 2 && BlockType.FluidInfiniteById[fluid])
        {
            // Two sources side by side become a source themselves, but only where the water
            // cannot simply drain away underneath
            if (_cells.TryGetCell(x, y - 1, z, out var below, out var belowState)
                && ((below != 0 && !BlockType.IsReplaceable(below) && !BlockType.IsFluid(below))
                    || (below == fluid && FluidState.IsSource(belowState))))
                return FluidState.Source;
        }

        // Water pouring in from above always fills the whole cell
        if (_cells.TryGetCell(x, y + 1, z, out var above, out _) && above == fluid)
            return FluidState.Falling;

        var range = BlockType.FluidRangeById[fluid];
        var newAmount = maxAmount - 1;
        if (newAmount <= 0 || newAmount < 8 - range) return StateNone;

        return FluidState.Flowing(8 - newAmount);
    }

    /// <summary>Whether any of the six face neighbors of a cell holds the given fluid.
    /// Unloaded neighbors read as absent, never as contact.</summary>
    internal bool TouchesFluid(int x, int y, int z, byte fluidId)
    {
        foreach (var (dx, dy, dz) in Horizontals)
        {
            if (_cells.TryGetCell(x + dx, y + dy, z + dz, out var id, out _) && id == fluidId)
                return true;
        }

        return (_cells.TryGetCell(x, y + 1, z, out var above, out _) && above == fluidId)
               || (_cells.TryGetCell(x, y - 1, z, out var below, out _) && below == fluidId);
    }

    /// <summary>
    /// Block id a flow of <paramref name="fluid"/> settling at a cell hardens into when a
    /// neighboring fluid hardens on its touch (water reaching lava crusts to the lava's
    /// cobblestone), or 0 when no neighbor does. Unloaded neighbors harden nothing.
    /// </summary>
    internal byte HardenedByNeighbor(int x, int y, int z, byte fluid)
    {
        foreach (var (dx, dy, dz) in Horizontals)
        {
            if (_cells.TryGetCell(x + dx, y + dy, z + dz, out var id, out _)
                && BlockType.IsFluid(id)
                && BlockType.HardensTouchingById[id] == fluid
                && BlockType.HardensToById[id] != 0)
                return BlockType.HardensToById[id];
        }

        if (_cells.TryGetCell(x, y + 1, z, out var above, out _)
            && BlockType.IsFluid(above)
            && BlockType.HardensTouchingById[above] == fluid
            && BlockType.HardensToById[above] != 0)
            return BlockType.HardensToById[above];

        if (_cells.TryGetCell(x, y - 1, z, out var below, out _)
            && BlockType.IsFluid(below)
            && BlockType.HardensTouchingById[below] == fluid
            && BlockType.HardensToById[below] != 0)
            return BlockType.HardensToById[below];

        return 0;
    }

    /// <summary>Horizontally adjacent source cells of the same fluid.</summary>
    internal int SourceNeighborCount(int x, int y, int z, byte fluid)
    {
        var count = 0;
        foreach (var (dx, dy, dz) in Horizontals)
        {
            if (_cells.TryGetCell(x + dx, y + dy, z + dz, out var id, out var state)
                && id == fluid && FluidState.IsSource(state))
                count++;
        }

        return count;
    }
}
