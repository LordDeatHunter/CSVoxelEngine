using System.IO;
using VoxelEngine.Simulation;

namespace VoxelEngine.Networking;

/// <summary>
/// The wire form of a <see cref="NetMessage.StationState"/>: every campfire, quern, kiln and cast
/// mold block entity the server runs, in that order, each manager writing and reading its own part
/// (<c>WriteNet</c>/<c>ReadNet</c>). A client replaces its mirrors with what arrives, so the stations
/// it draws are the server's; they never tick there. Reads are bounds-checked: a count past
/// <see cref="MaxEntries"/> or a field outside its range is a malformed frame
/// (<see cref="InvalidDataException"/>). GL-free.
/// </summary>
public static class StationNetCodec
{
    /// <summary>Most block entities of one kind a snapshot may carry.</summary>
    public const int MaxEntries = 4096;

    /// <summary>Writes the four managers' state.</summary>
    public static void Write(PacketWriter writer, CampfireManager campfires, QuernManager querns,
        KilnManager kilns, CastingManager casting)
    {
        campfires.WriteNet(writer);
        querns.WriteNet(writer);
        kilns.WriteNet(writer);
        casting.WriteNet(writer);
    }

    /// <summary>Replaces the four managers' state with a snapshot written by <see cref="Write"/>.</summary>
    public static void Read(PacketReader reader, CampfireManager campfires, QuernManager querns,
        KilnManager kilns, CastingManager casting)
    {
        campfires.ReadNet(reader);
        querns.ReadNet(reader);
        kilns.ReadNet(reader);
        casting.ReadNet(reader);
    }

    /// <summary>Whether any of the four holds anything, so an empty world sends no snapshots.</summary>
    public static bool AnyStations(CampfireManager campfires, QuernManager querns, KilnManager kilns,
        CastingManager casting) =>
        campfires.HasAny || querns.HasAny || kilns.HasAny || casting.HasAny;

    /// <summary>Reads an entry count, refusing one outside 0..<see cref="MaxEntries"/>.</summary>
    internal static int ReadCount(PacketReader reader)
    {
        var count = reader.ReadInt();
        if (count is < 0 or > MaxEntries)
            throw new InvalidDataException($"station snapshot with {count} entries");
        return count;
    }

    /// <summary>Reads a cell written as three ints.</summary>
    internal static (int X, int Y, int Z) ReadCell(PacketReader reader) =>
        (reader.ReadInt(), reader.ReadInt(), reader.ReadInt());

    /// <summary>Writes a cell as three ints.</summary>
    internal static void WriteCell(PacketWriter writer, int x, int y, int z)
    {
        writer.Write(x);
        writer.Write(y);
        writer.Write(z);
    }

    /// <summary>Reads a float that must be finite.</summary>
    internal static float ReadFinite(PacketReader reader)
    {
        var value = reader.ReadFloat();
        if (!float.IsFinite(value))
            throw new InvalidDataException("station snapshot with a non-finite value");
        return value;
    }
}
