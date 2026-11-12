using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Particles;

namespace VoxelEngine.Content.StatusEffects;

/// <summary>
/// Loads <c>data/effects/&lt;name&gt;.json</c> into <see cref="StatusEffectRegistry"/>. A missing folder is
/// no effects; ids are positions in file-name order, at most <see cref="StatusEffect.MaxKinds"/> of them,
/// and every effect error names its file. <see cref="ReadEffects"/> and <see cref="Compile"/> touch no
/// static state, so tests call them directly instead of replacing the published registry. Runs before the
/// spell parts, whose element <c>effect</c> field names an effect.
/// </summary>
internal static class StatusEffectLoader
{
    private static readonly string[] ParticleNames = Enum.GetNames<FireParticleKind>();

    internal static void LoadEffects(string folder) => StatusEffectRegistry.Publish(ReadEffects(folder));

    /// <summary>Every effect under <paramref name="folder"/> in id order; empty when the folder is missing.</summary>
    internal static List<StatusEffect> ReadEffects(string folder)
    {
        var effects = new List<StatusEffect>();
        if (!Directory.Exists(folder)) return effects;

        var files = ContentFiles.JsonFilesIn(folder);
        if (files.Count > StatusEffect.MaxKinds)
            throw new InvalidOperationException(
                $"Status effect folder '{folder}' holds {files.Count} effects; at most {StatusEffect.MaxKinds} fit the one-byte effect masks.");

        for (var i = 0; i < files.Count; i++)
        {
            var (name, path) = files[i];
            effects.Add(Compile(name, ContentFiles.ReadJson<StatusEffectDefinition>(path), path, (byte)i));
        }
        return effects;
    }

    /// <summary>Compiles one effect, throwing an error that names <paramref name="path"/> on any bad field.</summary>
    internal static StatusEffect Compile(string name, StatusEffectDefinition definition, string path, byte id)
    {
        if (definition.DurationSeconds is not { } duration || !float.IsFinite(duration) || duration <= 0f)
            throw new InvalidOperationException(
                $"Status effect '{path}' has durationSeconds {definition.DurationSeconds}; expected more than 0.");

        var damageShare = 0f;
        if (definition.DamageShare is { } share)
        {
            if (!float.IsFinite(share) || share <= 0f || share > 1f)
                throw new InvalidOperationException(
                    $"Status effect '{path}' has damageShare {share}; expected a share of full health in 0..1, exclusive of 0.");
            damageShare = share;
        }

        var moveFactor = CheckFactor(definition.MoveFactor, "moveFactor", path);
        var attackFactor = CheckFactor(definition.AttackFactor, "attackFactor", path);

        var blocks = StatusEffectBlockAction.None;
        if (definition.Blocks is { } b)
        {
            if (b.Ignite == true) blocks |= StatusEffectBlockAction.Ignite;
            if (b.Freeze == true) blocks |= StatusEffectBlockAction.Freeze;
            if (b.Thaw == true) blocks |= StatusEffectBlockAction.Thaw;
        }

        // A tick only exists to deal damage or to act on blocks, so its interval is required exactly then
        var ticks = damageShare > 0f || blocks != StatusEffectBlockAction.None;
        var tickSeconds = 0f;
        if (ticks)
        {
            if (definition.TickSeconds is not { } tick || !float.IsFinite(tick) || tick <= 0f)
                throw new InvalidOperationException(
                    $"Status effect '{path}' has tickSeconds {definition.TickSeconds}; an effect that deals damage or acts on blocks needs more than 0.");
            tickSeconds = tick;
        }
        else if (definition.TickSeconds != null)
        {
            throw new InvalidOperationException(
                $"Status effect '{path}' sets tickSeconds but deals no damage and acts on no blocks.");
        }

        if (!ticks && moveFactor >= 1f && attackFactor >= 1f)
            throw new InvalidOperationException(
                $"Status effect '{path}' does nothing; it needs damage, a moveFactor or attackFactor below 1, or a block action.");

        Vector3? tint = null;
        if (definition.Tint != null)
        {
            if (definition.Tint is not { Length: 3 } color || color.Any(c => c is < 0 or > 255))
                throw new InvalidOperationException(
                    $"Status effect '{path}' has a tint that is not three integers in 0..255.");
            tint = ColorSpace.SrgbToLinear(new Vector3(color[0], color[1], color[2]) / 255f);
        }

        FireParticleKind? particle = null;
        if (definition.Particle != null)
        {
            // Matched by name, never Enum.TryParse, which accepts numbers
            var match = Array.FindIndex(ParticleNames,
                n => string.Equals(n, definition.Particle, StringComparison.OrdinalIgnoreCase));
            if (match < 0)
                throw new InvalidOperationException(
                    $"Status effect '{path}' has particle '{definition.Particle}'; expected one of {string.Join(", ", ParticleNames)}.");
            particle = Enum.Parse<FireParticleKind>(ParticleNames[match]);
        }

        if (definition.Icon != null
            && (string.IsNullOrWhiteSpace(definition.Icon)
                || !definition.Icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Status effect '{path}' has icon '{definition.Icon}'; expected a .png texture path.");

        if (definition.DeathMessage != null && string.IsNullOrWhiteSpace(definition.DeathMessage))
            throw new InvalidOperationException($"Status effect '{path}' has a blank deathMessage.");

        return new StatusEffect(id, name, definition.Name ?? name, duration, tickSeconds, damageShare,
            moveFactor, attackFactor, blocks, tint, particle, definition.Icon, definition.DeathMessage);
    }

    /// <summary>A slowing factor: 1 when unset; otherwise finite and in 0..1, exclusive of 0.</summary>
    private static float CheckFactor(float? factor, string field, string path)
    {
        if (factor is not { } value) return 1f;
        if (!float.IsFinite(value) || value <= 0f || value > 1f)
            throw new InvalidOperationException(
                $"Status effect '{path}' has {field} {value}; expected a slowing factor in 0..1, exclusive of 0.");
        return value;
    }
}
