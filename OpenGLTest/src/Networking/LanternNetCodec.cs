using System.IO;
using System.Text.Json;
using VoxelEngine.Simulation.Lanterns;

namespace VoxelEngine.Networking;

/// <summary>Lantern requests name targets and moves, never client-authored fuel or contents.</summary>
public static class LanternNetCodec
{
    public static void WriteTarget(PacketWriter writer, LanternTarget target)
    {
        writer.Write(target.Instance);
        writer.Write(target.Cell.X); writer.Write(target.Cell.Y); writer.Write(target.Cell.Z);
    }
    public static LanternTarget ReadTarget(PacketReader reader)
    {
        var instance = reader.ReadInt();
        if (instance < 0) throw new InvalidDataException("Invalid lantern instance");
        return new(instance, (reader.ReadInt(), reader.ReadInt(), reader.ReadInt()));
    }
    public static void WriteContents(PacketWriter writer, LanternTarget target, LanternState state)
    {
        WriteTarget(writer, target);
        writer.WriteBytes(JsonSerializer.SerializeToUtf8Bytes(LanternCodec.Export(state)));
    }
    public static (LanternTarget Target, LanternState State) ReadContents(PacketReader reader) =>
        (ReadTarget(reader), LanternCodec.Import(JsonSerializer.Deserialize<LanternData>(reader.ReadBytes())
            ?? throw new InvalidDataException("Empty lantern state")));
}
