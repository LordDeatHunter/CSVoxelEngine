namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// One metal in a crucible (<see cref="CrucibleComponent"/>): <paramref name="MoltenMl"/> of it
/// has melted and can be poured, <paramref name="PendingMl"/> is solid charge still waiting on a
/// fire. <paramref name="MaterialId"/> is a molten material id, never 0.
/// </summary>
public readonly record struct CrucibleMetal(byte MaterialId, int MoltenMl, int PendingMl)
{
    /// <summary>Everything of this metal in the vessel.</summary>
    public int TotalMl => MoltenMl + PendingMl;
}
