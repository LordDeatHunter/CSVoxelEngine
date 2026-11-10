using System.Collections.Generic;
using System.IO;
using VoxelEngine.Simulation;
using VoxelEngine.UI.InventoryPanel;

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

    /// <summary>Writes a crucible's contents: the metal count, then each metal's material id, molten
    /// and pending ml, then the last charged input.</summary>
    internal static void WriteCrucible(PacketWriter writer, CrucibleComponent contents)
    {
        writer.Write((byte)contents.Metals.Count);
        foreach (var metal in contents.Metals)
        {
            writer.Write(metal.MaterialId);
            writer.Write(metal.MoltenMl);
            writer.Write(metal.PendingMl);
        }
        writer.WriteId(contents.PendingInputId);
    }

    /// <summary>Reads what <see cref="WriteCrucible"/> wrote, refusing more than
    /// <see cref="CrucibleComponent.MaxMetals"/> metals, a material id 0, a repeated material and a
    /// negative amount. The last input's id is read as it came; the caller checks it against its content.</summary>
    internal static CrucibleComponent ReadCrucible(PacketReader reader)
    {
        var count = reader.ReadByte();
        if (count > CrucibleComponent.MaxMetals)
            throw new InvalidDataException($"crucible with {count} metals");
        var metals = new List<CrucibleMetal>(count);
        for (var i = 0; i < count; i++)
        {
            var metal = new CrucibleMetal(reader.ReadByte(), reader.ReadInt(), reader.ReadInt());
            if (metal.MaterialId == 0 || metal.MoltenMl < 0 || metal.PendingMl < 0
                || metals.Exists(m => m.MaterialId == metal.MaterialId))
                throw new InvalidDataException("crucible metal out of range");
            metals.Add(metal);
        }
        return new CrucibleComponent(metals, reader.ReadId());
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
