using System.Collections.Generic;
using VoxelEngine.Simulation.Waystones;

namespace VoxelEngine.Networking.Client;

/// <summary>
/// A multiplayer client's copy of the waystone registry rows it is sent (<see cref="NetMessage.WaystoneRows"/>: only the
/// waystones this player discovered), and the newest <see cref="NetMessage.WaystoneOpened"/> answer waiting for the
/// waystone screen to take it. The client keeps no registry of its own: these rows are what its list shows, falling back
/// to the discovery-time name for a waystone the server sent no row for. GL-free; main thread only.
/// </summary>
public sealed class WaystoneMirror
{
    private readonly Dictionary<(int X, int Y, int Z), WaystoneRecord> _records = new();
    private readonly HashSet<(int X, int Y, int Z)> _renamable = [];
    private WaystoneOpened? _opened;

    /// <summary>Moves whenever a new set of rows was taken (what an open list rebuilds on).</summary>
    public int Version { get; private set; }

    /// <summary>The row of the waystone at the cell, or null when the server sent none.</summary>
    public WaystoneRecord? Peek(int x, int y, int z) => _records.GetValueOrDefault((x, y, z));

    /// <summary>Whether the server says this player may rename the waystone at the cell (its first discoverer, not lost).</summary>
    public bool MayRename(int x, int y, int z) => _renamable.Contains((x, y, z));

    /// <summary>Replaces every row with the server's newest set.</summary>
    public void Replace(IReadOnlyList<WaystoneNetRow> rows)
    {
        _records.Clear();
        _renamable.Clear();
        foreach (var row in rows)
        {
            // Who discovered it first is the server's business; the client only knows whether it is itself
            _records[(row.X, row.Y, row.Z)] = new WaystoneRecord(row.Name, "", row.Lost);
            if (row.MayRename && !row.Lost) _renamable.Add((row.X, row.Y, row.Z));
        }
        Version++;
    }

    /// <summary>The server answered an activation: the waystone screen takes it on its next frame.</summary>
    public void Offer(in WaystoneOpened opened) => _opened = opened;

    /// <summary>Takes the waiting activation answer once.</summary>
    public bool TryTakeOpened(out WaystoneOpened opened)
    {
        opened = _opened.GetValueOrDefault();
        if (_opened == null) return false;
        _opened = null;
        return true;
    }

    // The trip this client asked for: the intent's sequence id, the destination, and whether the server accepted it
    private (int Seq, (int X, int Y, int Z) Destination, bool Accepted)? _trip;

    /// <summary>A <see cref="NetIntentKind.Waystone"/> intent went out with sequence id <paramref name="seq"/>.</summary>
    public void NoteTripSent(int seq, (int X, int Y, int Z) destination) => _trip = (seq, destination, false);

    /// <summary>The server answered intent <paramref name="seq"/>: a refused trip is forgotten, an accepted one waits for
    /// its landing (the next <see cref="NetMessage.PlayerCorrection"/>). False unless it answered the trip.</summary>
    public bool NoteResult(int seq, bool accepted)
    {
        if (_trip is not { } trip || trip.Seq != seq) return false;
        _trip = accepted ? trip with { Accepted = true } : null;
        return true;
    }

    /// <summary>A correction arrived: when it is the landing of an accepted trip, hands back the destination waystone
    /// (the client then holds the camera there until its own chunks are in) and forgets the trip.</summary>
    public bool TryTakeLanding(out (int X, int Y, int Z) destination)
    {
        destination = default;
        if (_trip is not { Accepted: true } trip) return false;
        destination = trip.Destination;
        _trip = null;
        return true;
    }

    /// <summary>Forgets everything (a disconnect).</summary>
    public void Clear()
    {
        _records.Clear();
        _renamable.Clear();
        _opened = null;
        _trip = null;
        Version++;
    }
}
