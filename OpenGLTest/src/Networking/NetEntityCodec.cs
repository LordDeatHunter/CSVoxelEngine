namespace VoxelEngine.Networking;

/// <summary>
/// The wire form of one <see cref="NetMessage.EntityList"/> entry: <c>(byte kind, int id, Vector3 position,
/// float yaw, int health, int ammo, id itemId, Vector3 velocity, byte effects)</c> (the id at the width
/// <see cref="PacketWriter.WriteId"/> fixes), every kind in the same
/// shape (<see cref="NetEntity"/> documents what each kind puts in the fields). The server writes the list
/// through <see cref="Write"/> and the client reads it back through <see cref="Read"/>, so the two can never
/// drift apart. A short frame throws <see cref="System.IO.EndOfStreamException"/>, which the client answers by
/// leaving the server. Only a drop, a primed TNT and a falling block carry a block or item id in the item field
/// (<see cref="CarriesContentId"/>), so only theirs goes through the client's <see cref="WireIds"/>; a mob's look,
/// tier or phase there is plain data. GL-free and content-free.
/// </summary>
public static class NetEntityCodec
{
    public static void Write(PacketWriter writer, in NetEntity entity)
    {
        writer.Write((byte)entity.Kind);
        writer.Write(entity.Id);
        writer.Write(entity.Position);
        writer.Write(entity.Yaw);
        writer.Write(entity.Health);
        writer.Write(entity.Ammo);
        if (CarriesContentId(entity.Kind)) writer.WriteId(entity.ItemId);
        else writer.Write(entity.ItemId);
        writer.Write(entity.Velocity);
        writer.Write(entity.Effects);
        if (entity.Kind == NetEntityKind.Drop)
            writer.WriteBytes(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(entity.Instance));
    }

    /// <summary>Reads one entry written by <see cref="Write"/>.</summary>
    public static NetEntity Read(PacketReader reader)
    {
        var kind = (NetEntityKind)reader.ReadByte();
        var id = reader.ReadInt();
        var position = reader.ReadVector3();
        var yaw = reader.ReadFloat();
        var health = reader.ReadInt();
        var ammo = reader.ReadInt();
        var itemId = CarriesContentId(kind) ? reader.ReadId() : reader.ReadUInt16();
        var velocity = reader.ReadVector3();
        var effects = reader.ReadEffectMask();
        var instance = kind == NetEntityKind.Drop
            ? System.Text.Json.JsonSerializer.Deserialize<Saves.PlayerSaveData.ToolData>(reader.ReadBytes()) : null;
        return new NetEntity(kind, id, position, yaw, health, ammo, itemId, velocity, effects, instance);
    }

    /// <summary>Whether a kind's <see cref="NetEntity.ItemId"/> is a block or item id rather than mob data.</summary>
    internal static bool CarriesContentId(NetEntityKind kind) =>
        kind is NetEntityKind.Drop or NetEntityKind.PrimedTnt or NetEntityKind.FallingBlock;
}
