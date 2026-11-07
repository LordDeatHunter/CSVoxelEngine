namespace VoxelEngine.Simulation.Chests;

/// <summary>What a player asks of an open chest.</summary>
public enum ChestMoveKind : byte
{
    /// <summary>Move units of a stackable from the inventory into the chest (<see cref="ChestMove.Id"/>, <see cref="ChestMove.Count"/>).</summary>
    DepositStack = 0,

    /// <summary>Move one tool or piece of gear from the inventory into the chest (<see cref="ChestMove.Id"/> is its instance id).</summary>
    DepositInstance = 1,

    /// <summary>Move units out of a chest slot into the inventory (<see cref="ChestMove.Slot"/>, <see cref="ChestMove.Count"/>).</summary>
    Withdraw = 2,
}

/// <summary>
/// One move between a player's inventory and the chest they have open. A <see cref="Count"/> of 0 or less means
/// everything (a shift-click). Which fields are read depends on the <see cref="Kind"/>.
/// </summary>
public readonly record struct ChestMove(ChestMoveKind Kind, int Slot = 0, int Id = 0, int Count = 1)
{
    public static ChestMove DepositStack(byte id, int count) => new(ChestMoveKind.DepositStack, Id: id, Count: count);

    public static ChestMove DepositInstance(int instanceId) => new(ChestMoveKind.DepositInstance, Id: instanceId);

    public static ChestMove Withdraw(int slot, int count) => new(ChestMoveKind.Withdraw, Slot: slot, Count: count);
}
