using System;

namespace silkgltriangle;

/// <summary>
/// GL-free per-size slime rules. A slime's size is an integer from <see cref="Min"/> to
/// <see cref="Max"/>, and every stat it scales is the size-1 value times
/// <c>1 + (size - 1) * step</c> with one step per stat in <see cref="WorldGenerationConfig"/>
/// (<c>SlimeSize*Step</c>), so no stat branches on a particular size.
/// </summary>
public static class SlimeSize
{
    /// <summary>The plain, smallest slime.</summary>
    public const int Min = 1;

    /// <summary>The largest size a slime can have.</summary>
    public const int Max = WorldGenerationConfig.SlimeMaxSize;

    /// <summary><paramref name="size"/> clamped into <see cref="Min"/>..<see cref="Max"/>, so a
    /// stale save or an old server's zero still names a real size.</summary>
    public static int Clamp(int size) => Math.Clamp(size, Min, Max);

    /// <summary>The multiplier <c>1 + (size - 1) * step</c> of a stat that grows by
    /// <paramref name="step"/> per size.</summary>
    public static float Multiplier(int size, float step) => 1f + (Clamp(size) - Min) * step;

    /// <summary>The body scale: the drawn model and the collision box are the size-1 ones times this.</summary>
    public static float Scale(int size) => Multiplier(size, WorldGenerationConfig.SlimeSizeScaleStep);

    /// <summary>The health pool of a slime whose kind's JSON pool is <paramref name="baseHealth"/>.</summary>
    public static int Health(int baseHealth, int size) =>
        Math.Max(1, (int)MathF.Round(baseHealth * Multiplier(size, WorldGenerationConfig.SlimeSizeHealthStep)));

    /// <summary>The damage one airborne touch deals.</summary>
    public static int ContactDamage(int size) =>
        (int)MathF.Round(WorldGenerationConfig.SlimeContactDamage * Multiplier(size, WorldGenerationConfig.SlimeSizeDamageStep));

    /// <summary>How many times the slime's loot table is rolled on its death.</summary>
    public static int LootRolls(int size) =>
        Math.Max(1, (int)MathF.Round(Multiplier(size, WorldGenerationConfig.SlimeSizeLootStep)));
}
