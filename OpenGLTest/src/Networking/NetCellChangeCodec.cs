namespace VoxelEngine.Networking;

/// <summary>
/// The wire form of one <see cref="NetCellChange"/> in a <see cref="NetMessage.BlockChanges"/> or
/// <see cref="NetMessage.BlockCorrection"/> batch: <c>(int x, int y, int z, id, byte state)</c>, the id at the width
/// <see cref="PacketWriter.WriteId"/> fixes. The server writes every change through <see cref="Write"/> and the client
/// reads it back through <see cref="Read"/>, so the two can never drift apart; the batch count around them is the
/// caller's. A short frame throws <see cref="System.IO.EndOfStreamException"/>. GL-free and content-free: the client
/// checks the id against its content before it applies the change.
/// </summary>
public static class NetCellChangeCodec
{
    /// <summary>Appends one change to <paramref name="writer"/>.</summary>
    public static void Write(PacketWriter writer, in NetCellChange change)
    {
        writer.Write(change.X);
        writer.Write(change.Y);
        writer.Write(change.Z);
        writer.WriteId(change.Id);
        writer.Write(change.State);
    }

    /// <summary>Reads one change written by <see cref="Write"/>.</summary>
    public static NetCellChange Read(PacketReader reader)
    {
        var x = reader.ReadInt();
        var y = reader.ReadInt();
        var z = reader.ReadInt();
        var id = reader.ReadId();
        var state = reader.ReadByte();
        return new NetCellChange(x, y, z, id, state);
    }
}
