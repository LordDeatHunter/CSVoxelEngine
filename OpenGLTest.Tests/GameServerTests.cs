using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// A real <see cref="GameServer"/> over loopback: a malformed frame drops only its sender, every
/// joiner spawns at the world spawn rather than at the players' centroid, and far-LOD requests
/// are built a bounded number per Pump with a repeated request served once.
/// </summary>
[Collection("Worldgen seed")]
public sealed class GameServerTests : IDisposable
{
    private readonly World _world;
    private readonly GameServer _server;

    public GameServerTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, serverMode: true);
        _server = new GameServer(_world, "Test World", WorldSeed.Default, 0);
        _server.Start();
    }

    public void Dispose()
    {
        _server.Dispose();
        _world.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private NetworkClient Connect(string name)
    {
        var client = new NetworkClient(new ServerAddress("127.0.0.1", _server.BoundPort), name, 5.0);
        new Thread(client.Connect) { IsBackground = true, Name = "test-connect" }.Start();
        var sw = Stopwatch.StartNew();
        while (client.Status == NetworkStatus.Connecting && sw.Elapsed.TotalSeconds < 15)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
        }

        Assert.Equal(NetworkStatus.Handshaken, client.Status);
        return client;
    }

    private void PumpFor(double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void AMalformedFrameDropsItsSenderAndTheServerKeepsServing()
    {
        // A one-byte ClientHello: the version read runs off the end of the payload
        using var raw = new TcpClient();
        raw.Connect("127.0.0.1", _server.BoundPort);
        var stream = raw.GetStream();
        var frame = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 1);
        frame[4] = (byte)NetMessage.ClientHello;
        stream.Write(frame);
        stream.Flush();

        // Pump until the server has handled the frame and closed the socket (a readable socket
        // with nothing buffered is a closed one), bounded so a hang fails instead of blocking
        var sw = Stopwatch.StartNew();
        while (!raw.Client.Poll(0, SelectMode.SelectRead) && sw.Elapsed.TotalSeconds < 15)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
        }

        // The bad peer was closed: its read ends instead of blocking
        stream.ReadTimeout = 2000;
        int read;
        try
        {
            read = stream.Read(new byte[16]);
        }
        catch (System.IO.IOException e) when (e.InnerException is SocketException { SocketErrorCode: not SocketError.TimedOut })
        {
            read = 0; // a reset is a close too
        }
        Assert.Equal(0, read);

        using var good = Connect("Tester");
        Assert.Equal(1, _server.ClientCount);
    }

    [Fact]
    public void ALaterJoinerSpawnsAtTheWorldSpawnNotTheCentroid()
    {
        using var first = Connect("First");
        var spawn = first.Server!.Value.Spawn;

        // The first player walks off; the server's chunk focus follows them
        PumpFor(0.1);
        var moved = spawn + new Vector3(1.5f, 0f, 0f);
        using (var state = new PacketWriter(NetMessage.PlayerState))
        {
            state.Write(moved);
            state.Write(0f);
            state.Write(0f);
            state.Write((byte)0);
            first.Send(state.ToArray());
        }

        var sw = Stopwatch.StartNew();
        while (Vector3.Distance(_world.Camera.Position, moved) > 0.01f && sw.Elapsed.TotalSeconds < 15)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
        }
        Assert.True(Vector3.Distance(_world.Camera.Position, moved) <= 0.01f, "the focus never followed the player");

        using var second = Connect("Second");
        Assert.Equal(spawn, second.Server!.Value.Spawn);
    }

    [Fact]
    public void LodRequestsAreBuiltABoundedNumberPerPumpAndARepeatOnce()
    {
        using var client = Connect("Tester");
        while (client.Inbound.TryDequeue(out _)) { }

        var distinct = WorldGenerationConfig.NetLodBuildsPerPump + 2;
        void Request(int rx)
        {
            using var writer = new PacketWriter(NetMessage.LodRegionRequest);
            writer.Write(0);
            writer.Write(rx);
            writer.Write(0);
            client.Send(writer.ToArray());
        }

        // Region 0 asked three times while it is still queued
        Request(0);
        Request(0);
        for (var rx = 1; rx < distinct; rx++)
            Request(rx);
        Request(0);
        Thread.Sleep(300);

        var received = new List<int>();
        void Drain()
        {
            while (client.Inbound.TryDequeue(out var payload))
            {
                using var reader = new PacketReader(payload);
                if (reader.Message != NetMessage.LodRegionData)
                    continue;
                reader.ReadInt();
                received.Add(reader.ReadInt());
            }
        }

        _server.Pump(0.01);
        Thread.Sleep(200);
        Drain();
        Assert.True(received.Count <= WorldGenerationConfig.NetLodBuildsPerPump,
            $"{received.Count} regions built in one Pump");

        var sw = Stopwatch.StartNew();
        while (received.Count < distinct && sw.Elapsed.TotalSeconds < 60)
        {
            _server.Pump(0.01);
            Thread.Sleep(20);
            Drain();
        }
        PumpFor(0.2);
        Drain();

        Assert.Equal(distinct, received.Count);
        Assert.Single(received, rx => rx == 0);
    }
}
