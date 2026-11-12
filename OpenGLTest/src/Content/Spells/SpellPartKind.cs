namespace VoxelEngine.Content.Spells;

/// <summary>
/// What role a spell part plays in a <see cref="SpellRecipe"/>. A part's kind is the folder it lies in under
/// <c>assets/data/spell</c>, the lowercase kind name (<see cref="SpellPart.FolderOf"/>).
/// </summary>
public enum SpellPartKind
{
    /// <summary>What the spell is made of (fire, frost, nature); a recipe needs exactly one.</summary>
    Element,

    /// <summary>How the spell is delivered (projectile, self); a recipe needs exactly one.</summary>
    Shape,

    /// <summary>What changes the spell's numbers (power, range); any number of distinct ones.</summary>
    Modifier,

    /// <summary>What fires a follow-up spell; at most one per recipe.</summary>
    Trigger
}
