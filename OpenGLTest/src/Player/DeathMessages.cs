namespace VoxelEngine.Player;

/// <summary>What dealt the last blow to a player's pool, for the death message.</summary>
public enum DamageSource : byte
{
    /// <summary>Nothing has hurt the pool (or the source was not recorded).</summary>
    None,

    /// <summary>A mob's hit: a slime's contact, a goblin's stone or swing.</summary>
    Mob,

    /// <summary>A landing past <see cref="PlayerHealth.FallMinBlocks"/>.</summary>
    Fall,

    /// <summary>Lava contact.</summary>
    Lava,

    /// <summary>Standing against a cactus.</summary>
    Cactus,
}

/// <summary>
/// The death message, GL-free: the one system chat line a player's death posts, named for what
/// dealt the lethal blow — "Player was slain by Goblin", "Player fell", "Player burned in lava".
/// The wording lives here so singleplayer and a server say the same thing.
/// </summary>
public static class DeathMessages
{
    /// <summary>The line for a death by <paramref name="source"/>; <paramref name="killer"/> is the
    /// mob's display name when one dealt it.</summary>
    public static string For(string player, DamageSource source, string? killer) => source switch
    {
        DamageSource.Mob when !string.IsNullOrEmpty(killer) => $"{player} was slain by {killer}",
        DamageSource.Mob => $"{player} was slain",
        DamageSource.Fall => $"{player} fell",
        DamageSource.Lava => $"{player} burned in lava",
        DamageSource.Cactus => $"{player} was pricked to death",
        _ => $"{player} died",
    };
}
