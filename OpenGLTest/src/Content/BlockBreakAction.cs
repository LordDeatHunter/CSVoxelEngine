namespace silkgltriangle;

/// <summary>What happens when a block is destroyed: roll its loot table, or prime it as TNT
/// (which drops nothing and spawns a <see cref="PrimedTnt"/> instead).</summary>
public enum BlockBreakAction
{
    Drop,
    PrimeTnt,
}
