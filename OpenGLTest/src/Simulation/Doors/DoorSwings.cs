using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Interaction;

namespace VoxelEngine.Simulation.Doors;

/// <summary>
/// The side-hinged doors that are swinging (<see cref="BlockType.SideHinged"/>). The state byte flips at once, as it
/// always did; this only carries the quarter second in between: while a door swings, its two cells sit in
/// <see cref="ChunkManager.SwingingCells"/> (out of the chunk mesh and out of the block collision), the panel is
/// drawn turning (<c>DoorPasses</c>) and it moves the bodies in its way (<see cref="DoorPush"/>), then the cells go
/// back to the chunk with one remesh. A swing reversed midway turns back from where the panel stands. Every side
/// runs it from the state flips it sees (the toggle, or a server's block change on a client), so it is never saved
/// or sent. GL-free; main thread only.
/// </summary>
public sealed class DoorSwings(ChunkManager chunks)
{
    private readonly Dictionary<(int X, int Y, int Z), (ushort LowerId, byte State, float Progress)> _swings = [];
    private readonly List<DoorSwing> _active = [];
    // Scratch: the doors to end, then the doors to advance, so no pass edits the table it walks
    private readonly List<(int X, int Y, int Z)> _keys = [];

    /// <summary>The doors swinging this tick, as of the last <see cref="Update"/>.</summary>
    public IReadOnlyList<DoorSwing> Active => _active;

    /// <summary>
    /// Starts the swing of the door one of whose halves is the cell (x, y, z), holding <paramref name="id"/> and
    /// about to take <paramref name="targetState"/>. Call it before the state write, so the remesh that write
    /// triggers already leaves the door out. A second call for the same door and direction (its other half) changes
    /// nothing; a block that is not side-hinged is ignored.
    /// </summary>
    public void Start(int x, int y, int z, ushort id, byte targetState)
    {
        if (BlockType.ById[id] is not { SideHinged: true } type) return;
        if (type.LowerHalfId != 0)
        {
            id = type.LowerHalfId;
            y--;
        }

        var progress = 0f;
        if (_swings.TryGetValue((x, y, z), out var running))
        {
            if (DoorState.IsOpen(running.State) == DoorState.IsOpen(targetState)) return;
            // Turned back midway: the eased curve is symmetric, so the mirrored progress is the same angle
            progress = 1f - running.Progress;
        }

        _swings[(x, y, z)] = (id, targetState, progress);
        chunks.SwingingCells.Add(x, y, z);
        chunks.SwingingCells.Add(x, y + 1, z);
    }

    /// <summary>
    /// Advances every swing by <paramref name="deltaTime"/> and rebuilds <see cref="Active"/>. A door whose panel
    /// arrived last tick, or whose lower cell no longer holds it (broken, unloaded), hands its cells back to the
    /// chunk mesh first.
    /// </summary>
    public void Update(float deltaTime)
    {
        foreach (var swing in _active)
        {
            var key = ((int)swing.Cell.X, (int)swing.Cell.Y, (int)swing.Cell.Z);
            if (swing.Done || !chunks.TryGetCell(key.Item1, key.Item2, key.Item3, out var held, out _) || held != swing.LowerId)
                _keys.Add(key);
        }
        foreach (var key in _keys)
            End(key);

        _keys.Clear();
        _keys.AddRange(_swings.Keys);
        _active.Clear();
        foreach (var key in _keys)
        {
            var (lowerId, state, progress) = _swings[key];
            progress = MathF.Min(progress + deltaTime / DoorConfig.SwingSeconds, 1f);
            _swings[key] = (lowerId, state, progress);
            var turned = MathUtils.EaseInOutCubic(progress);
            _active.Add(new DoorSwing(new Vector3(key.X, key.Y, key.Z), lowerId, state,
                DoorState.IsOpen(state) ? turned : 1f - turned, progress >= 1f));
        }
        _keys.Clear();
    }

    // Hands a door's two cells back to the chunk mesh and the block collision
    private void End((int X, int Y, int Z) key)
    {
        if (!_swings.Remove(key)) return;
        for (var dy = 0; dy < 2; dy++)
        {
            chunks.SwingingCells.Remove(key.X, key.Y + dy, key.Z);
            var cell = new Vector3(key.X, key.Y + dy, key.Z);
            var chunkPos = VoxelCoords.ChunkPosOf(cell);
            if (chunks.Chunks.ContainsKey(chunkPos))
                chunks.ImmediateRemeshWithBorders(chunkPos, cell);
        }
    }
}
