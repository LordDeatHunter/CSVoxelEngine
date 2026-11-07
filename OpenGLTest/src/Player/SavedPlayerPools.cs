using VoxelEngine.Magic;
using VoxelEngine.Saves;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Player;

/// <summary>
/// Restores a saved player's inventory, health and mana in the one order that keeps them consistent,
/// shared by singleplayer (<see cref="LocalPlayer.ApplySavedPlayer"/>) and a server's players
/// (<see cref="Networking.ServerSide.ServerPlayer.Restore"/>). A client never calls it: it imports
/// snapshots only, and its mana pool is mirrored from the server's status.
/// </summary>
internal static class SavedPlayerPools
{
    /// <summary>
    /// Imports the inventory, sizes the mana pool for its known spell parts, then restores health and
    /// mana. The known count must size the pool before the saved mana is clamped into it, or a pool that
    /// had grown past the starting max would come back clamped down.
    /// </summary>
    public static void Apply(PlayerSaveData saved, Inventory inventory, PlayerHealth health)
    {
        if (saved.Inventory is { } data)
            inventory.Import(data);
        KnownSpellParts.SizeMana(inventory, health.Mana);
        if (saved.Health is { } hp)
            health.Restore(hp);
        if (saved.Mana is { } mana)
            health.Mana.Restore(mana);
    }
}
