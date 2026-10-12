using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using VoxelEngine.Server;
using VoxelEngine.UI.Hud;
using Xunit;

namespace VoxelEngine.Tests.Networking;

public sealed class NetFramingTests
{
    [Fact]
    public void FramesRoundTripInOrderAndEndCleanly()
    {
        using var stream = new MemoryStream();
        var first = new byte[] { 1, 2, 3, 4, 5 };
        var second = new byte[] { 9 };

        NetFraming.WriteFrame(stream, first);
        NetFraming.WriteFrame(stream, second);
        stream.Position = 0;

        Assert.Equal(first, NetFraming.ReadFrame(stream));
        Assert.Equal(second, NetFraming.ReadFrame(stream));
        Assert.Null(NetFraming.ReadFrame(stream));
    }

    [Fact]
    public void AnOversizedLengthIsRefused()
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, NetFraming.MaxPayload + 1);
        stream.Write(length);
        stream.Position = 0;

        Assert.Null(NetFraming.ReadFrame(stream));
    }

    [Fact]
    public void PacketsRoundTripTheirFields()
    {
        using var writer = new PacketWriter(NetMessage.PlayerState);
        writer.Write(new Vector3(1f, 2f, 3f)).Write(45f).Write(-10f).Write((byte)3);

        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.PlayerState, reader.Message);
        Assert.Equal(new Vector3(1f, 2f, 3f), reader.ReadVector3());
        Assert.Equal(45f, reader.ReadFloat());
        Assert.Equal(-10f, reader.ReadFloat());
        Assert.Equal((byte)3, reader.ReadByte());
    }

    [Fact]
    public void ConnectingToAClosedPortFailsWithoutThrowing()
    {
        // The connect runs on a dedicated thread and any escape would crash the game, so a refused
        // port must come back as a Failed status with a reason, never an exception.
        using var client = new NetworkClient(new ServerAddress("127.0.0.1", 1), "Tester", 2.0);
        client.Connect();

        Assert.Equal(NetworkStatus.Failed, client.Status);
        Assert.False(string.IsNullOrEmpty(client.Error));
    }
}

/// <summary>
/// The protocol-4 player messages: a <see cref="NetMessage.PlayerStatus"/> carries the granted
/// modes, the health (clamped to the pool), the dead flag and a knockback through
/// <see cref="PlayerStatusCodec"/>, refusing a non-finite shove as a malformed frame, and a
/// <see cref="NetMessage.PlayerCorrection"/> carries its sequence id and eye.
/// </summary>
public sealed class NetworkProtocolTests
{
    private static PlayerStatusData RoundTrip(in PlayerStatusData status)
    {
        using var writer = new PacketWriter(NetMessage.PlayerStatus);
        PlayerStatusCodec.Write(writer, status);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.PlayerStatus, reader.Message);
        return PlayerStatusCodec.Read(reader);
    }

    private static byte[] StatusFrame(Action<PacketWriter> body)
    {
        using var writer = new PacketWriter(NetMessage.PlayerStatus);
        body(writer);
        return writer.ToArray();
    }

    [Fact]
    public void PlayerStatusAndCorrectionRoundTripTheirFields()
    {
        var hurt = new PlayerStatusData(NetPlayerFlags.Creative | NetPlayerFlags.Flying, PlayerHealth.MaxHealth / 2,
            Dead: false, new Vector3(3.5f, 0f, -2f), 4.25f);
        Assert.Equal(hurt, RoundTrip(hurt));

        var dead = new PlayerStatusData(NetPlayerFlags.None, 0, Dead: true, Vector3.Zero, 0f);
        Assert.Equal(dead, RoundTrip(dead));

        // A health outside the pool is clamped into it, and mode bits the protocol does not define
        // are dropped rather than handed to the camera
        var overfull = RoundTrip(new PlayerStatusData((NetPlayerFlags)0xFF, PlayerHealth.MaxHealth + 50, false,
            Vector3.Zero, 0f));
        Assert.Equal(PlayerHealth.MaxHealth, overfull.Health);
        Assert.Equal(NetPlayerFlags.Creative | NetPlayerFlags.Flying, overfull.Modes);
        Assert.Equal(0, RoundTrip(dead with { Health = -10 }).Health);

        // A correction: the sequence id the client acks, then the eye it snaps to
        var eye = new Vector3(12.5f, 70.62f, -8.5f);
        using var writer = new PacketWriter(NetMessage.PlayerCorrection);
        writer.Write(7).Write(eye);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.PlayerCorrection, reader.Message);
        Assert.Equal(7, reader.ReadInt());
        Assert.Equal(eye, reader.ReadVector3());
    }

    [Fact]
    public void ANonFiniteKnockbackIsAMalformedStatus()
    {
        static void AssertBad<T>(byte[] frame) where T : Exception
        {
            using var reader = new PacketReader(frame);
            _ = reader.Message;
            Assert.Throws<T>(() => PlayerStatusCodec.Read(reader));
        }

        // A NaN shove, an infinite one and an infinite lift are malformed frames
        AssertBad<InvalidDataException>(StatusFrame(w =>
            PlayerStatusCodec.Write(w, new PlayerStatusData(NetPlayerFlags.None, 50, false,
                new Vector3(float.NaN, 0f, 1f), 0f))));
        AssertBad<InvalidDataException>(StatusFrame(w =>
            PlayerStatusCodec.Write(w, new PlayerStatusData(NetPlayerFlags.None, 50, false,
                new Vector3(0f, 0f, float.PositiveInfinity), 0f))));
        AssertBad<InvalidDataException>(StatusFrame(w =>
            PlayerStatusCodec.Write(w, new PlayerStatusData(NetPlayerFlags.None, 50, false,
                Vector3.UnitX, float.NegativeInfinity))));
        // A status cut short before its knockback is a short frame
        AssertBad<EndOfStreamException>(StatusFrame(w => { w.Write((byte)0); w.Write(50); w.Write(false); }));
    }
}

