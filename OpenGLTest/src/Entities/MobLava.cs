using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Player;

namespace VoxelEngine.Entities;

/// <summary>
/// The mobs' lava rule, GL-free: the body box touches a burning fluid (a fluid with a light
/// emission: lava), the same cell test (<see cref="BurningFluid.AnyInBox"/>) <see cref="PlayerBodyProbe.InLava"/>
/// runs over the player's box, and every <see cref="MobConfig.MobLavaTickSeconds"/> spent in it costs
/// <see cref="MobConfig.MobLavaDamageShare"/> of the mob's full health (at least 1).
/// <see cref="Mob.UpdateHazards"/> applies it in the authoritative simulation only.
/// </summary>
public static class MobLava
{
    /// <summary>
    /// Whether any cell of the body box centered at <paramref name="center"/> (horizontal half extent
    /// <paramref name="halfExtent"/>, vertical <paramref name="halfHeight"/>) holds a burning fluid.
    /// An unloaded cell is not lava.
    /// </summary>
    public static bool BodyInLava(Vector3 center, float halfExtent, float halfHeight, ChunkManager chunks)
    {
        var half = new Vector3(halfExtent, halfHeight, halfExtent);
        return BurningFluid.AnyInBox(center - half, center + half, chunks);
    }

    /// <summary>Health one lava tick takes off a mob whose full health is <paramref name="maxHealth"/>.</summary>
    public static int DamagePerTick(int maxHealth) =>
        Math.Max(1, (int)MathF.Ceiling(maxHealth * MobConfig.MobLavaDamageShare));
}
