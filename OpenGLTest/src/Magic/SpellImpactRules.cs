using System;
using System.Collections.Generic;
using VoxelEngine.Content.Spells;
using VoxelEngine.Content.StatusEffects;

namespace VoxelEngine.Magic;

/// <summary>
/// The pure rules of what a cast spell does, GL-free: a bolt's direct damage and flight length from the
/// recipe's factors, which parts are its element and shape, and which of an element's impacts are block
/// conversions the <see cref="VoxelEngine.Status.StatusEffectBlockWriter"/> makes.
/// </summary>
public static class SpellImpactRules
{
    /// <summary>
    /// The direct damage a bolt deals a mob: <see cref="SpellConfig.BaseDamage"/> times the recipe's damage
    /// factor, rounded half away from zero, never below 1. Never falls as the factor grows.
    /// </summary>
    public static int DamageFor(SpellStats stats) =>
        Math.Max(1, (int)MathF.Round(SpellConfig.BaseDamage * stats.DamageFactor, MidpointRounding.AwayFromZero));

    /// <summary>How far a bolt flies before it is spent: <see cref="SpellConfig.ProjectileRangeBlocks"/> times the range factor.</summary>
    public static float RangeFor(SpellStats stats) => SpellConfig.ProjectileRangeBlocks * stats.RangeFactor;

    /// <summary>The recipe's element, the first element part; null when it has none.</summary>
    public static SpellPart? ElementOf(IReadOnlyList<SpellPart> parts) => FirstOf(parts, SpellPartKind.Element);

    /// <summary>The recipe's shape, the first shape part; null when it has none.</summary>
    public static SpellPart? ShapeOf(IReadOnlyList<SpellPart> parts) => FirstOf(parts, SpellPartKind.Shape);

    /// <summary>
    /// The block conversions among an element's impacts: ignite, freeze and thaw become the matching
    /// <see cref="StatusEffectBlockAction"/> flags; prime, light and grow are not conversions and are dropped.
    /// </summary>
    public static StatusEffectBlockAction BlockActionsOf(SpellImpact impact)
    {
        var actions = StatusEffectBlockAction.None;
        if ((impact & SpellImpact.Ignite) != 0) actions |= StatusEffectBlockAction.Ignite;
        if ((impact & SpellImpact.Freeze) != 0) actions |= StatusEffectBlockAction.Freeze;
        if ((impact & SpellImpact.Thaw) != 0) actions |= StatusEffectBlockAction.Thaw;
        return actions;
    }

    private static SpellPart? FirstOf(IReadOnlyList<SpellPart> parts, SpellPartKind kind)
    {
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Kind == kind) return parts[i];
        }
        return null;
    }
}
