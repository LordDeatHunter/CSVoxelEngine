namespace VoxelEngine.Entities.TowerMobs;

/// <summary>What the Skeleton King's brain (<see cref="SkeletonKingBrain.Choose"/>) picks for one decision.</summary>
public enum SkeletonKingAttack
{
    /// <summary>No attack: the king walks at its target.</summary>
    None,

    /// <summary>The club slam: a shockwave around the king's feet.</summary>
    Slam,

    /// <summary>The bone volley: a fan of bones thrown at the target's chest.</summary>
    Volley,

    /// <summary>The summon: a wave of skeletons rising around the king.</summary>
    Summon,
}
