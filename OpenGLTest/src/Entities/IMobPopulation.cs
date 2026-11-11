namespace VoxelEngine.Entities;

/// <summary>
/// One mob kind's live population as the global caps see it (<see cref="MobCaps"/>): every mob
/// manager registers itself, so a kind added later is counted without a cap of its own.
/// </summary>
public interface IMobPopulation
{
    /// <summary>Mobs of this kind that are alive; a mob playing its death
    /// (<see cref="Mob.IsDying"/>) no longer counts.</summary>
    int LivingCount { get; }

    /// <summary>Whether these are animals of the wild (deer, boars), which fill their own budget
    /// (<see cref="MobCaps.WildlifeCap"/>) instead of the hostile natural cap, so a herd never takes an
    /// enemy's place nor the other way round. False for every hostile kind.</summary>
    bool IsWildlife => false;
}
