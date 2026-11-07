using System.Collections.Generic;
using VoxelEngine.Content.Spells;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Magic;

/// <summary>
/// The one known-part rule the spell builder UI, the client's bind prediction and the server's
/// <see cref="Networking.ServerSide.InventoryIntentRules"/> share: creative knows every part, otherwise
/// the inventory's learned, saved set decides (<see cref="Inventory.KnowsPart"/>). Every change to that
/// set goes through <see cref="Learn"/> (or a restore followed by <see cref="SizeMana"/>), so the mana
/// pool always follows the owned count.
/// </summary>
internal static class KnownSpellParts
{
    /// <summary>Whether a player with <paramref name="inventory"/> may use <paramref name="part"/>.</summary>
    public static bool Knows(Inventory inventory, SpellPart part, bool creative) =>
        creative || inventory.KnowsPart(part.Key);

    /// <summary>
    /// Teaches <paramref name="keys"/> to <paramref name="inventory"/> and, when something was new and a
    /// <paramref name="mana"/> pool is given, resizes it for the new count. Returns whether anything was
    /// new. A client passes null mana: it only predicts the inventory, its pool is mirrored from the
    /// server's status.
    /// </summary>
    public static bool Learn(Inventory inventory, IEnumerable<string> keys, PlayerMana? mana)
    {
        if (!inventory.LearnParts(keys)) return false;
        if (mana != null) SizeMana(inventory, mana);
        return true;
    }

    /// <summary>
    /// Sizes <paramref name="mana"/> for the parts <paramref name="inventory"/> owns. The max follows the
    /// learned set only, never creative's every-part knowledge; a client never calls it, since its max is
    /// mirrored from <c>PlayerStatus</c>.
    /// </summary>
    public static void SizeMana(Inventory inventory, PlayerMana mana) => mana.SetKnownParts(inventory.KnownPartCount);
}
