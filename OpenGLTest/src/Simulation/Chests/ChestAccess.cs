using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Simulation.Chests;

/// <summary>
/// The rules of reaching and using a chest as a player, GL-free and shared by singleplayer and a server: the chest
/// must be a loaded chest block within reach of the player's eye (a little slack for movement), and a move is one of
/// the three <see cref="ChestMove"/>s judged by <see cref="ChestTransfer"/>.
/// </summary>
public static class ChestAccess
{
    /// <summary>Extra blocks of reach a held-open chest allows beyond the interaction reach, so a step back does not slam it shut.</summary>
    public const float ReachSlackBlocks = 1.5f;

    /// <summary>Whether a chest cell is within <paramref name="reach"/> blocks (plus the slack) of an eye.</summary>
    public static bool InReach(Vector3 eye, int x, int y, int z, float reach) =>
        Vector3.Distance(eye, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f)) <= reach + ReachSlackBlocks;

    /// <summary>
    /// Opens the chest at a cell for a player at <paramref name="eye"/>: its state, created (and a ruin chest rolled)
    /// on first use. Null when the cell is out of reach, unloaded or not a chest.
    /// </summary>
    public static ChestState? TryOpen(ChestManager chests, ChunkManager chunks, Vector3 eye, int x, int y, int z,
        float reach) =>
        InReach(eye, x, y, z, reach) ? chests.Open(chunks, x, y, z) : null;

    /// <summary>
    /// Applies one move to an inventory and an open chest. Returns how many units moved (0 when refused: nothing held,
    /// no room on either side, a slot outside the chest, or a deposit of something that does not move that way).
    /// </summary>
    public static int Apply(Inventory inventory, ChestState chest, in ChestMove move) => move.Kind switch
    {
        ChestMoveKind.DepositStack when BlockTables.IsAssignedId(move.Id) =>
            ChestTransfer.Deposit(inventory, chest, (ushort)move.Id, move.Count <= 0 ? int.MaxValue : move.Count),
        ChestMoveKind.DepositInstance => ChestTransfer.DepositInstance(inventory, chest, move.Id) ? 1 : 0,
        ChestMoveKind.Withdraw => ChestTransfer.Withdraw(inventory, chest, move.Slot, move.Count),
        _ => 0,
    };
}
