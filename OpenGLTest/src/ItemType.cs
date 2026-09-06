using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// Pure items: pickups that are not blocks. They have no <see cref="BlockType"/> entry, so
/// they can never be placed or meshed into chunks — they only exist as <see cref="ItemDrop"/>
/// entities (rendered from their 2D sprite, see <see cref="Primitives.CreateExtrudedSprite"/>)
/// and as <see cref="Inventory"/> slots. Ids live in the same byte space as block ids but
/// start high (100+) so they never collide with present or future block ids.
/// </summary>
public class ItemType
{
    public const byte Vine = 100;
    public const byte Branch = 101;
    public const byte Flint = 102;

    public static readonly Dictionary<byte, ItemType> Types = new()
    {
        { Vine, new ItemType("vine", "assets/textures/items/vine.png") },
        { Branch, new ItemType("branch", "assets/textures/items/branch.png") },
        { Flint, new ItemType("flint", "assets/textures/items/flint.png") },
    };

    /// <summary>Display name for future tooltips/crafting UI.</summary>
    public string Name { get; }

    /// <summary>Sprite source, relative to the working directory like every other asset.</summary>
    public string TexturePath { get; }

    public ItemType(string name, string texturePath)
    {
        Name = name;
        TexturePath = texturePath;
    }

    /// <summary>Whether an id is a pure item rather than a block (or air/unknown).</summary>
    public static bool IsItem(byte id) => Types.ContainsKey(id);
}
