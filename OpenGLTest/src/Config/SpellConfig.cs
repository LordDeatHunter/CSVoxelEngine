namespace VoxelEngine.Config;

/// <summary>
/// Spell recipe rules beside <see cref="ManaConfig"/>: when a recipe's summed complexity starts to risk a
/// fizzle, and how fast that risk grows. The part costs themselves come from the part JSON
/// (<c>assets/data/spell/&lt;kind&gt;/&lt;name&gt;.json</c>). The values are first guesses.
/// </summary>
public static class SpellConfig
{
    /// <summary>The summed part complexity a recipe may reach before it can fizzle at all.</summary>
    public const float FizzleFreeComplexity = 3f;

    /// <summary>The fizzle chance added per unit of complexity past <see cref="FizzleFreeComplexity"/>.</summary>
    public const float FizzleChancePerComplexity = 0.05f;

    /// <summary>The highest fizzle chance any recipe reaches, however complex.</summary>
    public const float FizzleMaxChance = 0.75f;

    /// <summary>The most spell slots a staff's JSON may declare (a first guess).</summary>
    public const int StaffMaxSlots = 8;
}
