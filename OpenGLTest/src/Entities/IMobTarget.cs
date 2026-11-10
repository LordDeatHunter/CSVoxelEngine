using System.Numerics;
using VoxelEngine.Player;
using VoxelEngine.Status;

namespace VoxelEngine.Entities;

/// <summary>
/// A player a hostile mob can chase and hurt: the local <see cref="Camera"/> in singleplayer, or a
/// connected <c>ServerPlayer</c> on a dedicated server. The mob managers pick the nearest one per
/// mob (<see cref="MobTargets.Nearest"/>) and bill a landed hit through <see cref="TakeHit"/>.
/// </summary>
public interface IMobTarget
{
    /// <summary>The player's eye; the body box hangs <see cref="Camera.EyeHeight"/> below it.</summary>
    Vector3 Eye { get; }

    /// <summary>How fast and which way the player moves, in blocks per second: the ring spawners lead a
    /// moving player (<see cref="Simulation.SpawnOrigin"/>) and drop the mobs it leaves far behind
    /// (<see cref="MobTargets.OutOfPlay"/>). Zero for a target that does not report it.</summary>
    Vector3 Velocity => Vector3.Zero;

    /// <summary>Whether no mob hit can hurt this player right now (creative or flying). Mobs still
    /// chase an immune player when no other is in play, so a hovering camera can film them.</summary>
    bool IsImmune { get; }

    /// <summary>Whether the player sneaks, which halves the range a mob notices it from
    /// (<see cref="MobTargets.NoticeRadius"/>): the camera's own crouch, or a connected player's
    /// cosmetic flag, which is untrusted but can only ever help the one who claims it.</summary>
    bool IsSneaking => false;

    /// <summary>Whether the player flies (noclip): a flying body neither pushes nor is blocked by
    /// an iceberg. The camera's own flight flag, or a connected player's server-owned one.</summary>
    bool IsFlying => false;

    /// <summary>Whether the player is dead: a dead body pushes nothing. Only a connected player
    /// reports its server-owned death here; the local camera keeps the default.</summary>
    bool IsDead => false;

    /// <summary>The target's status effects, null where nothing may apply them (a stand-in target, a
    /// server's camera). <see cref="StatusEffects.Apply(IMobTarget, string, float)"/> applies through it.</summary>
    StatusEffectSet? Effects => null;

    /// <summary>A mob hit landed: take <paramref name="damage"/> and a knockback shove along the
    /// horizontal <paramref name="awayHorizontal"/> at <paramref name="knockbackSpeed"/> plus an
    /// upward <paramref name="lift"/>. <paramref name="attacker"/> is the mob's display name, which
    /// a death names in its message.</summary>
    void TakeHit(int damage, Vector3 awayHorizontal, float knockbackSpeed, float lift, string? attacker = null);
}
