using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// Pure items: pickups that are not blocks, loaded from <c>assets/data/items/&lt;name&gt;.json</c>
/// by <see cref="ContentRegistry"/>. They have no <see cref="BlockType"/> entry, so they can
/// never be placed or meshed into chunks — they only exist as <see cref="ItemDrop"/> entities
/// (rendered from their 2D sprite, see <see cref="Primitives.CreateExtrudedSprite"/>) and as
/// <see cref="Inventory"/> slots. Ids live in the same byte space as block ids and are handed
/// out after them, so the two never collide.
/// </summary>
public sealed class ItemType
{
    /// <summary>Every registered item by id.</summary>
    public static IReadOnlyDictionary<byte, ItemType> Types { get; internal set; } =
        new Dictionary<byte, ItemType>();

    internal ItemType(byte id, string name, string texturePath)
    {
        Id = id;
        Name = name;
        TexturePath = texturePath;
    }

    /// <summary>Byte id assigned at load.</summary>
    public byte Id { get; }

    /// <summary>Display name for future tooltips/crafting UI.</summary>
    public string Name { get; }

    /// <summary>Sprite source, relative to the working directory like every other asset.</summary>
    public string TexturePath { get; }

    /// <summary>What right-clicking with this item does; <see cref="ItemUseAction.None"/> for a
    /// plain pickup.</summary>
    public ItemUseAction UseAction { get; internal init; }

    /// <summary>Item id this one becomes after a successful use (a bucket becomes a water
    /// bucket); 0 when the item declares no use.</summary>
    public byte UseResultId { get; internal init; }

    /// <summary>Fluid block id the use picks up or places; 0 when the item declares no use.</summary>
    public byte UseFluidId { get; internal init; }

    /// <summary>Whether an id is a pure item rather than a block (or air/unknown).</summary>
    public static bool IsItem(byte id) => Types.ContainsKey(id);

    /// <summary>
    /// Whether holding this id should make the block selection raycast see fluids. Only an item
    /// that scoops one needs to aim at water; for everything else the water is aimed through,
    /// so a pond never blocks placing a block on its floor.
    /// </summary>
    public static bool TargetsFluids(byte id) =>
        Types.TryGetValue(id, out var type) && type.UseAction == ItemUseAction.PickupFluid;
}
