namespace VoxelEngine.Content.Definitions;

/// <summary>
/// JSON shape of <c>assets/data/effects/&lt;name&gt;.json</c>: one status effect (burning, frostburn) a
/// spell element applies to a mob or a player. Compiled by
/// <see cref="VoxelEngine.Content.StatusEffects.StatusEffectLoader"/>.
/// </summary>
public sealed class StatusEffectDefinition
{
    /// <summary>Display name; default the file name.</summary>
    public string? Name { get; set; }

    /// <summary>How long one application lasts, in seconds, above 0. Required.</summary>
    public float? DurationSeconds { get; set; }

    /// <summary>
    /// Seconds between two ticks (damage and block actions), above 0. Required exactly when the effect
    /// deals damage or declares a block action.
    /// </summary>
    public float? TickSeconds { get; set; }

    /// <summary>The share of the target's full health one tick deals, in 0..1 exclusive of 0 (at least 1 point).</summary>
    public float? DamageShare { get; set; }

    /// <summary>The factor the target's movement speed is multiplied by, in 0..1 exclusive of 0; default 1.</summary>
    public float? MoveFactor { get; set; }

    /// <summary>The factor the target's attack pace is multiplied by, in 0..1 exclusive of 0; default 1.</summary>
    public float? AttackFactor { get; set; }

    /// <summary>What a tick does to the cells around the target; every flag defaults to false.</summary>
    public BlocksDefinition? Blocks { get; set; }

    /// <summary>The sRGB tint, three integers in 0..255, an affected mob is drawn with.</summary>
    public int[]? Tint { get; set; }

    /// <summary>The fire particle kind (<c>ember</c>, <c>smoke</c>, <c>steam</c>, <c>spark</c>) an affected mob gives off.</summary>
    public string? Particle { get; set; }

    /// <summary>The HUD icon (a <c>.png</c> path) shown while the player has the effect.</summary>
    public string? Icon { get; set; }

    /// <summary>The death line phrase after the player's name when the effect kills ("burned to death").</summary>
    public string? DeathMessage { get; set; }

    /// <summary>The block actions an effect's tick runs around its target.</summary>
    public sealed class BlocksDefinition
    {
        /// <summary>Flammable blocks burn away to air.</summary>
        public bool? Ignite { get; set; }

        /// <summary>A fluid source below the target's feet freezes to its <c>freezesTo</c> block.</summary>
        public bool? Freeze { get; set; }

        /// <summary>A block with a <c>meltsTo</c> fluid melts into a source of it.</summary>
        public bool? Thaw { get; set; }
    }
}
