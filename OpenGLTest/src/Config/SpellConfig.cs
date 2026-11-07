using System.Numerics;

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

    /// <summary>
    /// The shortest gap in seconds between two casts by one caster, locally and on the server
    /// (<see cref="VoxelEngine.Magic.CastPacing"/>; a first guess).
    /// </summary>
    public const float CastCooldownSeconds = 0.5f;

    /// <summary>How fast a spell bolt flies, in blocks per second (it flies straight, with no gravity).</summary>
    public const float ProjectileSpeed = 24f;

    /// <summary>How far a spell bolt flies before it is spent, in blocks, before the recipe's range factor.</summary>
    public const float ProjectileRangeBlocks = 24f;

    /// <summary>Half the edge of a spell bolt's collision box, in blocks.</summary>
    public const float ProjectileHalfExtent = 0.15f;

    /// <summary>
    /// The longest sub-step a bolt moves in one go, in blocks, so a slow frame never carries it past a
    /// one-cell fluid layer or a thin mob.
    /// </summary>
    public const float ProjectileMaxStepBlocks = 0.25f;

    /// <summary>The most spell bolts one world keeps in flight; a new one past it replaces the oldest.</summary>
    public const int MaxLiveProjectiles = 64;

    /// <summary>How far in front of the caster's eye a bolt starts, in blocks.</summary>
    public const float ProjectileLaunchOffsetBlocks = 0.6f;

    /// <summary>The direct damage a bolt deals a mob before the recipe's damage factor.</summary>
    public const float BaseDamage = 4f;

    /// <summary>The cube radius, in cells, of the block conversions an impact makes around the cell it lands on.</summary>
    public const int ImpactReachBlocks = 1;

    /// <summary>Seconds between two trail sparks of a flying bolt (cosmetic).</summary>
    public const float BoltSparkSeconds = 0.03f;

    /// <summary>Sparks a flying bolt leaves per trail puff (cosmetic).</summary>
    public const int BoltSparkCount = 2;

    /// <summary>Launch speed of a bolt's trail sparks, in blocks per second (cosmetic).</summary>
    public const float BoltSparkSpeed = 0.6f;

    /// <summary>Edge of a bolt's spark cubes, in blocks (cosmetic).</summary>
    public const float BoltSparkSize = 0.06f;

    /// <summary>Sparks a bolt bursts into where it lands (cosmetic).</summary>
    public const int ImpactSparkCount = 10;

    /// <summary>Launch speed of a bolt's impact sparks, in blocks per second (cosmetic).</summary>
    public const float ImpactSparkSpeed = 2.5f;

    /// <summary>The sRGB spark color of an element whose status effect has no tint (or that applies none).</summary>
    public static readonly Vector3 BoltDefaultColor = new(0.8f, 0.7f, 1.0f);
}
