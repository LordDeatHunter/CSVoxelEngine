namespace VoxelEngine.Magic;

/// <summary>
/// What became of one cast: <see cref="Cast"/> when it went through, otherwise the first rule it broke
/// (<see cref="CastRules.TryCast"/> checks them in this order, mana last).
/// </summary>
public enum CastVerdict
{
    /// <summary>The cast went through: the mana was billed and the effect may run.</summary>
    Cast,

    /// <summary>The world is still loading, or already gone.</summary>
    NotPlaying,

    /// <summary>The held item is not a staff, or its tool stack is gone.</summary>
    NoStaff,

    /// <summary>The slot is past the staff's slots, or not the one it has selected.</summary>
    WrongSlot,

    /// <summary>The selected slot holds no recipe.</summary>
    Unbound,

    /// <summary>The slot names a spell part this run does not know.</summary>
    UnknownPart,

    /// <summary>The bound recipe does not compile valid against the staff, or its cost is not finite.</summary>
    InvalidRecipe,

    /// <summary>The caster cast too recently (<see cref="CastPacing"/>).</summary>
    Cooldown,

    /// <summary>The caster's mana cannot cover the cost, or the caster has no mana pool.</summary>
    NoMana,
}
