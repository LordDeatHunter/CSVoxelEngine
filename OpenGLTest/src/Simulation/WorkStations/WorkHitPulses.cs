using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Simulation.WorkStations;

/// <summary>
/// When each work station was last hit, read off the states as they are drawn: a hit is a rise of the picked
/// recipe's hit count, or the count falling back to none as the stack shrinks (the hit that finished it). Picking
/// another recipe or taking from the stack is no hit. It watches the state instead of the hit itself, so it sees a
/// client's mirrored stations and other players' blows the same way. Cosmetic: the jolt of the stack reads it
/// (<see cref="Juice.WorkHitJolt"/>).
/// </summary>
public sealed class WorkHitPulses
{
    private readonly Dictionary<(int X, int Y, int Z), Entry> _entries = new();
    private readonly List<(int X, int Y, int Z)> _stale = new();

    private sealed class Entry
    {
        public int Hits;
        public int InputCount;
        public float HitAt = float.NegativeInfinity;
        public float SeenAt;
    }

    /// <summary>
    /// Notes the station at <paramref name="cell"/> as it stands at time <paramref name="now"/> (seconds, any clock
    /// that only runs forward) and returns the seconds since its last hit, infinity when none was seen. A station
    /// seen for the first time has not been hit.
    /// </summary>
    public float Observe(Vector3 cell, WorkStationManager.State state, float now)
    {
        var key = VoxelCoords.BlockCellOf(cell);
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry { Hits = state.Hits, InputCount = state.InputCount };
            _entries[key] = entry;
        }

        var struck = state.Hits > entry.Hits
                     || (state.Hits == 0 && entry.Hits > 0 && state.InputCount < entry.InputCount);
        if (struck) entry.HitAt = now;
        entry.Hits = state.Hits;
        entry.InputCount = state.InputCount;
        entry.SeenAt = now;
        return now - entry.HitAt;
    }

    /// <summary>Forgets every station not noted at <paramref name="now"/> (gone, or emptied).</summary>
    public void Sweep(float now)
    {
        _stale.Clear();
        foreach (var (key, entry) in _entries)
            if (entry.SeenAt != now)
                _stale.Add(key);
        foreach (var key in _stale)
            _entries.Remove(key);
    }
}
