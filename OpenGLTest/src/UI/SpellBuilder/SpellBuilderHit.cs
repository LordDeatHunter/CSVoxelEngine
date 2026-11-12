namespace VoxelEngine.UI.SpellBuilder;

/// <summary>
/// What a point on the spell builder panel lands on: the kind, plus the column (an index into
/// <see cref="SpellBuilderCatalog.Columns"/>) and row of a part, or the slot index of a slot button.
/// Unused fields are -1.
/// </summary>
public readonly record struct SpellBuilderHit(SpellBuilderHitKind Kind, int Column = -1, int Index = -1);
