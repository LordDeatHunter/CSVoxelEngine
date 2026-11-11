namespace VoxelEngine.Magic;

/// <summary>What one <see cref="SpellProjectile.Update"/> left a spell bolt doing.</summary>
internal enum SpellFlight
{
    /// <summary>Still in flight.</summary>
    Flying,

    /// <summary>It touched a mob (<see cref="SpellProjectile.HitMob"/>).</summary>
    HitMob,

    /// <summary>It landed on a cell (<see cref="SpellProjectile.ImpactCell"/>, <see cref="SpellProjectile.ImpactNormal"/>).</summary>
    HitCell,

    /// <summary>It flew its whole range without touching anything.</summary>
    Spent,
}
