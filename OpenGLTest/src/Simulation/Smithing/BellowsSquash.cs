using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Player;

namespace VoxelEngine.Simulation.Smithing;

/// <summary>
/// How far each bellows is pressed down right now, for the draw. A bellows is a sink block (its <c>sink</c> share,
/// <see cref="SinkLayer"/>): a player's body sinks slowly into its top, and the bag follows the lowest feet standing
/// in it, then swells back to a full block at <see cref="BloomeryConfig.BellowsExpandSpeed"/> once nothing stands
/// there. While a bellows is not full height its cell is in <see cref="ChunkManager.SwingingCells"/>, so the chunk
/// mesh leaves it out and the smithing pass draws it squashed (<see cref="Active"/>); at full height the cell goes
/// back to the chunk mesh.
///
/// Display only: it reads positions and never changes the simulation, so singleplayer, a client and its remote
/// players all run it and nothing rides the wire. Only player bodies sink (mobs stand on the whole cell), so only
/// players press it down. Main thread only.
/// </summary>
public sealed class BellowsSquash
{
    // A press shallower than this is not worth pulling the cell out of the chunk mesh for
    private const float MinPress = 0.01f;

    private readonly Dictionary<(int X, int Y, int Z), (ushort Id, float Height)> _heights = [];
    private readonly Dictionary<(int X, int Y, int Z), float> _pressed = [];
    private readonly List<(int X, int Y, int Z)> _keys = [];
    private readonly List<(Vector3 Cell, ushort Id, float Height)> _active = [];

    /// <summary>Every bellows that is not at full height: its cell, its block id and its height in blocks.</summary>
    public IReadOnlyList<(Vector3 Cell, ushort Id, float Height)> Active => _active;

    /// <summary>
    /// The height of a bellows one step later: held down to <paramref name="pressedTo"/> (the height of the lowest
    /// feet standing in it) while something stands in it, never rising under a weight, and otherwise swelling by
    /// <see cref="BloomeryConfig.BellowsExpandSpeed"/> up to the full block.
    /// </summary>
    public static float Step(float height, float? pressedTo, float deltaTime) =>
        pressedTo is { } feet
            ? MathF.Min(height, feet)
            : MathF.Min(1f, height + BloomeryConfig.BellowsExpandSpeed * deltaTime);

    /// <summary>
    /// The height feet at <paramref name="feetY"/> press a bellows in the cell at <paramref name="cellY"/> down
    /// to, or null when they do not stand in its soft layer: the feet's own height over the cell's floor, never
    /// under the solid part.
    /// </summary>
    public static float? PressedTo(float feetY, int cellY, float sink) =>
        SinkLayer.FeetIn(feetY, cellY, sink)
            ? Math.Clamp(feetY - cellY, SinkLayer.SolidHeight(sink), 1f)
            : null;

    /// <summary>Advances every bellows by <paramref name="deltaTime"/> under the player bodies whose feet are at
    /// <paramref name="feet"/> (each <paramref name="halfWidth"/> wide to each side).</summary>
    public void Update(float deltaTime, ChunkManager chunks, IReadOnlyList<Vector3> feet, float halfWidth)
    {
        _pressed.Clear();
        for (var i = 0; i < feet.Count; i++)
            foreach (var cell in BellowsProbe.CellsUnder(feet[i], halfWidth))
            {
                var id = chunks.GetBlockGlobal(cell.X, cell.Y, cell.Z);
                if (BlockType.ById[id] is not { IsBellows: true }) continue;
                if (PressedTo(feet[i].Y, cell.Y, BlockType.SinkOf(id)) is not { } to) continue;
                _pressed[cell] = _pressed.TryGetValue(cell, out var lowest) ? MathF.Min(lowest, to) : to;
            }

        // A bellows pressed for the first time leaves the chunk mesh
        foreach (var (cell, to) in _pressed)
        {
            if (_heights.ContainsKey(cell) || to > 1f - MinPress) continue;
            _heights[cell] = (chunks.GetBlockGlobal(cell.X, cell.Y, cell.Z), 1f);
            chunks.SwingingCells.Add(cell.X, cell.Y, cell.Z);
            Remesh(chunks, cell);
        }

        _keys.Clear();
        _keys.AddRange(_heights.Keys);
        _active.Clear();
        foreach (var cell in _keys)
        {
            var (id, height) = _heights[cell];
            height = Step(height, _pressed.TryGetValue(cell, out var to) ? to : null, deltaTime);
            // Back at full height, or no longer that bellows (mined, unloaded): the chunk mesh takes the cell back
            if (height >= 1f || !chunks.TryGetCell(cell.X, cell.Y, cell.Z, out var held, out _) || held != id)
            {
                _heights.Remove(cell);
                chunks.SwingingCells.Remove(cell.X, cell.Y, cell.Z);
                Remesh(chunks, cell);
                continue;
            }

            _heights[cell] = (id, height);
            _active.Add((new Vector3(cell.X, cell.Y, cell.Z), id, height));
        }
    }

    private static void Remesh(ChunkManager chunks, (int X, int Y, int Z) cell)
    {
        var position = new Vector3(cell.X, cell.Y, cell.Z);
        var chunkPos = VoxelCoords.ChunkPosOf(position);
        if (chunks.Chunks.ContainsKey(chunkPos))
            chunks.ImmediateRemeshWithBorders(chunkPos, position);
    }
}
