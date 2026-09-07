namespace silkgltriangle;

/// <summary>
/// What right-clicking while holding a pure item does, from the <c>use.action</c> field of
/// <c>assets/data/items/&lt;name&gt;.json</c>. An item with an action swaps itself for its
/// <c>use.result</c> when the action succeeds, so a bucket and a full bucket alternate on the
/// same button; an item without one is only ever a pickup.
/// </summary>
public enum ItemUseAction
{
    /// <summary>No use behaviour.</summary>
    None,

    /// <summary>Scoops the targeted fluid source into the item (bucket).</summary>
    PickupFluid,

    /// <summary>Pours a fluid source into the targeted cell (water bucket).</summary>
    PlaceFluid,
}
