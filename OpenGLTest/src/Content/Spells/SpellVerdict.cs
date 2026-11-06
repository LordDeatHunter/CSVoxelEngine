namespace VoxelEngine.Content.Spells;

/// <summary>Whether a <see cref="SpellRecipe"/> may be cast, and if not, the first rule it breaks.</summary>
public enum SpellVerdict
{
    /// <summary>The recipe follows every rule.</summary>
    Valid,

    /// <summary>The recipe holds no element.</summary>
    MissingElement,

    /// <summary>The recipe holds more than one element.</summary>
    TooManyElements,

    /// <summary>The recipe holds no shape.</summary>
    MissingShape,

    /// <summary>The recipe holds more than one shape.</summary>
    TooManyShapes,

    /// <summary>The recipe holds more than one trigger.</summary>
    TooManyTriggers,

    /// <summary>The recipe holds the same modifier twice.</summary>
    DuplicateModifier,

    /// <summary>A part's tags are all outside the allowed set (the staff tier forbids it).</summary>
    DisallowedPart
}
