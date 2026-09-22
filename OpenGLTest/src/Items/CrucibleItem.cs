namespace silkgltriangle;

/// <summary>
/// A vessel for molten metal. Unlike every other item family here the crucible carries per-vessel
/// state — the <c>CrucibleComponent</c> on its <see cref="ItemStack"/> — so two crucibles are
/// never interchangeable and it has to store as a tool instance rather than as a pooled count.
///
/// <para>
/// It declares no <c>use</c> action: a crucible only ever does something against a kiln or a cast
/// mold, and both of those are block-gated in <c>World</c> (like the quern) so the click is
/// resolved by what is under the crosshair rather than by the item.
/// </para>
/// </summary>
public sealed class CrucibleItem : Item
{
    internal CrucibleItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }

    /// <summary>One inventory entry per vessel, so each one's contents stay its own.</summary>
    public override InventoryCategory StorageCategory => InventoryCategory.Tools;
}
