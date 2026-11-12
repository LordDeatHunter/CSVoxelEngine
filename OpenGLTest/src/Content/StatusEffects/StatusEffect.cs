using System.Numerics;
using VoxelEngine.Particles;

namespace VoxelEngine.Content.StatusEffects;

/// <summary>
/// One compiled status effect from <c>assets/data/effects/&lt;name&gt;.json</c>, immutable after
/// <see cref="StatusEffectLoader"/> builds it. Its <see cref="Id"/> is its position in file-name order,
/// and <see cref="Bit"/> is what every wire mask carries.
/// </summary>
public sealed class StatusEffect
{
    /// <summary>How many effect kinds may exist: the entity and player status wire masks are one byte.</summary>
    public const int MaxKinds = 8;

    internal StatusEffect(byte id, string name, string displayName, float durationSeconds, float tickSeconds,
        float damageShare, float moveFactor, float attackFactor, StatusEffectBlockAction blockActions,
        Vector3? tintLinear, FireParticleKind? particle, string? iconPath, string? deathMessage)
    {
        Id = id;
        Name = name;
        DisplayName = displayName;
        DurationSeconds = durationSeconds;
        TickSeconds = tickSeconds;
        DamageShare = damageShare;
        MoveFactor = moveFactor;
        AttackFactor = attackFactor;
        BlockActions = blockActions;
        TintLinear = tintLinear;
        Particle = particle;
        IconPath = iconPath;
        DeathMessage = deathMessage;
    }

    /// <summary>The effect's index in file-name order, below <see cref="MaxKinds"/>.</summary>
    public byte Id { get; }

    /// <summary>The JSON file name.</summary>
    public string Name { get; }

    public string DisplayName { get; }

    /// <summary>How long one application lasts, in seconds.</summary>
    public float DurationSeconds { get; }

    /// <summary>Seconds between two ticks; 0 when the effect never ticks.</summary>
    public float TickSeconds { get; }

    /// <summary>The share of the target's full health one tick deals; 0 for none.</summary>
    public float DamageShare { get; }

    /// <summary>The movement speed factor; 1 when the effect does not slow movement.</summary>
    public float MoveFactor { get; }

    /// <summary>The attack pace factor; 1 when the effect does not slow attacks.</summary>
    public float AttackFactor { get; }

    /// <summary>What each tick does to the cells around the target.</summary>
    public StatusEffectBlockAction BlockActions { get; }

    /// <summary>The mob tint in linear light, or null for none.</summary>
    public Vector3? TintLinear { get; }

    /// <summary>The particle an affected mob gives off, or null for none.</summary>
    public FireParticleKind? Particle { get; }

    /// <summary>The HUD icon texture path, or null for none.</summary>
    public string? IconPath { get; }

    /// <summary>The death phrase after the player's name, or null for the generic magic line.</summary>
    public string? DeathMessage { get; }

    /// <summary>This effect's bit in a one-byte effect mask.</summary>
    public byte Bit => (byte)(1 << Id);
}
