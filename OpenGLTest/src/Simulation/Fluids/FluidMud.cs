using System;
using VoxelEngine.Config;
using VoxelEngine.Content;

namespace VoxelEngine.Simulation.Fluids;

/// <summary>Turns dirt beside settled water into mud the moment a water cell ticks next to it. Owned by <see cref="FluidManager"/>; main thread only.</summary>
internal sealed class FluidMud
{
    private readonly FluidCells _cells;
    private readonly FluidSchedule _schedule;

    // Dirt-to-mud ids, resolved once content is loaded. Every ticked fluid cell checks
    // its dirt neighbors against the mud condition right away, so settled water still
    // converts dirt the moment an edit schedules it, with no timers to keep.
    private byte _dirtId;
    private byte _mudId;
    private byte _waterId;
    private bool _mudIdsResolved;

    internal FluidMud(FluidCells cells, FluidSchedule schedule)
    {
        _cells = cells;
        _schedule = schedule;
    }

    /// <summary>
    /// Turns the dirt around a ticked fluid cell to mud wherever it meets the mud condition
    /// (water above, enough water across the 26-neighborhood). Instant: no wet-time is kept,
    /// so drying out needs no handling — mud simply stays mud. Conversions write through
    /// <see cref="FluidCells.Write"/> like any fluid edit, so their remeshes join the tick's batch.
    /// </summary>
    internal void TryConvertMudAround(int x, int y, int z, byte fluid)
    {
        if (!MudIds(out var dirt, out var mud, out var water)) return;
        if (fluid != water) return;

        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (!_cells.TryGetCell(nx, ny, nz, out var id, out _) || id != dirt) continue;
            if (!IsMudWet(nx, ny, nz, water)) continue;

            _cells.Write(nx, ny, nz, mud, 0);
            _schedule.ScheduleAround(nx, ny, nz);
        }
    }

    /// <summary>
    /// Whether a dirt cell counts as submerged: a water cell directly above it, and enough
    /// water across the whole 26-neighborhood. Unloaded neighbors read as absent.
    /// </summary>
    private bool IsMudWet(int x, int y, int z, byte water)
    {
        if (!_cells.TryGetCell(x, y + 1, z, out var above, out _) || above != water) return false;

        var count = 0;
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            if (_cells.TryGetCell(x + dx, y + dy, z + dz, out var id, out _) && id == water
                && ++count >= WorldGenerationConfig.MudWaterNeighbors)
                return true;
        }

        return false;
    }

    /// <summary>Dirt, mud and water ids, resolved once content is loaded.</summary>
    private bool MudIds(out byte dirt, out byte mud, out byte water)
    {
        if (!_mudIdsResolved)
        {
            try
            {
                _dirtId = ContentRegistry.RequireBlock("dirt").Id;
                _mudId = ContentRegistry.RequireBlock("mud").Id;
                _waterId = ContentRegistry.RequireBlock("water").Id;
            }
            catch (InvalidOperationException)
            {
                dirt = mud = water = 0;
                return false;
            }

            _mudIdsResolved = true;
        }

        dirt = _dirtId;
        mud = _mudId;
        water = _waterId;
        return true;
    }
}
