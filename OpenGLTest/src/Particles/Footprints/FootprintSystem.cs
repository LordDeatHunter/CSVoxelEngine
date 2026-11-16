using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Chunks;

namespace VoxelEngine.Particles.Footprints;

/// <summary>One print on the ground: where its quad lies, how clear it started and how long it lasts.</summary>
public struct Footprint
{
    public Vector3 Feet;
    public Vector2 Heading;
    public int Side;
    public Vector3 Cell;
    public ushort BlockId;
    public float Visibility;
    public float Persistence;
    public float Age;

    /// <summary>The print's opacity now (<see cref="FootprintRules.Alpha"/>).</summary>
    public readonly float Alpha => FootprintRules.Alpha(Visibility, Persistence, Age);
}

/// <summary>
/// The live footprints: one per footfall taken from <see cref="Footsteps"/>, tiered by the block stepped on
/// (<see cref="BlockType.FootprintOf"/>), aged out, capped at <see cref="FootprintConfig.Max"/> (the oldest
/// goes first) and dropped when the block under them is dug away. Cosmetic client state: never saved, never
/// sent, never spawned by a headless server. Main thread only.
/// </summary>
public sealed class FootprintSystem
{
    private readonly List<Footprint> _prints = [];
    private int _supportCursor;

    /// <summary>The live prints, oldest first.</summary>
    public IReadOnlyList<Footprint> Prints => _prints;

    /// <summary>Leaves the print of <paramref name="step"/>.</summary>
    public void Spawn(FootfallEvent step)
    {
        BlockType.FootprintOf(step.BlockId, out var visibility, out var persistence);
        if (visibility <= 0f || persistence <= 0f) return;
        if (_prints.Count >= FootprintConfig.Max) _prints.RemoveAt(0);
        _prints.Add(new Footprint
        {
            Feet = step.Feet, Heading = step.Heading, Side = step.Side, Cell = step.Cell, BlockId = step.BlockId,
            Visibility = visibility, Persistence = persistence,
        });
    }

    /// <summary>Ages every print, drops the faded ones and a few more each frame whose supporting block is gone.</summary>
    public void Update(float deltaTime, ChunkManager chunks)
    {
        for (var i = _prints.Count - 1; i >= 0; i--)
        {
            var print = _prints[i];
            print.Age += deltaTime;
            if (print.Age >= print.Persistence) _prints.RemoveAt(i);
            else _prints[i] = print;
        }

        var checks = System.Math.Min(FootprintConfig.SupportChecksPerFrame, _prints.Count);
        for (var n = 0; n < checks && _prints.Count > 0; n++)
        {
            _supportCursor %= _prints.Count;
            var print = _prints[_supportCursor];
            var cell = print.Cell;
            if (chunks.GetBlockGlobal((int)cell.X, (int)cell.Y, (int)cell.Z) != print.BlockId)
                _prints.RemoveAt(_supportCursor);
            else
                _supportCursor++;
        }
    }
}
