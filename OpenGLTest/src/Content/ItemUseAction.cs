namespace silkgltriangle;

/// <summary>
/// What right-clicking while holding a pure item does, from the <c>use.action</c> field of
/// <c>assets/data/items/&lt;name&gt;.json</c>. A fluid action swaps the item for its
/// <c>use.result</c> when it succeeds, so a bucket and a full bucket alternate on the
/// same button; a stack action spends one unit from its stack instead, so it stays a
/// plain pickup. An item without an action is only ever a pickup.
/// </summary>
public enum ItemUseAction
{
    /// <summary>No use behaviour.</summary>
    None,

    /// <summary>Scoops the targeted fluid source into the item (bucket).</summary>
    PickupFluid,

    /// <summary>Pours a fluid source into the targeted cell (water bucket).</summary>
    PlaceFluid,

    /// <summary>
    /// Builds up a multi-state block (flint on a pebble pile): augments the targeted pile
    /// or grounds a fresh one, spending one unit. See <see cref="ItemType.UseBlockId"/>.
    /// </summary>
    PlaceStack,
}
