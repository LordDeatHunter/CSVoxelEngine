using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// A player a hostile mob can chase and hurt: the local <see cref="Camera"/> in singleplayer, or a
/// connected <c>ServerPlayer</c> on a dedicated server. The mob managers pick the nearest one per
/// mob (<see cref="MobTargets.Nearest"/>) and bill a landed hit through <see cref="TakeHit"/>.
/// </summary>
public interface IMobTarget
{
    /// <summary>The player's eye; the body box hangs <see cref="Camera.EyeHeight"/> below it.</summary>
    Vector3 Eye { get; }

    /// <summary>Whether no mob hit can hurt this player right now (creative or flying). Mobs still
    /// chase an immune player when no other is in play, so a hovering camera can film them.</summary>
    bool IsImmune { get; }

    /// <summary>Whether the player sneaks, which halves the range a mob notices it from
    /// (<see cref="MobTargets.NoticeRadius"/>): the camera's own crouch, or a connected player's
    /// cosmetic flag, which is untrusted but can only ever help the one who claims it.</summary>
    bool IsSneaking => false;

    /// <summary>A mob hit landed: take <paramref name="damage"/> and a knockback shove along the
    /// horizontal <paramref name="awayHorizontal"/> at <paramref name="knockbackSpeed"/> plus an
    /// upward <paramref name="lift"/>.</summary>
    void TakeHit(int damage, Vector3 awayHorizontal, float knockbackSpeed, float lift);
}
