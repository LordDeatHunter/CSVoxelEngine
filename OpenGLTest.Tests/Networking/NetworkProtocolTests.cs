using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Config;
using VoxelEngine.Player;
using VoxelEngine.Server;
using VoxelEngine.UI.Hud;
using Xunit;

namespace VoxelEngine.Tests.Networking;

[Collection("Worldgen seed")]
public sealed class NetFramingTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public NetFramingTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PartialFrameWriteFailureIsTerminalAndNeverWritesAgain()
    {
        using var stream = new HeaderThenFailStream();
        using var client = new NetworkClient(new ServerAddress("127.0.0.1", 1), "Tester", 1, stream);
        var payload = new byte[1024];

        var send = Task.Run(() => client.Send(payload));
        await send.WaitAsync(TimeSpan.FromSeconds(2)); // watchdog ensures a broken send cannot hang the suite

        Assert.Equal(NetworkStatus.Failed, client.Status);
        Assert.Contains("sending", client.Error, StringComparison.OrdinalIgnoreCase);
        client.Send(payload);
        Assert.Equal(2, stream.WriteCalls); // header succeeded; payload failed; later send is inert
    }

    [Fact]
    public async Task ClientWriteToPeerThatStopsReadingFailsWithinConfiguredBound()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = (IPEndPoint)listener.LocalEndpoint;
        var acceptAndHandshake = Task.Run(() =>
        {
            var peer = listener.AcceptTcpClient();
            peer.ReceiveBufferSize = 4096;
            var stream = peer.GetStream();
            Assert.NotNull(NetFraming.ReadFrame(stream)); // client hello
            using var hello = new PacketWriter(NetMessage.ServerHello);
            hello.Write(NetFraming.ProtocolVersion).Write(1).Write("Slow reader")
                .Write(1).Write(0.0).Write(Vector3.Zero);
            NetFraming.WriteFrame(stream, hello.ToArray());
            return peer; // Keep the peer open but never read client payloads.
        });

        using var client = new NetworkClient(new ServerAddress("127.0.0.1", address.Port), "Tester", 3.0);
        var connect = Task.Run(client.Connect);
        TcpClient? peer = null;
        Task? sendLoop = null;
        using var sendCancellation = new CancellationTokenSource();
        TimeSpan elapsed = default;
        try
        {
            peer = await acceptAndHandshake.WaitAsync(TimeSpan.FromSeconds(5));
            await connect.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(NetworkStatus.Handshaken, client.Status);

            var payload = new byte[NetFraming.MaxPayload];
            var watch = Stopwatch.StartNew();
            sendLoop = Task.Run(() =>
            {
                while (!sendCancellation.IsCancellationRequested && client.Status == NetworkStatus.Handshaken)
                    client.Send(payload);
            });

            await sendLoop.WaitAsync(TimeSpan.FromMilliseconds(NetworkConfig.NetSendTimeoutMs + 3000));
            elapsed = watch.Elapsed;
        }
        finally
        {
            sendCancellation.Cancel();
            peer?.Dispose();
            client.Dispose();
            listener.Stop();
        }

        if (sendLoop != null)
            await sendLoop.WaitAsync(TimeSpan.FromSeconds(2)); // forced close above is the watchdog cleanup
        _output.WriteLine("Slow-reader send loop terminal status: {0}; elapsed: {1:F0} ms; configured write bound: {2} ms",
            client.Status, elapsed.TotalMilliseconds, NetworkConfig.NetSendTimeoutMs);
        Assert.Equal(NetworkStatus.Failed, client.Status);
        Assert.False(string.IsNullOrWhiteSpace(client.Error));
        Assert.InRange(elapsed.TotalMilliseconds, 0, NetworkConfig.NetSendTimeoutMs + 3000);
    }

    [Fact]
    public void SuccessfulSendKeepsConnectionHandshaken()
    {
        using var stream = new MemoryStream();
        using var client = new NetworkClient(new ServerAddress("127.0.0.1", 1), "Tester", 1, stream);

        client.Send(new byte[] { 7, 8, 9 });

        Assert.Equal(NetworkStatus.Handshaken, client.Status);
        Assert.Null(client.Error);
        Assert.Equal(new byte[] { 3, 0, 0, 0, 7, 8, 9 }, stream.ToArray());
    }

    private sealed class HeaderThenFailStream : Stream
    {
        public int WriteCalls { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCalls++;
            if (WriteCalls == 2)
                throw new IOException("simulated payload write timeout");
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteCalls++;
            if (WriteCalls == 2)
                throw new IOException("simulated payload write timeout");
        }
    }

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
/// modes, the health (clamped to the pool), the dead flag, a knockback, the air and the mana (each
/// clamped) through <see cref="PlayerStatusCodec"/>, refusing a non-finite shove as a malformed frame, and a
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

        // The air rides along and is clamped to a full lung
        Assert.Equal(7, RoundTrip(hurt with { Air = 7 }).Air);
        Assert.Equal(VoxelEngine.Player.Breath.NetSteps, RoundTrip(hurt with { Air = 250 }).Air);

        // The mana and its max ride along; the max is clamped to 1..MaxCap and the mana into it
        var casting = hurt with { Mana = 12, MaxMana = ManaConfig.StartMax + ManaConfig.MaxPerKnownPart };
        Assert.Equal(casting, RoundTrip(casting));
        var overflowing = RoundTrip(hurt with { Mana = 5000, MaxMana = 50 });
        Assert.Equal(50, overflowing.Mana);
        Assert.Equal(50, overflowing.MaxMana);
        Assert.Equal(ManaConfig.MaxCap, RoundTrip(hurt with { MaxMana = int.MaxValue }).MaxMana);
        var negative = RoundTrip(hurt with { Mana = -3, MaxMana = 0 });
        Assert.Equal(0, negative.Mana);
        Assert.Equal(1, negative.MaxMana);

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
    public void PlayerStatusRoundTripsItsEffectMask()
    {
        // The mask rides last and raw, every bit kept: the codec is content-free, and the client's
        // mirror (StatusEffectSet.SetMirrorMask) is what drops bits no loaded effect owns
        var status = new PlayerStatusData(NetPlayerFlags.None, 40, false, Vector3.Zero, 0f, EffectMask: 0b1010_0101);
        Assert.Equal(status, RoundTrip(status));
        Assert.Equal(0, RoundTrip(status with { EffectMask = 0 }).EffectMask);
        Assert.Equal(byte.MaxValue, RoundTrip(status with { EffectMask = byte.MaxValue }).EffectMask);

        // A status cut short before its mask is a short frame, never a status with no effects
        using var writer = new PacketWriter(NetMessage.PlayerStatus);
        PlayerStatusCodec.Write(writer, status);
        var full = writer.ToArray();
        using var reader = new PacketReader(full.AsSpan(0, full.Length - 1).ToArray());
        _ = reader.Message;
        Assert.Throws<EndOfStreamException>(() => PlayerStatusCodec.Read(reader));
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

    [Fact]
    public void TheIcebergPackingRoundTrips()
    {
        // The scale and sinking survive within their quantization, whatever the other holds
        foreach (var scale in new[] { 0.5f, 0.85f, 1f, 1.237f, 2f })
        foreach (var sink in new[] { 0f, 0.3f, 0.999f, 1f })
        {
            NetEntity.UnpackIcebergAmmo(NetEntity.PackIcebergAmmo(scale, sink), out var backScale, out var backSink);
            Assert.InRange(backScale, scale - 0.5e-3f - 1e-6f, scale + 0.5e-3f + 1e-6f);
            Assert.InRange(backSink, sink - 0.5f / 255f - 1e-6f, sink + 0.5f / 255f + 1e-6f);
        }

        // Every quarter turn survives the yaw it travels in
        for (var turns = 0; turns < 4; turns++)
            Assert.Equal(turns, NetEntity.TurnsFromYaw(NetEntity.YawFromTurns(turns)));
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
