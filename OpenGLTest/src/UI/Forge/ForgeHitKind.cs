namespace VoxelEngine.UI.Forge;

/// <summary>What a point on the anvil's recipe list lands on (<see cref="ForgeLayout.HitAt"/>).</summary>
public enum ForgeHitKind
{
    /// <summary>Off the panel.</summary>
    None,

    /// <summary>On the panel, but on no recipe or button.</summary>
    Panel,

    /// <summary>A recipe card: forge it.</summary>
    Recipe,

    /// <summary>The CLOSE button.</summary>
    Close,
}
