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
    /// or grounds a fresh one, spending one unit. See <see cref="Item.UseBlockId"/>.
    /// </summary>
    PlaceStack,

    /// <summary>
    /// Strikes a spark at a fully built firepit (a max-count branch pile): a chance per
    /// use to light it, spending durability instead of morphing. See
    /// <see cref="Item.UseBlockId"/> and <see cref="Item.UseChance"/>.
    /// </summary>
    IgniteCampfire,

    /// <summary>
    /// Right-clicks a <see cref="Item.UseBlockId"/> cell to turn the 2x2x2 volume it belongs to
    /// into the <see cref="Item.UseResultId"/> anchor block (see <see cref="Multiblock"/>).
    /// The tool is never consumed and never morphs — the converter is an endless misc item.
    /// </summary>
    ConvertMultiblock,
}
