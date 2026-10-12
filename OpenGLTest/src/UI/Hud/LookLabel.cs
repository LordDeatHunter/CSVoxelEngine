using System;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.UI.Hud;

/// <summary>
/// The look-at label's rule, GL-free: what the label under the crosshair reads and how it draws.
/// A block the player has obtained shows its name; one they have not shows "Unknown" with a flat
/// dark silhouette instead of its texture; creative mode always shows everything, and a block
/// with no item at all (pebbles, piles, snow layers, kiln parts) is always known. The name is
/// flagged red when the held item cannot mine the block, so a tool gate is visible before the
/// player tries it.
/// </summary>
public static class LookLabel
{
    /// <summary>What the label shows for a targeted block: its text, whether the block is known
    /// (an unknown one draws as a silhouette) and whether the held item can mine it.</summary>
    public readonly record struct State(string Text, bool Known, bool Mineable);

    /// <summary>
    /// The label for a targeted cell's block: an empty text with no target (air or an unknown id).
    /// <paramref name="knows"/> is the player's obtained set (<see cref="Inventory.Knows"/>).
    /// </summary>
    public static State For(byte blockId, bool creative, Func<byte, bool> knows, Item? held)
    {
        if (BlockType.ById[blockId] is not { } block) return new State("", false, false);
        var known = creative || !block.HasItem || knows(blockId);
        return new State(known ? block.DisplayName : "Unknown", known, creative || Mining.CanMine(blockId, held));
    }
}
