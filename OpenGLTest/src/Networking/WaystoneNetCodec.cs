using System.Collections.Generic;
using System.IO;
using VoxelEngine.Simulation.Waystones;

namespace VoxelEngine.Networking;

/// <summary>One waystone's registry row as one player's client sees it: its cell, its current name, whether its block is
/// gone and whether this player (its first discoverer) may rename it.</summary>
public readonly record struct WaystoneNetRow(int X, int Y, int Z, string Name, bool Lost, bool MayRename);

/// <summary>What one player's activation of a waystone came to on the server (<see cref="NetMessage.WaystoneOpened"/>):
/// its cell and name, whether the player may rename it and whether it was new to the player (the discovery popup shows
/// only then, the teleport list otherwise).</summary>
public readonly record struct WaystoneOpened(int X, int Y, int Z, string Name, bool MayRename, bool NewToPlayer)
{
    /// <summary>The waystone's cell.</summary>
    public (int X, int Y, int Z) Cell => (X, Y, Z);
}

/// <summary>
/// The wire forms of the waystone messages (GL-free): a <see cref="NetMessage.WaystoneRename"/> request, a player's
/// <see cref="NetMessage.WaystoneRows"/> and a <see cref="NetMessage.WaystoneOpened"/> answer. Every reader is
/// bounds-checked: a rename longer than <see cref="MaxRenameLength"/> characters, a row count below zero or above
/// <see cref="MaxRows"/>, or a name with nothing a waystone would keep (<see cref="WaystoneNames.TrySanitize"/>) is a
/// malformed frame (<see cref="InvalidDataException"/>); a read name is always the sanitized one. A rename's text is
/// otherwise taken raw, since the server sanitizes it when it applies it (<see cref="WaystoneManager.TryRename"/>).
/// </summary>
public static class WaystoneNetCodec
{
    /// <summary>The longest rename text a server reads: room for the dropped characters a sanitize removes, well past
    /// <see cref="WaystoneNames.MaxLength"/>.</summary>
    public const int MaxRenameLength = WaystoneNames.MaxLength * 4;

    /// <summary>The most rows one <see cref="NetMessage.WaystoneRows"/> may carry.</summary>
    public const int MaxRows = 1 << 16;

    /// <summary>Writes a <see cref="NetMessage.WaystoneRename"/> body: the waystone's cell and the new name.</summary>
    public static void WriteRename(PacketWriter writer, (int X, int Y, int Z) cell, string name)
    {
        WriteCell(writer, cell);
        writer.Write(name);
    }

    /// <summary>Reads a <see cref="NetMessage.WaystoneRename"/>; an overlong name is a malformed frame.</summary>
    public static ((int X, int Y, int Z) Cell, string Name) ReadRename(PacketReader reader)
    {
        var cell = ReadCell(reader);
        var name = reader.ReadString();
        if (name.Length > MaxRenameLength)
            throw new InvalidDataException($"waystone rename of {name.Length} characters");
        return (cell, name);
    }

    /// <summary>Writes a <see cref="NetMessage.WaystoneRows"/> body: the row count, then each row.</summary>
    public static void WriteRows(PacketWriter writer, IReadOnlyList<WaystoneNetRow> rows)
    {
        writer.Write(rows.Count);
        foreach (var row in rows)
        {
            WriteCell(writer, (row.X, row.Y, row.Z));
            writer.Write(row.Name);
            writer.Write(row.Lost);
            writer.Write(row.MayRename);
        }
    }

    /// <summary>Reads a <see cref="NetMessage.WaystoneRows"/> into <paramref name="rows"/> (cleared first).</summary>
    public static void ReadRows(PacketReader reader, List<WaystoneNetRow> rows)
    {
        var count = reader.ReadInt();
        if (count is < 0 or > MaxRows)
            throw new InvalidDataException($"waystone rows of {count}");
        rows.Clear();
        for (var i = 0; i < count; i++)
        {
            var (x, y, z) = ReadCell(reader);
            var name = ReadName(reader);
            var lost = reader.ReadBool();
            var mayRename = reader.ReadBool();
            rows.Add(new WaystoneNetRow(x, y, z, name, lost, mayRename));
        }
    }

    /// <summary>Writes a <see cref="NetMessage.WaystoneOpened"/> body.</summary>
    public static void WriteOpened(PacketWriter writer, in WaystoneOpened opened)
    {
        WriteCell(writer, opened.Cell);
        writer.Write(opened.Name);
        writer.Write(opened.MayRename);
        writer.Write(opened.NewToPlayer);
    }

    /// <summary>Reads a <see cref="NetMessage.WaystoneOpened"/>.</summary>
    public static WaystoneOpened ReadOpened(PacketReader reader)
    {
        var (x, y, z) = ReadCell(reader);
        var name = ReadName(reader);
        var mayRename = reader.ReadBool();
        var newToPlayer = reader.ReadBool();
        return new WaystoneOpened(x, y, z, name, mayRename, newToPlayer);
    }

    private static void WriteCell(PacketWriter writer, (int X, int Y, int Z) cell)
    {
        writer.Write(cell.X);
        writer.Write(cell.Y);
        writer.Write(cell.Z);
    }

    private static (int X, int Y, int Z) ReadCell(PacketReader reader) => (reader.ReadInt(), reader.ReadInt(), reader.ReadInt());

    // A name as a waystone keeps it: a server only ever sends sanitized names, so one that is not is a bad frame
    private static string ReadName(PacketReader reader)
    {
        var raw = reader.ReadString();
        if (raw.Length > MaxRenameLength || !WaystoneNames.TrySanitize(raw, out var name))
            throw new InvalidDataException("waystone name is not one a waystone keeps");
        return name;
    }
}
