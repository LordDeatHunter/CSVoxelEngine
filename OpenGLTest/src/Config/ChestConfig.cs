namespace VoxelEngine.Config;

/// <summary>Chest tuning: the biggest chest a block may declare and how much one slot holds.</summary>
public static class ChestConfig
{
    /// <summary>The most slots a chest block may declare.</summary>
    public const int MaxSlots = 54;

    /// <summary>The most units of a stackable one chest slot holds.</summary>
    public const int MaxStack = 999;

    /// <summary>How many chests the stations file and the network snapshot may carry.</summary>
    public const int MaxChests = 4096;

    /// <summary>Seconds the open chest panel waits for the server before it shows nothing (a client with no answer).</summary>
    public const float OpenTimeoutSeconds = 5f;
}
