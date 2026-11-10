using System;
using System.Collections.Generic;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Posing;
using VoxelEngine.Networking;

namespace VoxelEngine.Simulation.Chests;

/// <summary>
/// Which chests stand open and what their lids are doing. A chest is open while it has at least one viewer: the
/// lid plays the model's open clip the moment its viewers go above zero and holds it, and plays the close clip the
/// moment they drop back to zero. The viewers come from two places: the cells told from outside
/// (<see cref="SetViewed"/>: the server's players with a chest open, which a client receives at the end of each
/// <see cref="NetMessage.StationState"/>) and the local player's own open panel (handed to <see cref="Update"/>, so
/// singleplayer and a client's own chest answer at once). A closed chest at rest holds no entry. The locked rattle
/// (<see cref="Wobble"/>) is a third clip that only a closed, resting chest plays. Cosmetic and never saved: a
/// world always loads with every lid shut. GL-free; main thread only.
/// </summary>
public sealed class ChestLids(AnimationClip open, AnimationClip close, AnimationClip wobble)
{
    private enum Motion : byte
    {
        Opening,
        Closing,
        Wobbling,
    }

    private readonly Dictionary<(int X, int Y, int Z), (Motion Kind, float Time)> _motions = new();
    private readonly HashSet<(int X, int Y, int Z)> _viewed = [];
    private readonly HashSet<(int X, int Y, int Z)> _open = [];
    private readonly List<(int X, int Y, int Z)> _scratch = [];

    /// <summary>Raised the moment a lid starts its open or close clip (true for open), once per swing. The locked
    /// rattle never raises it, and a swing reversed mid-clip raises it again. Listeners play the lid sound.</summary>
    public event Action<bool>? LidStarted;

    /// <summary>Whether any chest has a viewer told from outside, so the station broadcast has something to say.</summary>
    public bool HasViewed => _viewed.Count > 0;

    /// <summary>Replaces the cells that have at least one viewer other than the local player's panel: every chest
    /// some player of the server has open. The lids follow on the next <see cref="Update"/>.</summary>
    public void SetViewed(IReadOnlyCollection<(int X, int Y, int Z)> cells)
    {
        _viewed.Clear();
        foreach (var cell in cells)
            _viewed.Add(cell);
    }

    /// <summary>Whether the chest at the cell is open (has a viewer) as of the last <see cref="Update"/>.</summary>
    public bool IsOpen(int x, int y, int z) => _open.Contains((x, y, z));

    /// <summary>
    /// Rattles the chest at the cell, the way a locked chest answers a hand: the wobble clip, once. Ignored (false)
    /// while the chest is open or its lid is still moving, so it never cuts an open or a close short.
    /// </summary>
    public bool Wobble(int x, int y, int z)
    {
        var cell = (x, y, z);
        if (_open.Contains(cell) || _motions.ContainsKey(cell)) return false;
        _motions[cell] = (Motion.Wobbling, 0f);
        return true;
    }

    /// <summary>
    /// One frame: a chest that gained its first viewer starts opening, one that lost its last starts closing, and
    /// every moving lid advances. An open lid holds the end of its clip; a close or a wobble that has played out
    /// leaves the chest at rest. <paramref name="localViewer"/> is the chest the local player has open, or null
    /// (always null on a server, whose viewers all come through <see cref="SetViewed"/>).
    /// </summary>
    public void Update(float deltaTime, (int X, int Y, int Z)? localViewer = null)
    {
        FollowViewers(localViewer);
        if (_motions.Count == 0) return;

        _scratch.Clear();
        foreach (var cell in _motions.Keys)
            _scratch.Add(cell);
        foreach (var cell in _scratch)
        {
            var (kind, time) = _motions[cell];
            time += deltaTime;
            var length = ClipOf(kind).Length;
            if (kind != Motion.Opening && time >= length)
            {
                _motions.Remove(cell);
                continue;
            }
            _motions[cell] = (kind, MathF.Min(time, length));
        }
    }

    /// <summary>What the chest at the cell shows right now: the clip its lid is playing and how far in, or the
    /// rest pose (the default) for a closed chest.</summary>
    public ClipPose PoseAt(int x, int y, int z) =>
        _motions.TryGetValue((x, y, z), out var motion) ? new ClipPose(ClipOf(motion.Kind), motion.Time) : default;

    /// <summary>Writes the viewed cells for the server's station broadcast.</summary>
    public void WriteNet(PacketWriter writer)
    {
        writer.Write(_viewed.Count);
        foreach (var (x, y, z) in _viewed)
            StationNetCodec.WriteCell(writer, x, y, z);
    }

    /// <summary>Reads the viewed cells a server wrote with <see cref="WriteNet"/>, replacing this side's.</summary>
    public void ReadNet(PacketReader reader)
    {
        var count = StationNetCodec.ReadCount(reader);
        _viewed.Clear();
        for (var i = 0; i < count; i++)
            _viewed.Add(StationNetCodec.ReadCell(reader));
    }

    // Brings the open set in step with the viewers, starting the clip of every chest that changed
    private void FollowViewers((int X, int Y, int Z)? localViewer)
    {
        _scratch.Clear();
        foreach (var cell in _open)
            if (!_viewed.Contains(cell) && localViewer != cell)
                _scratch.Add(cell);
        foreach (var cell in _scratch)
        {
            _open.Remove(cell);
            Start(cell, Motion.Closing);
        }

        foreach (var cell in _viewed)
            if (_open.Add(cell))
                Start(cell, Motion.Opening);
        if (localViewer is { } local && _open.Add(local))
            Start(local, Motion.Opening);
    }

    // A lid turned around mid-swing picks the new clip up as far from its end as the old one was from its start,
    // so reopening a closing chest (or closing an opening one) does not jump back to the clip's first frame
    private void Start(in (int X, int Y, int Z) cell, Motion kind)
    {
        var time = 0f;
        if (_motions.TryGetValue(cell, out var running) && running.Kind != Motion.Wobbling && running.Kind != kind)
        {
            var from = ClipOf(running.Kind);
            var done = from.Length > 0f ? running.Time / from.Length : 1f;
            time = (1f - done) * ClipOf(kind).Length;
        }
        _motions[cell] = (kind, time);
        LidStarted?.Invoke(kind == Motion.Opening);
    }

    private AnimationClip ClipOf(Motion kind) => kind switch
    {
        Motion.Opening => open,
        Motion.Closing => close,
        _ => wobble,
    };
}
