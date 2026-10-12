using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Player;

namespace VoxelEngine.Entities;

/// <summary>
/// The mobs' lava rule, GL-free: the body box touches a burning fluid (a fluid with a light
/// emission: lava), the same cell test <see cref="PlayerBodyProbe.InLava"/> runs over the player's
/// box, and every <see cref="WorldGenerationConfig.MobLavaTickSeconds"/> spent in it costs
/// <see cref="WorldGenerationConfig.MobLavaDamageShare"/> of the mob's full health (at least 1).
/// <see cref="Mob.UpdateHazards"/> applies it in the authoritative simulation only.
/// </summary>
public static class MobLava
{
    /// <summary>
    /// Whether any cell of the body box centred at <paramref name="centre"/> (horizontal half extent
    /// <paramref name="halfExtent"/>, vertical <paramref name="halfHeight"/>) holds a burning fluid.
    /// An unloaded cell is not lava.
    /// </summary>
    public static bool BodyInLava(Vector3 centre, float halfExtent, float halfHeight, ChunkManager chunks)
    {
        var half = new Vector3(halfExtent, halfHeight, halfExtent);
        VoxelCoords.CellRange(centre - half, centre + half,
            out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            if (chunks.TryGetCell(x, y, z, out var id, out _) && BlockType.IsFluid(id) && BlockType.Emission[id] > 0)
                return true;
        }
        return false;
    }

    /// <summary>Health one lava tick takes off a mob whose full health is <paramref name="maxHealth"/>.</summary>
    public static int DamagePerTick(int maxHealth) =>
        Math.Max(1, (int)MathF.Ceiling(maxHealth * WorldGenerationConfig.MobLavaDamageShare));
}
