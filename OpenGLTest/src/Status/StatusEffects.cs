using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Player;

namespace VoxelEngine.Status;

/// <summary>
/// The one way to put a status effect on something (what a spell's element applies on hit): a mob, a
/// player as a mob target (the local camera, a server's connected player), or a player's own pool. Each
/// overload looks the effect up by its file name (<see cref="StatusEffectRegistry.Require"/>, which
/// throws naming an unknown one) and applies it for its duration times <c>durationScale</c>; a refresh
/// never stacks (<see cref="StatusEffectSet.Apply"/>). Every overload is for the authoritative
/// simulation only (singleplayer and the server): a client never calls them, since its effects are the
/// server's mirror. Main thread only.
/// </summary>
public static class StatusEffects
{
    /// <summary>Applies <paramref name="effectName"/> to <paramref name="mob"/>; refused (false) for a
    /// dead or dying mob, or a duration scale that leaves no time.</summary>
    public static bool Apply(Mob mob, string effectName, float durationScale = 1f)
    {
        if (mob.IsDying || mob.IsDead)
            return false;
        return mob.Effects.Apply(StatusEffectRegistry.Require(effectName), durationScale);
    }

    /// <summary>Applies <paramref name="effectName"/> to a player as a mob target; refused (false) for
    /// an immune (creative, flying) or dead target, a target with no effect set, or a duration scale
    /// that leaves no time.</summary>
    public static bool Apply(IMobTarget target, string effectName, float durationScale = 1f)
    {
        if (target.IsImmune || target.IsDead || target.Effects is not { } effects)
            return false;
        return effects.Apply(StatusEffectRegistry.Require(effectName), durationScale);
    }

    /// <summary>Applies <paramref name="effectName"/> to a player's own pool (a caster's, through
    /// <see cref="PlayerContext.Health"/>); refused (false) while <paramref name="immune"/> (creative,
    /// flying) or dead, or for a duration scale that leaves no time.</summary>
    public static bool Apply(PlayerHealth health, bool immune, string effectName, float durationScale = 1f)
    {
        if (immune || health.IsDead)
            return false;
        return health.Effects.Apply(StatusEffectRegistry.Require(effectName), durationScale);
    }
}
