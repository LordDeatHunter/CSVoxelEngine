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
    // Well-known handles, resolved by name once the registry has assigned ids
    public static byte Vine { get; internal set; }
    public static byte Branch { get; internal set; }
    public static byte Flint { get; internal set; }

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

    /// <summary>Whether an id is a pure item rather than a block (or air/unknown).</summary>
    public static bool IsItem(byte id) => Types.ContainsKey(id);
}
