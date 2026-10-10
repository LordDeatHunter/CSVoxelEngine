namespace silkgltriangle;

/// <summary>
/// One mob kind's live population as the global caps see it (<see cref="MobCaps"/>): every mob
/// manager registers itself, so a kind added later is counted without a cap of its own.
/// </summary>
public interface IMobPopulation
{
    /// <summary>Mobs of this kind that are alive; a mob playing its death
    /// (<see cref="Mob.IsDying"/>) no longer counts.</summary>
    int LivingCount { get; }
}
