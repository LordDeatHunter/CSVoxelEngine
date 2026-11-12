using System;
using System.Collections.Generic;
using System.Linq;

namespace VoxelEngine.Content.Spells;

/// <summary>
/// A spell as the builder assembles it: one element, one shape, any distinct modifiers and at most one
/// trigger. <see cref="Compile"/> turns it into a cost, a validity verdict, a fizzle chance and the effect
/// factors. Everything comes from the parts' data: no rule here ever switches on a part's name, so a new
/// part is a JSON file. The numbers are computed even for an invalid recipe, so the builder can show them
/// while the player is still assembling it.
/// </summary>
public sealed class SpellRecipe
{
    private readonly SpellPart[] _parts;

    /// <summary>A recipe of these parts, in any order.</summary>
    public SpellRecipe(IEnumerable<SpellPart> parts) => _parts = parts.ToArray();

    /// <summary>The recipe's parts.</summary>
    public IReadOnlyList<SpellPart> Parts => _parts;

    /// <summary>
    /// The fizzle chance of a recipe of this summed complexity: 0 up to
    /// <see cref="SpellConfig.FizzleFreeComplexity"/>, then rising by
    /// <see cref="SpellConfig.FizzleChancePerComplexity"/> per unit, capped at
    /// <see cref="SpellConfig.FizzleMaxChance"/>. Never falls as the complexity grows.
    /// </summary>
    public static float FizzleChanceFor(float complexity) =>
        Math.Clamp((complexity - SpellConfig.FizzleFreeComplexity) * SpellConfig.FizzleChancePerComplexity,
            0f, SpellConfig.FizzleMaxChance);

    /// <summary>
    /// Compiles the recipe. Pure: no roll, no registry read. The verdict checks, in order: exactly one
    /// element, exactly one shape, at most one trigger, no modifier twice, and (when
    /// <paramref name="allowedTags"/> is given, the staff's set) every part allowed by it.
    /// </summary>
    public SpellStats Compile(IReadOnlySet<string>? allowedTags = null)
    {
        float shareSum = 0f, complexity = 0f, damage = 1f, range = 1f;
        int elements = 0, shapes = 0, triggers = 0;
        bool duplicate = false, disallowed = false;

        for (int i = 0; i < _parts.Length; i++)
        {
            var part = _parts[i];
            shareSum += part.CostShare;
            complexity += part.Complexity;
            damage *= part.DamageFactor;
            range *= part.RangeFactor;

            if (part.Kind == SpellPartKind.Element) elements++;
            else if (part.Kind == SpellPartKind.Shape) shapes++;
            else if (part.Kind == SpellPartKind.Trigger) triggers++;
            else if (RepeatsEarlierModifier(i)) duplicate = true;

            if (allowedTags != null && !part.IsAllowedBy(allowedTags)) disallowed = true;
        }

        var verdict = elements == 0 ? SpellVerdict.MissingElement
            : elements > 1 ? SpellVerdict.TooManyElements
            : shapes == 0 ? SpellVerdict.MissingShape
            : shapes > 1 ? SpellVerdict.TooManyShapes
            : triggers > 1 ? SpellVerdict.TooManyTriggers
            : duplicate ? SpellVerdict.DuplicateModifier
            : disallowed ? SpellVerdict.DisallowedPart
            : SpellVerdict.Valid;

        return new SpellStats(verdict, ManaConfig.StartMax * shareSum, complexity, FizzleChanceFor(complexity),
            damage, range);
    }

    /// <summary>Whether the modifier at <paramref name="index"/> has the key of a modifier before it.</summary>
    private bool RepeatsEarlierModifier(int index)
    {
        var key = _parts[index].Key;
        for (int j = 0; j < index; j++)
        {
            if (_parts[j].Kind == SpellPartKind.Modifier && _parts[j].Key == key) return true;
        }
        return false;
    }
}
