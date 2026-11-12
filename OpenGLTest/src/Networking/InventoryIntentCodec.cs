using System;
using System.IO;
using VoxelEngine.Networking.ServerSide;

namespace VoxelEngine.Networking;

/// <summary>
/// The wire form of an <see cref="InventoryIntent"/>: the <see cref="NetInventoryAction"/> byte,
/// then only the fields that action reads (see <see cref="InventoryIntent"/>). The reader is
/// bounds-checked: an unknown action, an overlong recipe name, more fed members than any recipe
/// could have, or a staff bind with more than <see cref="MaxSpellParts"/> parts or a part key longer
/// than <see cref="MaxPartKeyLength"/> is a malformed frame (<see cref="InvalidDataException"/>), which the server answers
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

    /// <summary>Most spell part keys a staff bind intent may carry.</summary>
    public const int MaxSpellParts = 16;

    /// <summary>Longest spell part key accepted on the wire.</summary>
    public const int MaxPartKeyLength = 64;

    public static void Write(PacketWriter writer, InventoryIntent intent)
    {
        writer.Write((byte)intent.Action);
        switch (intent.Action)
        {
            case NetInventoryAction.SelectSlot:
            case NetInventoryAction.SelectStaffSlot:
            case NetInventoryAction.ClearStaffSlot:
            case NetInventoryAction.UnequipGear:
            case NetInventoryAction.ClearSlot:
                writer.Write(intent.Slot);
                break;
            case NetInventoryAction.MoveSlot:
                writer.Write(intent.Slot);
                writer.Write(intent.Id);
                break;
            case NetInventoryAction.EquipGear:
                writer.Write(intent.Slot);
                writer.Write(intent.Id);
                break;
            case NetInventoryAction.BindStaffSlot:
            {
                // A null list goes out empty, which the rules refuse
                var parts = intent.Parts ?? [];
                if (parts.Length > MaxSpellParts)
                    throw new ArgumentException($"A staff bind intent carries at most {MaxSpellParts} parts.", nameof(intent));
                writer.Write(intent.Slot);
                writer.Write((byte)parts.Length);
                foreach (var part in parts)
                    writer.Write(part);
                break;
            }
            case NetInventoryAction.DropSelected:
            case NetInventoryAction.DropAllSelected:
                break;
            case NetInventoryAction.Equip:
                writer.Write(intent.Slot);
                writer.Write(intent.IsTool);
                // A tool's instance id is the server's own number; a stackable's is a content id
                if (intent.IsTool) writer.Write(intent.Id);
                else writer.WriteId((ushort)intent.Id);
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
                    writer.WriteId(member);
                break;
            }
            case NetInventoryAction.CreativeGrant:
                writer.WriteId((ushort)intent.Id);
                writer.Write(intent.Count);
                break;
            case NetInventoryAction.CreativeClear:
            case NetInventoryAction.PickBlock:
                writer.WriteId((ushort)intent.Id);
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
                var id = isTool ? reader.ReadInt() : reader.ReadId();
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
                var members = new ushort[count];
                for (var i = 0; i < count; i++)
                    members[i] = reader.ReadId();
                return InventoryIntent.Craft(recipe, batch, members);
            }
            case NetInventoryAction.CreativeGrant:
            {
                var id = reader.ReadId();
                return InventoryIntent.CreativeGrant(id, reader.ReadInt());
            }
            case NetInventoryAction.CreativeClear:
                return InventoryIntent.CreativeClear(reader.ReadId());
            case NetInventoryAction.PickBlock:
                return InventoryIntent.PickBlock(reader.ReadId());
            case NetInventoryAction.SelectStaffSlot:
                return InventoryIntent.SelectStaffSlot(reader.ReadInt());
            case NetInventoryAction.ClearStaffSlot:
                return InventoryIntent.ClearStaffSlot(reader.ReadInt());
            case NetInventoryAction.UnequipGear:
                return InventoryIntent.UnequipGear(reader.ReadInt());
            case NetInventoryAction.ClearSlot:
                return InventoryIntent.ClearSlot(reader.ReadInt());
            case NetInventoryAction.MoveSlot:
            {
                var from = reader.ReadInt();
                return InventoryIntent.MoveSlot(from, reader.ReadInt());
            }
            case NetInventoryAction.EquipGear:
            {
                var slot = reader.ReadInt();
                return InventoryIntent.EquipGear(slot, reader.ReadInt());
            }
            case NetInventoryAction.BindStaffSlot:
            {
                var slot = reader.ReadInt();
                var count = reader.ReadByte();
                if (count > MaxSpellParts)
                    throw new InvalidDataException($"staff bind intent with {count} parts");
                var parts = new string[count];
                for (var i = 0; i < count; i++)
                {
                    parts[i] = reader.ReadString();
                    if (parts[i].Length > MaxPartKeyLength)
                        throw new InvalidDataException($"staff bind intent part key of {parts[i].Length} chars");
                }
                return InventoryIntent.BindStaffSlot(slot, parts);
            }
            default:
                throw new InvalidDataException($"unknown inventory action {(byte)action}");
        }
    }
}
