using System;
using System.Collections.Concurrent;

namespace VoxelEngine.Chunks.Meshing;

/// <summary>
/// The cosmetic notches an axe is cutting into the trunk cells it chops: per world cell, which side
/// face the bite is on and how deep it is, in steps. The mesher reads it (on its pool threads) when it
/// emits a full-cube wood cell, so a notched cell is drawn as the cube minus a bite
/// (<see cref="TreeWoodEmitter.EmitNotched"/>); the hitbox and collision stay the whole cube. It is
/// per world, owned by <see cref="ChunkManager"/>, written by the local player's mining only (the
/// main thread) and never saved or sent: a client shows its own, a server none.
/// </summary>
public sealed class WoodNotches
{
    /// <summary>The deepest notch, in steps.</summary>
    public const int MaxSteps = 4;

    /// <summary>Pixels (of the 16 across a cell) each step bites in.</summary>
    public const int PixelsPerStep = 2;

    private readonly ConcurrentDictionary<long, byte> _cells = new();

    /// <summary>
    /// The notch step for a block that has taken <paramref name="damage"/> of <paramref name="hardness"/>:
    /// 0 untouched, rising by one at each equal share of the hardness up to <see cref="MaxSteps"/>, which
    /// the last hit before the break stays under (the block is gone at full damage). Never decreases as
    /// the damage grows.
    /// </summary>
    public static int StepFor(float damage, float hardness)
    {
        if (hardness <= 0f || damage <= 0f) return 0;
        var share = Math.Clamp(damage / hardness, 0f, 1f);
        return Math.Min(MaxSteps, (int)MathF.Floor(share * (MaxSteps + 1)));
    }

    /// <summary>Records a notch of <paramref name="step"/> steps on side <paramref name="face"/> (0..3, the
    /// horizontal engine faces) of the cell; a step of 0 clears it. Returns whether the stored notch changed.</summary>
    public bool Set(int x, int y, int z, int face, int step)
    {
        var key = CellKey.Pack(x, y, z);
        if (step <= 0) return _cells.TryRemove(key, out _);
        var packed = (byte)(face | Math.Min(step, MaxSteps) << 2);
        if (_cells.TryGetValue(key, out var old) && old == packed) return false;
        _cells[key] = packed;
        return true;
    }

    /// <summary>Whether the cell has a notch, with its side face and step.</summary>
    public bool TryGet(int x, int y, int z, out int face, out int step)
    {
        face = 0;
        step = 0;
        if (_cells.IsEmpty || !_cells.TryGetValue(CellKey.Pack(x, y, z), out var packed)) return false;
        face = packed & 3;
        step = packed >> 2;
        return true;
    }
}
