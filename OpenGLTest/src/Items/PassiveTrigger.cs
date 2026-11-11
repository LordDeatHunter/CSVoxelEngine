namespace VoxelEngine.Items;

/// <summary>When a gear passive fires.</summary>
public enum PassiveTrigger : byte
{
    /// <summary>The wearer lands a blow on a mob.</summary>
    OnHit,

    /// <summary>The wearer takes damage.</summary>
    OnDamaged,

    /// <summary>The wearer's health is under a share of the pool.</summary>
    LowHealth,
}
