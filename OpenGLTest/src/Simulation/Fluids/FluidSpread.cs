using System.Collections.Generic;
using System.Numerics;

namespace VoxelEngine.Simulation.Fluids;

/// <summary>Pushes a ticked fluid cell's water out: downwards, or sideways toward the nearest hole the slope search finds within <see cref="SimulationConfig.FluidSlopeSearchDistance"/>, washing fragile blocks away and hardening on contact. Owned by <see cref="FluidManager"/>; main thread only.</summary>
internal sealed class FluidSpread
{
    private readonly FluidCells _cells;
    private readonly FluidSchedule _schedule;
    private readonly FluidNeighborhood _neighborhood;
    private readonly List<Vector3> _hardened;

    internal FluidSpread(FluidCells cells, FluidSchedule schedule, FluidNeighborhood neighborhood, List<Vector3> hardened)
    {
        _cells = cells;
        _schedule = schedule;
        _neighborhood = neighborhood;
        _hardened = hardened;
    }

    // Sentinel slope distance meaning "no hole found down this branch" (Minecraft's 1000).
    // Leaving it above every real distance makes the "keep the minimum" filter degrade into
    // "keep every passable direction" on flat ground for free.
    private const int NoSlope = 1000;


    // Reused by the slope search: nothing it calls chooses directions again, so one buffer
    // keeps a draining pond from allocating per cell per tick
    private readonly List<(int X, int Y, int Z)> _spreadDirections = new(4);

    /// <summary>
    /// Pushes water out of a cell. Spreading only ever writes into non-fluid replaceable cells;
    /// a cell that already holds fluid recomputes itself on its own tick, which is how the cell
    /// under fresh water turns into a falling column.
    /// </summary>
    internal void Spread(int x, int y, int z, ushort fluid, byte state, DropManager drops)
    {
        if (CanFlowInto(x, y - 1, z))
        {
            Flow(x, y - 1, z, fluid, drops);

            // A cell ringed by sources keeps its skirt even while draining downwards
            if (_neighborhood.SourceNeighborCount(x, y, z, fluid) >= 3)
                SpreadSides(x, y, z, fluid, state, drops);
        }
        else if (FluidState.IsSource(state) || !IsWaterHole(x, y - 1, z, fluid))
        {
            // Standing on something it cannot fall through, so it goes sideways
            SpreadSides(x, y, z, fluid, state, drops);
        }
    }

    /// <summary>Flows into the neighboring cells the slope search picked out.</summary>
    private void SpreadSides(int x, int y, int z, ushort fluid, byte state, DropManager drops)
    {
        // Level 7 has nothing left to give
        if (FluidState.Amount(state) - 1 <= 0) return;

        // Fast path: spreading only ever writes into an immediate horizontal neighbor
        // (see the CanFlowInto gate on each Flow below), so when none of the four is
        // flowable the slope search can pick nothing writable — skip it entirely.
        // This is exact, not a heuristic: the loop below cannot write otherwise.
        if (!CanFlowInto(x + 1, y, z)
            && !CanFlowInto(x - 1, y, z)
            && !CanFlowInto(x, y, z + 1)
            && !CanFlowInto(x, y, z - 1))
            return;

        foreach (var (dx, dy, dz) in ChooseSpreadDirections(x, y, z, fluid))
        {
            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (CanFlowInto(nx, ny, nz))
                Flow(nx, ny, nz, fluid, drops);
        }
    }

    /// <summary>
    /// The horizontal directions water should take out of a cell (Minecraft's <c>getSpread</c>):
    /// the ones whose nearest hole is closest, within
    /// <see cref="SimulationConfig.FluidSlopeSearchDistance"/> blocks. On flat ground no
    /// direction finds a hole, they all tie, and the water spreads evenly.
    /// </summary>
    private List<(int X, int Y, int Z)> ChooseSpreadDirections(int x, int y, int z, ushort fluid)
    {
        var best = NoSlope;
        var chosen = _spreadDirections;
        chosen.Clear();

        foreach (var (dx, dy, dz) in FluidNeighborhood.Horizontals)
        {
            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (!CanPassThrough(nx, ny, nz, fluid)) continue;

            int distance;
            if (IsWaterHole(nx, ny - 1, nz, fluid))
            {
                // A hole directly under this neighbor is distance 0, the global minimum
                distance = 0;
            }
            else if (best == 0)
            {
                // No direction can beat a direct hole, and this branch cannot tie at 0
                // either (it has no direct hole), so the deep search is skipped whole
                continue;
            }
            else
            {
                distance = SlopeDistance(nx, ny, nz, 1, (-dx, -dy, -dz), fluid);
            }

            if (distance < best)
            {
                best = distance;
                chosen.Clear();
            }

            if (distance == best) chosen.Add((dx, dy, dz));
        }

        return chosen;
    }

