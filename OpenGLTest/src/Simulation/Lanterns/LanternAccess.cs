using VoxelEngine.Chunks;
using VoxelEngine.Player;

namespace VoxelEngine.Simulation.Lanterns;

/// <summary>One click-target rule used by local input and server session admission.</summary>
public static class LanternAccess
{
    public static bool TryTarget(PlayerContext player, ChunkManager chunks, float reach, out LanternTarget target, out bool pickup)
    {
        target = default;
        pickup = false;
        var aimed = player.Raycast(reach, chunks, out var hit, out _);
        var cell = hit.BlockCell();
        var placed = aimed && chunks.TryGetCell(cell.X, cell.Y, cell.Z, out var id, out _) && LanternState.IsLantern(id);
        if (placed && player.IsSneaking)
        {
            target = new(0, cell); pickup = true; return true;
        }
        if (LanternState.Held(player.Inventory) != null && (player.IsSneaking || !aimed))
        {
            target = new(player.Inventory.SelectedToolInstance, default); return true;
        }
        if (!placed) return false;
        target = new(0, cell);
        return true;
    }
}