/// <summary>
/// A real <see cref="GameServer"/> and <see cref="NetworkClient"/> over loopback: the handshake
/// delivers the world seed, and a chat line goes out and comes back to the sender.
/// </summary>
[Collection("Worldgen seed")]
public sealed class NetworkLoopbackTests : IDisposable
{
    public NetworkLoopbackTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    [Fact]
    public void HandshakeDeliversTheWorldAndChatComesBack()
    {
        WorldSeed.Apply(WorldSeed.Default);
        var world = new World(null!, null!, null!, null!, null!, null!, null, null, serverMode: true);
        using var server = new GameServer(world, "Test World", WorldSeed.Default, 0);
        server.Start();

        using var client = new NetworkClient(
            new ServerAddress("127.0.0.1", server.BoundPort), "Tester", 5.0);
        var connectThread = new Thread(client.Connect) { IsBackground = true, Name = "test-connect" };
        connectThread.Start();

        var sw = Stopwatch.StartNew();
        while (client.Status == NetworkStatus.Connecting && sw.Elapsed.TotalSeconds < 5)
        {
            server.Pump(0.01);
            Thread.Sleep(5);
        }

        Assert.Equal(NetworkStatus.Handshaken, client.Status);
        Assert.True(client.Server.HasValue);
        Assert.Equal("Test World", client.Server!.Value.WorldName);
        Assert.Equal(WorldSeed.Default, client.Server.Value.Seed);

        using (var chat = new PacketWriter(NetMessage.ChatSend))
        {
            chat.Write("hello");
            client.Send(chat.ToArray());
        }

        var seen = false;
        sw.Restart();
        while (!seen && sw.Elapsed.TotalSeconds < 5)
        {
            server.Pump(0.01);
            while (client.Inbound.TryDequeue(out var payload))
            {
                using var reader = new PacketReader(payload);
                if (reader.Message != NetMessage.ChatMessage)
                    continue;
                var kind = (ChatKind)reader.ReadByte();
                var sender = reader.ReadString();
                var text = reader.ReadString();
                if (kind == ChatKind.Player && sender == "Tester" && text == "hello")
                    seen = true;
            }
            Thread.Sleep(5);
        }

        Assert.True(seen, "the chat line never came back from the server");
        world.SaveAndDispose();
    }
}
