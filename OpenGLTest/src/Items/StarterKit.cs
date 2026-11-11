using VoxelEngine.Saves;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// What a new survival player starts with: one hint scroll, whose parts make a castable spell. Given
/// by a store-backed singleplayer world (the <see cref="World"/> constructor) and to a server's
/// newcomer (<see cref="Networking.ServerSide.ServerPlayer.Restore"/>); storeless scenario and test
/// worlds, the server camera and clients never get it.
/// </summary>
internal static class StarterKit
{
    /// <summary>The hint scroll's item file name: the handpicked example combo, fire + projectile.</summary>
    internal const string HintScrollName = "scroll_fire_projectile";

    /// <summary>
    /// Whether a player loaded from <paramref name="saved"/> needs the starter kit: no saved player, no
    /// saved inventory, or an inventory saved before spell parts could be learned (no known-part list).
    /// Every export writes the list, so a returning player who used the scroll never gets another.
    /// </summary>
    public static bool NeededBy(PlayerSaveData? saved) => saved?.Inventory?.KnownParts == null;

    /// <summary>Adds the hint scroll (marked obtained, equipped to a free hotbar slot).</summary>
    public static void GiveTo(Inventory inventory) => inventory.Add(ItemRegistry.RequireItem(HintScrollName).Id);
}
