using System;
using System.IO;
using System.Numerics;
using VoxelEngine.Networking;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// The <see cref="NetMessage.EntityList"/> entry codec the server writes and the client reads: every field of
/// every kind comes back exactly (a realm mob's attack count in the ammo field and the highest block or item id
/// included), and an entry cut short
/// is a short frame, never a guessed entity.
/// </summary>
public sealed class NetEntityCodecTests
{
    private static readonly NetEntity[] Entries =
    [
        new(NetEntityKind.Hellbrute, 3, new Vector3(1.5f, -400.25f, 7f), 1.25f, 87, 41, 0, new Vector3(0.5f, -2f, 0f), 0b101),
        new(NetEntityKind.Galeraptor, int.MaxValue, new Vector3(-9f, 355.5f, 2f), -3f, 1, int.MaxValue, 0,
            new Vector3(0f, 4f, -1f)),
        new(NetEntityKind.Skeleton, 11, new Vector3(2920.5f, 11.95f, -1410.5f), 0.75f, 35, 7, 5,
            new Vector3(-1f, 0f, 2f)),
        new(NetEntityKind.Shardling, 15, new Vector3(-30.5f, -250f, 12.5f), 2f, 28, 9, 2, new Vector3(1f, 0f, -1f), 0b10),
        new(NetEntityKind.Sporeling, 16, new Vector3(8f, -280.5f, -3f), -1f, 40, 4, 3, Vector3.Zero, 0b100),
        new(NetEntityKind.Magmaborn, 17, new Vector3(0.5f, -320f, 0.5f), 0.5f, 99, 1, 4, new Vector3(0f, -1f, 0f), 0b1),
        new(NetEntityKind.Goblin, 12, new Vector3(0f, 64f, 0f), 0f, 10, 3, 2, Vector3.Zero),
        new(NetEntityKind.Drop, 13, new Vector3(4f, 70f, 4f), 0f, 0, 0, 200, new Vector3(1f, 2f, 3f)),
        // The kinds whose item field is a block or item id carry the widest one an id can hold
        new(NetEntityKind.Drop, 18, new Vector3(-4f, 70f, 4f), 0f, 0, 0, ushort.MaxValue, Vector3.Zero),
        new(NetEntityKind.FallingBlock, 19, new Vector3(4.5f, 80f, -4.5f), 0f, 0, 0, ushort.MaxValue, Vector3.Zero),
        new(NetEntityKind.PrimedTnt, 20, new Vector3(0.5f, 66f, 0.5f), 0f, 2500, 4000, ushort.MaxValue, Vector3.Zero),
        new(NetEntityKind.Iceberg, 14, new Vector3(100f, 62f, -50f), NetEntity.YawFromTurns(3), -77,
            NetEntity.PackIcebergAmmo(1.4f, 0.5f), 2, new Vector3(0.3f, 0f, 0.1f)),
    ];

    private static byte[] Frame()
    {
        using var writer = new PacketWriter(NetMessage.EntityList);
        foreach (var entry in Entries)
            NetEntityCodec.Write(writer, entry);
        return writer.ToArray();
    }

    [Fact]
    public void EveryEntryRoundTripsItsFields()
    {
        using var reader = new PacketReader(Frame());
        Assert.Equal(NetMessage.EntityList, reader.Message);
        foreach (var entry in Entries)
            Assert.Equal(entry, NetEntityCodec.Read(reader));
    }

    [Fact]
    public void AnEntryCutShortIsAShortFrame()
    {
        var full = Frame();
        using var reader = new PacketReader(full.AsSpan(0, full.Length - 1).ToArray());
        _ = reader.Message;
        for (var i = 0; i < Entries.Length - 1; i++)
            NetEntityCodec.Read(reader);
        Assert.Throws<EndOfStreamException>(() => NetEntityCodec.Read(reader));
    }
}
