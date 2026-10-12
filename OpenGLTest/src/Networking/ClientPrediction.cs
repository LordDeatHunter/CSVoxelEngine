using System.Collections.Generic;

namespace VoxelEngine.Networking;

/// <summary>
/// A multiplayer client's bookkeeping of what it predicted and the server has not answered yet, so
/// a stale server message never overwrites a newer prediction. Every intent the client sends takes
/// the next sequence id (<see cref="Commit"/>); the cells its local action changed
/// (<see cref="Stage"/>, fed by <see cref="World.BlockEdited"/>) are tagged with that id, and an
/// intent whose prediction changed the inventory marks the inventory pending until a snapshot
/// answering it arrives.
/// <list type="bullet">
/// <item>A server cell value for a cell with no pending prediction applies at once.</item>
/// <item>A correction tagged with the id of the newest prediction on its cell (or a later one) is the
/// server's state after that prediction was judged: it applies and settles the cell.</item>
/// <item>Anything older for a predicted cell - a broadcast, or a correction answering an earlier
/// intent - is held back, the newest one per cell, and applied when the prediction resolves
/// (<see cref="Resolve"/>), so the cell always ends on the last value the server sent.</item>
/// <item>An inventory snapshot older than the newest inventory-predicting intent is dropped: the
/// snapshot answering that intent is still coming, and it already includes everything older.</item>
/// </list>
/// The server answers every intent, in order, with exactly one result, which is what
/// <see cref="Resolve"/> consumes. GL-free; main thread only.
/// </summary>
public sealed class ClientPrediction
{
    private readonly List<(int X, int Y, int Z)> _staged = [];
    private readonly Dictionary<(int X, int Y, int Z), int> _pending = new();
    private readonly Dictionary<(int X, int Y, int Z), NetCellChange> _deferred = new();
    private readonly List<(int X, int Y, int Z)> _settled = [];
    private int _lastSeq;
    private int _pendingInventorySeq;

    /// <summary>The sequence id of the last intent sent.</summary>
    public int LastSeq => _lastSeq;

    /// <summary>Cells whose prediction the server has not answered yet.</summary>
    public int PendingCellCount => _pending.Count;

    /// <summary>Records a cell the action about to be sent as an intent changed locally.</summary>
    public void Stage(int x, int y, int z) => _staged.Add((x, y, z));

    /// <summary>
    /// Allocates the next sequence id for an intent going out now, tags every staged cell with it,
    /// and marks the inventory pending when <paramref name="predictsInventory"/>.
    /// </summary>
    public int Commit(bool predictsInventory)
    {
        var seq = ++_lastSeq;
        // A value already held back for a cell stays held: it is still the newest the server sent,
        // and it applies when this newer prediction resolves unless a correction replaces it first
        foreach (var cell in _staged)
            _pending[cell] = seq;
        _staged.Clear();
        if (predictsInventory)
            _pendingInventorySeq = seq;
        return seq;
    }

    /// <summary>
    /// Offers a server value for a cell: <paramref name="seq"/> is the id a correction answers, or
    /// null for a broadcast. True when the caller applies it now; false when it was held back behind
    /// a newer prediction.
    /// </summary>
    public bool Offer(in NetCellChange change, int? seq)
    {
        var key = (change.X, change.Y, change.Z);
        if (!_pending.TryGetValue(key, out var predicted))
            return true;
        if (seq is { } answered && answered >= predicted)
        {
            _pending.Remove(key);
            _deferred.Remove(key);
            return true;
        }
        _deferred[key] = change;
        return false;
    }

    /// <summary>
    /// The server answered intent <paramref name="seq"/> (accepted or refused): every prediction
    /// made at or before it is settled, and the values held back for those cells are added to
    /// <paramref name="apply"/>.
    /// </summary>
    public void Resolve(int seq, List<NetCellChange> apply)
    {
        _settled.Clear();
        foreach (var (cell, predicted) in _pending)
            if (predicted <= seq)
                _settled.Add(cell);
        foreach (var cell in _settled)
        {
            _pending.Remove(cell);
            if (_deferred.Remove(cell, out var held))
                apply.Add(held);
        }
    }

    /// <summary>Whether an inventory snapshot answering intent <paramref name="seq"/> is current.</summary>
    public bool AcceptsInventory(int seq) => seq >= _pendingInventorySeq;
}
