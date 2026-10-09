using System;
using System.IO;

namespace silkgltriangle;

/// <summary>
/// The wire form of an <see cref="InventoryIntent"/>: the <see cref="NetInventoryAction"/> byte,
/// then only the fields that action reads (see <see cref="InventoryIntent"/>). The reader is
/// bounds-checked: an unknown action, an overlong recipe name or more fed members than any recipe
/// could have is a malformed frame (<see cref="InvalidDataException"/>), which the server answers
/// by dropping the sender; a short frame throws <see cref="EndOfStreamException"/> the same way.
/// Values merely out of range (a slot past the hotbar, a zero batch) parse fine and are refused by
/// <see cref="InventoryIntentRules"/> instead. GL-free and content-free.
/// </summary>
public static class InventoryIntentCodec
{
    /// <summary>Longest recipe name accepted on the wire.</summary>
    public const int MaxRecipeNameLength = 128;

    /// <summary>Most fed members a craft intent may carry (one per ingredient).</summary>
    public const int MaxCraftMembers = 32;

    public static void Write(PacketWriter writer, InventoryIntent intent)
    {
        writer.Write((byte)intent.Action);
        switch (intent.Action)
        {
            case NetInventoryAction.SelectSlot:
                writer.Write(intent.Slot);
                break;
            case NetInventoryAction.DropSelected:
            case NetInventoryAction.DropAllSelected:
                break;
            case NetInventoryAction.Equip:
                writer.Write(intent.Slot);
                writer.Write(intent.IsTool);
                writer.Write(intent.Id);
                break;
            case NetInventoryAction.Craft:
            {
                var members = intent.Members ?? [];
                if (members.Length > MaxCraftMembers)
                    throw new ArgumentException($"A craft intent carries at most {MaxCraftMembers} members.", nameof(intent));
                writer.Write(intent.Recipe ?? "");
                writer.Write(intent.Count);
                writer.Write((byte)members.Length);
                foreach (var member in members)
                    writer.Write(member);
                break;
            }
            case NetInventoryAction.CreativeGrant:
                writer.Write((byte)intent.Id);
                writer.Write(intent.Count);
                break;
            case NetInventoryAction.CreativeClear:
            case NetInventoryAction.PickBlock:
                writer.Write((byte)intent.Id);
                break;
            default:
                throw new ArgumentException($"Unknown inventory action {intent.Action}.", nameof(intent));
        }
    }

    /// <summary>Reads one intent written by <see cref="Write"/>, starting at its action byte.</summary>
    public static InventoryIntent Read(PacketReader reader)
    {
        var action = (NetInventoryAction)reader.ReadByte();
        switch (action)
        {
            case NetInventoryAction.SelectSlot:
                return InventoryIntent.Select(reader.ReadInt());
            case NetInventoryAction.DropSelected:
            case NetInventoryAction.DropAllSelected:
                return new InventoryIntent(action);
            case NetInventoryAction.Equip:
            {
                var slot = reader.ReadInt();
                var isTool = reader.ReadBool();
                var id = reader.ReadInt();
                return new InventoryIntent(action, Slot: slot, IsTool: isTool, Id: id);
            }
            case NetInventoryAction.Craft:
            {
                var recipe = reader.ReadString();
                if (recipe.Length > MaxRecipeNameLength)
                    throw new InvalidDataException($"craft intent recipe name of {recipe.Length} chars");
                var batch = reader.ReadInt();
                var count = reader.ReadByte();
                if (count > MaxCraftMembers)
                    throw new InvalidDataException($"craft intent with {count} members");
                var members = new byte[count];
                for (var i = 0; i < count; i++)
                    members[i] = reader.ReadByte();
                return InventoryIntent.Craft(recipe, batch, members);
            }
            case NetInventoryAction.CreativeGrant:
            {
                var id = reader.ReadByte();
                return InventoryIntent.CreativeGrant(id, reader.ReadInt());
            }
            case NetInventoryAction.CreativeClear:
                return InventoryIntent.CreativeClear(reader.ReadByte());
            case NetInventoryAction.PickBlock:
                return InventoryIntent.PickBlock(reader.ReadByte());
            default:
                throw new InvalidDataException($"unknown inventory action {(byte)action}");
        }
    }
}
