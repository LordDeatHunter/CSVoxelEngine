namespace VoxelEngine.Content.Spells;

/// <summary>
/// What a <see cref="SpellRecipe"/> compiles to. <paramref name="Cost"/> is in mana
/// (<see cref="VoxelEngine.Config.ManaConfig.StartMax"/> times the summed part shares), the same unit
/// <see cref="VoxelEngine.Player.PlayerMana.TrySpend"/> takes. Every number is computed even for an invalid
/// recipe, so a builder can show it; a cast must check <see cref="IsValid"/> first.
/// </summary>
/// <param name="Verdict">Whether the recipe may be cast, or the first rule it breaks.</param>
/// <param name="Cost">The mana one cast spends.</param>
/// <param name="Complexity">The parts' summed complexity.</param>
/// <param name="FizzleChance">The chance, 0..1, that a cast fizzles (<see cref="SpellRecipe.FizzleChanceFor"/>).</param>
/// <param name="DamageFactor">The product of the parts' damage factors.</param>
/// <param name="RangeFactor">The product of the parts' range factors.</param>
public readonly record struct SpellStats(SpellVerdict Verdict, float Cost, float Complexity, float FizzleChance,
    float DamageFactor, float RangeFactor)
{
    /// <summary>Whether the recipe may be cast.</summary>
    public bool IsValid => Verdict == SpellVerdict.Valid;
}
