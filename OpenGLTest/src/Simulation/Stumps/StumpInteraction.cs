using System.Numerics;
using VoxelEngine.Player;

namespace VoxelEngine.Simulation.Stumps;

/// <summary>
/// What a player's clicks do to a tree stump: right-click stands a held log on an empty stump or, on one holding a log,
/// lifts that log back into the inventory (sneaking leaves the stump alone, so a block can still be placed against it);
/// a left-click blow wobbles the log. The mutations run on the authoritative world; a connected client only claims the
/// click (the server replays it) and waits for the station snapshot. Owned by <see cref="Interaction.RightClickAction"/>.
/// </summary>
internal sealed class StumpInteraction(World world, bool clientMode)
{
    /// <summary>
    /// The stump cell the player aims at within <paramref name="reach"/>, or false for any other target.
    /// </summary>
    internal bool TryTarget(PlayerContext player, float reach, out (int X, int Y, int Z) cell)
    {
        cell = default;
        var chunks = world.GetChunkManager();
        if (!player.Raycast(reach, chunks, out var hit, out _)) return false;
        var (x, y, z) = hit.BlockCell();
        if (!chunks.TryGetCell(x, y, z, out var id, out _) || BlockType.ById[id]?.IsStump != true) return false;
        cell = (x, y, z);
        return true;
    }

    /// <summary>
    /// Right-click: places the held log on an empty stump, or picks the log off a loaded one. Returns whether the click
    /// belonged to the stump (a stump with nothing to place on it and nothing standing on it is not claimed).
    /// </summary>
    internal bool TryUse(PlayerContext player, float reach)
    {
        if (player.Camera is { IsSneaking: true } || !TryTarget(player, reach, out var cell)) return false;

        var stumps = world.Stumps;
        var inventory = player.Inventory;
        if (stumps.LogAt(cell.X, cell.Y, cell.Z) != null)
        {
            if (clientMode) return true;
            var logId = stumps.TryTake(cell.X, cell.Y, cell.Z);
            if (logId != 0 && !inventory.Add(logId))
                world.DropSpawner.Spawn(logId, new Vector3(cell.X, cell.Y, cell.Z));
            return true;
        }

        var held = inventory.SelectedBlockId;
        if (held == 0 || inventory.SelectedCount <= 0 || !StumpManager.IsLog(held)) return false;
        if (clientMode) return true;
        if (!stumps.TryPlace(world.GetChunkManager(), cell.X, cell.Y, cell.Z, held, player.Eye, player.Forward)) return false;
        if (!player.IsCreative)
            inventory.ConsumeSelected();
        return true;
    }

    /// <summary>
    /// A left-click blow on the stump cell: wobbles the log standing on it. Returns whether the stump holds a log, so
    /// the caller knows the blow was not a mining hit (the stump itself can only be mined bare).
    /// </summary>
    internal bool TryHit(int x, int y, int z)
    {
        if (world.Stumps.LogAt(x, y, z) == null) return false;
        if (!clientMode) world.Stumps.Wobble(x, y, z);
        return true;
    }
}
