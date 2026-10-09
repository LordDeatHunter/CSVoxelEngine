using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// End-to-end chunk streaming over a real loopback connection: a server-mode world serves a loaded
/// chunk and its column terrain, and a client chunk manager restores them from the wire - the path
/// a multiplayer client uses instead of worldgen.
/// </summary>
[Collection("Worldgen seed")]
public sealed class ChunkStreamingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ChunkStreamingTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AClientStreamsAChunkAndItsColumnFromTheServer()
    {
        WorldSeed.Apply(WorldSeed.Default);
        var store = new ChunkSaveStore(_dir);
        var world = new World(null!, null!, null!, null!, null!, null!, null, store, serverMode: true);
        try
        {
            var sw = Stopwatch.StartNew();
            while (!world.IsLoaded && sw.Elapsed.TotalSeconds < 45)
            {
                world.Update(0.05, null);
                Thread.Sleep(1);
            }

            Assert.True(world.IsLoaded, "the server world never finished loading");
            Chunk? serverChunk = null;
            foreach (var chunk in world.GetChunkManager().Chunks.Values)
            {
                serverChunk = chunk;
                break;
            }

            Assert.NotNull(serverChunk);
            var cx = (int)serverChunk.Position.X;
            var cy = (int)serverChunk.Position.Y;
            var cz = (int)serverChunk.Position.Z;

            using var server = new GameServer(world, "Test", WorldSeed.Default, 0);
            server.Start();
            using var client = new NetworkClient(new ServerAddress("127.0.0.1", server.BoundPort), "Tester", 5);
            var connect = new Thread(client.Connect) { IsBackground = true, Name = "stream-test-connect" };
            connect.Start();

            sw.Restart();
            while (client.Status == NetworkStatus.Connecting && sw.Elapsed.TotalSeconds < 5)
            {
                server.Pump(0.01);
                Thread.Sleep(5);
            }

            Assert.Equal(NetworkStatus.Handshaken, client.Status);

            using (var request = new PacketWriter(NetMessage.ChunkRequest))
            {
                request.Write(cx);
                request.Write(cy);
                request.Write(cz);
                client.Send(request.ToArray());
            }

            using (var request = new PacketWriter(NetMessage.ColumnRequest))
            {
                request.Write(cx);
                request.Write(cz);
                client.Send(request.ToArray());
            }

            var manager = new ChunkManager(null!);
            var streamer = new ClientChunkStreamer(manager, (_, _, _) => { }, (_, _) => { });
            var gotChunk = false;
            var gotColumn = false;
            var ids = new System.Collections.Generic.List<byte>();
            sw.Restart();
            while ((!gotChunk || !gotColumn) && sw.Elapsed.TotalSeconds < 5)
            {
                server.Pump(0.01);
                while (client.Inbound.TryDequeue(out var payload))
                {
                    using var reader = new PacketReader(payload);
                    var message = reader.Message;
                    ids.Add((byte)message);
                    if (message == NetMessage.ChunkData)
                    {
                        var rx = reader.ReadInt();
                        var ry = reader.ReadInt();
                        var rz = reader.ReadInt();
                        streamer.ApplyChunk(rx, ry, rz, reader.ReadBytes());
                        gotChunk = true;
                    }
                    else if (message == NetMessage.ColumnTerrain)
                    {
                        var rx = reader.ReadInt();
                        var rz = reader.ReadInt();
                        streamer.ApplyColumn(rx, rz, reader.ReadBytes());
                        gotColumn = true;
                    }
                }

                Thread.Sleep(5);
            }

            Assert.True(gotChunk, $"no chunk data came back from the server (ids: {string.Join(",", ids)})");
            Assert.True(gotColumn, $"no column terrain came back from the server (ids: {string.Join(",", ids)})");
            Assert.Equal(serverChunk.GetLocalBlock(0, 0, 0),
                manager.GetBlockGlobal(cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size));
            // The streamed column is what the client's mesh builds read (the manager's column
            // source), and exactly what the server's own worldgen reads for it: heights, water
            // levels and corner climate
            Assert.True(manager.ColumnSource.TryGet(cx, cz, out var got));
            Assert.True(WorldgenColumnSource.Instance.TryGet(cx, cz, out var expected));
            Assert.True(got.ContentEquals(expected), "the streamed column differs from the server's worldgen column");
        }
        finally
        {
            world.SaveAndDispose();
        }
    }

    [Fact]
    public void TheServerBuildsAndStreamsAnLodRegion()
    {
        WorldSeed.Apply(WorldSeed.Default);
        var store = new ChunkSaveStore(_dir);
        var world = new World(null!, null!, null!, null!, null!, null!, null, store, serverMode: true);
        try
        {
            var sw = Stopwatch.StartNew();
            while (!world.IsLoaded && sw.Elapsed.TotalSeconds < 45)
            {
                world.Update(0.05, null);
                Thread.Sleep(1);
            }

            Assert.True(world.IsLoaded);

            using var server = new GameServer(world, "Test", WorldSeed.Default, 0);
            server.Start();
            using var client = new NetworkClient(new ServerAddress("127.0.0.1", server.BoundPort), "Tester", 5);
            var connect = new Thread(client.Connect) { IsBackground = true, Name = "lod-test-connect" };
            connect.Start();
            sw.Restart();
            while (client.Status == NetworkStatus.Connecting && sw.Elapsed.TotalSeconds < 5)
            {
                server.Pump(0.01);
                Thread.Sleep(5);
            }

            Assert.Equal(NetworkStatus.Handshaken, client.Status);

            using (var request = new PacketWriter(NetMessage.LodRegionRequest))
            {
                request.Write(0);
                request.Write(0);
                request.Write(0);
                client.Send(request.ToArray());
            }

            LodMeshData? region = null;
            sw.Restart();
            while (region == null && sw.Elapsed.TotalSeconds < 10)
            {
                server.Pump(0.01);
                while (client.Inbound.TryDequeue(out var payload))
                {
                    using var reader = new PacketReader(payload);
                    if (reader.Message != NetMessage.LodRegionData)
                        continue;
                    reader.ReadInt();
                    reader.ReadInt();
                    reader.ReadInt();
                    region = LodNetCodec.Decode(reader.ReadBytes());
                }

                Thread.Sleep(5);
            }

            Assert.NotNull(region);
            Assert.Equal(0, region!.Level);
            Assert.Equal(0, region.X);
            Assert.Equal(0, region.Z);
            Assert.NotEmpty(region.Vertices);
        }
        finally
        {
            world.SaveAndDispose();
        }
    }
}
