using System;
using System.IO;
using System.Numerics;
using VoxelEngine.Networking;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// The <see cref="NetMessage.EntityList"/> entry codec the server writes and the client reads: every field of
/// every kind comes back exactly (a realm mob's attack count in the ammo field included), and an entry cut short
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
        new(NetEntityKind.Goblin, 12, new Vector3(0f, 64f, 0f), 0f, 10, 3, 2, Vector3.Zero),
        new(NetEntityKind.Drop, 13, new Vector3(4f, 70f, 4f), 0f, 0, 0, 200, new Vector3(1f, 2f, 3f)),
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
