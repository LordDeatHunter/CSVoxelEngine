using System.IO;
using System.Text.Json;
using VoxelEngine.Config;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.Chests;

namespace VoxelEngine.Networking;

/// <summary>
/// The wire forms of the chest messages (GL-free). A <see cref="NetMessage.ChestOpen"/> carries a cell; a
/// <see cref="NetMessage.ChestMove"/> a <see cref="ChestMove"/> (the reader refuses an unknown kind, and clamps a count
/// to the range a slot can hold); a <see cref="NetMessage.ChestContents"/> answer is either "closed" or the open chest's
/// cell and contents as the same JSON shape a saved chest uses (<see cref="StationSaveData.ChestData"/>), so item names and
/// every tool or gear component survive the trip.
/// </summary>
public static class ChestNetCodec
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Writes a <see cref="NetMessage.ChestOpen"/> body: the cell of the chest to open.</summary>
    public static void WriteOpen(PacketWriter writer, int x, int y, int z)
    {
        writer.Write(x);
        writer.Write(y);
        writer.Write(z);
    }

    /// <summary>Reads the cell of a <see cref="NetMessage.ChestOpen"/>.</summary>
    public static (int X, int Y, int Z) ReadOpen(PacketReader reader) => (reader.ReadInt(), reader.ReadInt(), reader.ReadInt());

    /// <summary>Writes a <see cref="NetMessage.ChestMove"/> body.</summary>
    public static void WriteMove(PacketWriter writer, in ChestMove move)
    {
        writer.Write((byte)move.Kind);
        writer.Write(move.Slot);
        // A deposited stack names a content id; an instance id is the server's own number
        if (move.Kind == ChestMoveKind.DepositStack) writer.WriteId((ushort)move.Id);
        else writer.Write(move.Id);
        writer.Write(move.Count);
    }

    /// <summary>Reads a <see cref="NetMessage.ChestMove"/>; an unknown kind is a malformed frame.</summary>
    public static ChestMove ReadMove(PacketReader reader)
    {
        var kind = (ChestMoveKind)reader.ReadByte();
        if (!System.Enum.IsDefined(kind)) throw new InvalidDataException($"unknown chest move {(byte)kind}");
        var slot = reader.ReadInt();
        var id = kind == ChestMoveKind.DepositStack ? reader.ReadId() : reader.ReadInt();
        var count = reader.ReadInt();
        return new ChestMove(kind, slot, id, System.Math.Clamp(count, 0, ChestConfig.MaxStack));
    }

    /// <summary>Writes the answer that says no chest is open for this player (refused, out of reach, or gone).</summary>
    public static void WriteClosed(PacketWriter writer) => writer.Write(false);

    /// <summary>Writes the open chest at a cell with its contents.</summary>
    public static void WriteOpenContents(PacketWriter writer, int x, int y, int z, ChestState chest)
    {
        writer.Write(true);
        writer.WriteBytes(JsonSerializer.SerializeToUtf8Bytes(ChestSaveCodec.Export(x, y, z, chest), Options));
    }

    /// <summary>
    /// Reads a <see cref="NetMessage.ChestContents"/>: null for the closed answer, else the cell and the contents.
    /// Content this run does not know is dropped (and logged by the save codec), a malformed cell is refused.
    /// </summary>
    public static ((int X, int Y, int Z) Cell, ChestState Contents)? ReadContents(PacketReader reader)
    {
        if (!reader.ReadBool()) return null;
        var data = JsonSerializer.Deserialize<StationSaveData.ChestData>(reader.ReadBytes(), Options)
            ?? throw new InvalidDataException("chest contents is empty");
        if (!StationSaveCodec.TryCell(data.Cell, out var cell)) throw new InvalidDataException("chest contents has no cell");
        var dropped = new System.Collections.Generic.List<string>();
        var contents = ChestSaveCodec.Import(data, dropped) ?? throw new InvalidDataException("chest contents is unreadable");
        return (cell, contents);
    }
}
