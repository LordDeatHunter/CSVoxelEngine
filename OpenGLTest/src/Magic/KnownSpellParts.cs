using VoxelEngine.Content.Spells;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Magic;

/// <summary>
/// The one known-part rule the spell builder UI, the client's bind prediction and the server's
/// <see cref="Networking.ServerSide.InventoryIntentRules"/> share: creative knows every part, otherwise
/// the inventory's own knowledge decides (<see cref="Inventory.KnowsPart"/>). MG-8 swaps the source
/// behind it for the learned, saved set.
/// </summary>
internal static class KnownSpellParts
{
    /// <summary>Whether a player with <paramref name="inventory"/> may use <paramref name="part"/>.</summary>
    public static bool Knows(Inventory inventory, SpellPart part, bool creative) =>
        creative || inventory.KnowsPart(part.Key);
}
