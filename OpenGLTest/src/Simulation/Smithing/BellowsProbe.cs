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
        var y = MathUtils.FloorToInt(feet.Y - BelowFeet);
        ReadOnlySpan<(float X, float Z)> probes =
        [
            (0f, 0f), (-halfWidth, -halfWidth), (halfWidth, -halfWidth), (-halfWidth, halfWidth), (halfWidth, halfWidth),
        ];
        foreach (var (dx, dz) in probes)
        {
            cell = (MathUtils.FloorToInt(feet.X + dx), y, MathUtils.FloorToInt(feet.Z + dz));
            if (BlockType.ById[chunks.GetBlockGlobal(cell.X, cell.Y, cell.Z)] is { IsBellows: true })
                return true;
        }
        cell = default;
        return false;
    }
}
