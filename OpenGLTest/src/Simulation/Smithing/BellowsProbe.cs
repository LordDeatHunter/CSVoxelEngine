using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Simulation.Smithing;

/// <summary>
/// Which bellows block a landing body came down on: the cell a hair under its feet, at the body's middle first and
/// then at the four corners of its footprint, so a body standing half over the edge of a bellows still works it.
/// GL-free; the player, the server's players and the mobs all land through it (<see cref="World.PressBellows"/>).
/// </summary>
public static class BellowsProbe
{
    // How far under the feet the probe reads, so a body resting flush on a block top finds that block
    private const float BelowFeet = 0.05f;

    /// <summary>The bellows cell under a body whose feet are at <paramref name="feet"/> and whose footprint is
    /// <paramref name="halfWidth"/> to each side; false when none lies there.</summary>
    public static bool TryFind(ChunkManager chunks, Vector3 feet, float halfWidth, out (int X, int Y, int Z) cell)
    {
        foreach (var probe in CellsUnder(feet, halfWidth))
        {
            cell = probe;
            if (BlockType.ById[chunks.GetBlockGlobal(cell.X, cell.Y, cell.Z)] is { IsBellows: true })
                return true;
        }
        cell = default;
        return false;
    }

    /// <summary>The cells a hair under a body's feet, whatever they hold: under its middle first, then under the four
    /// corners of its footprint (a cell under several of them comes up more than once).</summary>
    public static CellProbes CellsUnder(Vector3 feet, float halfWidth) => new(feet, halfWidth);

    /// <summary>The five probe cells of <see cref="CellsUnder"/>, enumerated without allocating.</summary>
    public readonly struct CellProbes(Vector3 feet, float halfWidth)
    {
        /// <summary>Starts the enumeration.</summary>
        public Enumerator GetEnumerator() => new(feet, halfWidth);

        /// <summary>Walks the middle and the four corners.</summary>
        public struct Enumerator(Vector3 feet, float halfWidth)
        {
            private int _index = -1;

            /// <summary>The probe cell at the enumerator's position.</summary>
            public readonly (int X, int Y, int Z) Current
            {
                get
                {
                    // 0 is the middle; 1..4 are the corners, x from bit 0 and z from bit 1 of (index - 1)
                    var dx = _index == 0 ? 0f : ((_index - 1) & 1) == 0 ? -halfWidth : halfWidth;
                    var dz = _index == 0 ? 0f : ((_index - 1) & 2) == 0 ? -halfWidth : halfWidth;
                    return (MathUtils.FloorToInt(feet.X + dx), MathUtils.FloorToInt(feet.Y - BelowFeet),
                        MathUtils.FloorToInt(feet.Z + dz));
                }
            }

            /// <summary>Moves to the next probe; false past the last.</summary>
            public bool MoveNext() => ++_index < 5;
        }
    }
}