    /// <summary>
    /// Blocks from <paramref name="depth"/> to the nearest hole reachable through passable
    /// cells, or <see cref="NoSlope"/> when none is within
    /// <see cref="SimulationConfig.FluidSlopeSearchDistance"/>. Never turns back on
    /// itself (<paramref name="exclude"/>), so the search fans out.
    /// </summary>
    private int SlopeDistance(int x, int y, int z, int depth, (int X, int Y, int Z) exclude, ushort fluid)
    {
        var best = NoSlope;

        foreach (var (dx, dy, dz) in FluidNeighborhood.Horizontals)
        {
            if ((dx, dy, dz) == exclude) continue;

            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (!CanPassThrough(nx, ny, nz, fluid)) continue;

            // A hole one step on is the shallowest this call can find, so it wins at once
            if (IsWaterHole(nx, ny - 1, nz, fluid)) return depth;

            // A hole found at depth + 1 is the shallowest any recursive branch can find.
            // Once best is that shallow, a later direction is still checked for a direct
            // hole above, but its recursion can only tie, never beat, so it is skipped.
            if (best == depth + 1) continue;

            if (depth >= SimulationConfig.FluidSlopeSearchDistance) continue;

            var distance = SlopeDistance(nx, ny, nz, depth + 1, (-dx, -dy, -dz), fluid);
            if (distance < best) best = distance;
        }

        return best;
    }

    /// <summary>
    /// Fills a cell with fluid at the level its surroundings support, popping whatever fragile
    /// block the flow washed away (a pebble drops its flint) and rescheduling the neighborhood.
    /// </summary>
    private void Flow(int x, int y, int z, ushort fluid, DropManager drops)
    {
        var state = _neighborhood.ComputeState(x, y, z, fluid);

        // Nothing actually reaches this cell (it would drain again next tick): leave it alone
        // rather than washing a block away for a drop of water that never arrives
        if (state == FluidNeighborhood.StateNone) return;

        // A flow that would land touching the fluid that hardens it arrives hardened:
        // lava reaching water comes out as cobblestone, not as a one-tick lava cell
        var writeId = fluid;
        byte writeState = (byte)state;
        var hardensTo = BlockType.HardensToById[fluid];
        if (hardensTo != 0 && _neighborhood.TouchesFluid(x, y, z, BlockType.HardensTouchingById[fluid]))
        {
            writeId = hardensTo;
            writeState = 0;
        }
        else
        {
            // ...and so does a flow settling against a fluid that hardens on its touch:
            // water reaching lava crusts to cobblestone too, whichever side ticks first,
            // so the outcome never depends on the schedule's iteration order
            var crust = _neighborhood.HardenedByNeighbor(x, y, z, fluid);
            if (crust != 0)
            {
                writeId = crust;
                writeState = 0;
            }
        }

        if (!_cells.TryGetCell(x, y, z, out var previous, out var previousState)) return;

        if (previous != 0)
        {
            drops.SpawnLoot(previous, new Vector3(x, y, z),
                DropManager.PileCount(previous, previousState),
                DropManager.PileSecondary(previous, previousState),
                previousState);
            _cells.Chunks.UnmarkPlayerPlaced(x, y, z);
        }

        _cells.Write(x, y, z, writeId, writeState);
        if (writeId != fluid)
            _hardened.Add(new Vector3(x, y, z));
        _schedule.ScheduleAround(x, y, z);
    }

    /// <summary>Whether fluid may replace what is in a cell: loaded, not already fluid, and
    /// either air or a fragile block the flow washes away.</summary>
    private bool CanFlowInto(int x, int y, int z)
    {
        return _cells.TryGetCell(x, y, z, out var id, out _) && FluidWakeRules.IsFlowTarget(id);
    }

    /// <summary>Whether a cell would let fluid drain into it: replaceable, or already the same
    /// fluid. What the slope search calls a "hole".</summary>
    private bool IsWaterHole(int x, int y, int z, ushort fluid)
    {
        return _cells.TryGetCell(x, y, z, out var id, out _)
               && (BlockType.IsReplaceable(id) || id == fluid);
    }

    /// <summary>Whether the slope search may travel through a cell: replaceable, or the same
    /// fluid but not a source (a source is a destination, not a corridor).</summary>
    private bool CanPassThrough(int x, int y, int z, ushort fluid)
    {
        if (!_cells.TryGetCell(x, y, z, out var id, out var state)) return false;
        return BlockType.IsReplaceable(id) || (id == fluid && !FluidState.IsSource(state));
    }
}
