using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Simulation.Fluids;

/// <summary>The fluid simulation's one schedule: the cells waiting for the next tick, gated by <see cref="Enabled"/> on the edit paths. Owned by <see cref="FluidManager"/>; main thread only.</summary>
internal sealed class FluidSchedule
{
    // Cells scheduled since the last tick started. Every write, edit and chunk wake adds here;
    // a tick snapshots it into _tickCells below, so a cell scheduled while ticking is handled on
    // the following tick (no unbounded cascade within one).
    private readonly HashSet<long> _next = [];

    /// <summary>See <see cref="FluidManager.Enabled"/>: false makes <see cref="ScheduleAround"/> and <see cref="ScheduleBox"/> return at once.</summary>
    internal bool Enabled { get; set; } = true;

    /// <summary>Cells scheduled for the next tick (the wake's backlog gate).</summary>
    internal int Count => _next.Count;

    /// <summary>Schedules a cell and its six face neighbors; nothing while disabled.</summary>
    internal void ScheduleAround(int x, int y, int z)
    {
        if (!Enabled) return;
        _next.Add(FluidCellKey.Pack(x, y, z));
        _next.Add(FluidCellKey.Pack(x + 1, y, z));
        _next.Add(FluidCellKey.Pack(x - 1, y, z));
        _next.Add(FluidCellKey.Pack(x, y + 1, z));
        _next.Add(FluidCellKey.Pack(x, y - 1, z));
        _next.Add(FluidCellKey.Pack(x, y, z + 1));
        _next.Add(FluidCellKey.Pack(x, y, z - 1));
    }

    /// <summary>Schedules every cell of the inclusive box; nothing while disabled.</summary>
    internal void ScheduleBox(Vector3 min, Vector3 max)
    {
        if (!Enabled) return;
        for (var x = (int)min.X; x <= (int)max.X; x++)
        for (var y = (int)min.Y; y <= (int)max.Y; y++)
        for (var z = (int)min.Z; z <= (int)max.Z; z++)
            _next.Add(FluidCellKey.Pack(x, y, z));
    }

    /// <summary>Schedules a chunk-local cell for the next tick.</summary>
    internal void ScheduleLocal(Chunk chunk, int lx, int ly, int lz) =>
        _next.Add(FluidCellKey.Pack((int)chunk.Position.X * Chunk.Size + lx,
            (int)chunk.Position.Y * Chunk.Size + ly,
            (int)chunk.Position.Z * Chunk.Size + lz));

    /// <summary>Re-adds a cell for the next tick, ungated (a slow fluid sitting a tick out).</summary>
    internal void Requeue(long key) => _next.Add(key);

    /// <summary>Moves every scheduled cell into <paramref name="cells"/> in the set's own iteration order and empties the schedule.</summary>
    internal void DrainInto(List<long> cells)
    {
        cells.AddRange(_next);
        _next.Clear();
    }
}
