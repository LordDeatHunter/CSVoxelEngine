using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Threading.Tasks;
using VoxelEngine.Data;
using VoxelEngine.Lod;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.Networking.ServerSide;

/// <summary>
/// Real framed loopback for the LOD server/client components. The server's mesh producer is
/// supplied explicitly so these facts start no World, terrain worker or GL context.
/// </summary>
[Collection("Worldgen seed")]
public sealed class LodPagedEditLoopbackTests
{
    [Fact]
    public async Task TwoClientsSharePagedRawOwnershipAndMatchingCredit()
    {
        var state = new ServerState(null!);
        var builds = 0;
        var server = new StreamRequests(state, (level, x, z) =>
            Fixture(level, x, z, ++builds));
        using var first = await Peer.Connect(state, server, 1, (0, 0, 0));
        using var second = await Peer.Connect(state, server, 2, (0, 0, 0));

        first.Request();
        second.Request();
        server.ServeLodRequests();
        first.Drain();
        second.Drain();
        Assert.Equal(8, first.Pages);
        Assert.Equal(0, second.Pages);
        Assert.Empty(first.Applied);
        Assert.True(first.Connection.LodLedger.OwnsRawBytes);

        server.ServeLodRequests();
        first.Drain();
        Assert.Single(first.Applied);
        AssertPayload(first.Applied[0], first.Target);
        Assert.False(first.Connection.LodLedger.OwnsRawBytes);
        Assert.False(first.Connection.LodLedger.Available); // the real client ACK is held
        Assert.Single(first.PendingAcks);

        state.Elapsed += NetworkConfig.NetLodResendSeconds + 1;
        server.ServeLodRequests();
        second.Drain();
        Assert.Equal(8, second.Pages);
        Assert.False(first.Connection.LodLedger.Available);
        Assert.Equal(2, builds); // second peer advanced while first ACK was withheld

        first.SendAck(first.LastBegin!.Value.Nonce + 1, first.LastBegin.Value.Generation,
            LodTransfer.Applied);
        Assert.False(first.Connection.LodLedger.Available);

        server.ServeLodRequests();
        second.Drain();
        Assert.Single(second.Applied);
        AssertPayload(second.Applied[0], second.Target);
        Assert.Equal(first.LastBegin.Value.Pages, first.Pages);
        Assert.Equal(second.LastBegin!.Value.Pages, second.Pages);
        second.FlushAck(LodTransfer.Applied);
        Assert.True(second.Connection.LodLedger.Available);
        Assert.False(first.Connection.LodLedger.Available);
        first.FlushAck(LodTransfer.Applied);
        Assert.True(first.Connection.LodLedger.Available);
        Assert.Equal(2, builds);
    }

    [Fact]
    public async Task EditInvalidationRejectsStalePagesAndReplacesResidentRegion()
    {
        TestContent.EnsureLoaded(); // OnEditedColumn reads the real tree reach table.
        WorldSeed.Apply(WorldSeed.Default);
        var state = new ServerState(null!);
        var builds = 0;
        var server = new StreamRequests(state, (level, x, z) =>
            Fixture(level, x, z, ++builds));
        var serverLod = new LodManager(null);
        serverLod.RelevantColumnEdited += server.OnEditedColumn;
        try
        {
            using var first = await Peer.Connect(state, server, 1, (0, 0, 0));
            using var second = await Peer.Connect(state, server, 2, (0, 1, 0));
            first.Request();
            second.Request();
            server.ServeLodRequests();
            first.Drain();
            Assert.Equal(8, first.Pages);
            Assert.Empty(first.Applied);

            // The actual relevant-edit event invalidates the active transfer before its
            // remaining pages. Region (1,0) is outside this column's level-0 reach.
            serverLod.MarkEditedColumn(0, 0);
            server.ServeLodRequests();
            first.Drain();
            second.Drain();
            Assert.Empty(first.Applied);
            Assert.Equal(LodTransfer.Rejected, first.LastCompleteStatus);
            Assert.Equal(8, second.Pages);
            first.FlushAck(LodTransfer.Canceled);
            Assert.True(first.Connection.LodLedger.Available);

            first.Request(); // the invalidation removed its old nonce
            server.ServeLodRequests();
            second.Drain();
            Assert.Single(second.Applied);
            second.FlushAck(LodTransfer.Applied);
            server.ServeLodRequests();
            first.Drain();
            Assert.Equal(16, first.Pages);
            server.ServeLodRequests();
            first.Drain();
            Assert.Single(first.Applied);
            AssertPayload(first.Applied[0], first.Target);
            first.FlushAck(LodTransfer.Applied);
            var oldResident = first.Lod.Regions[first.Target];
            var oldY = first.Applied[0].Vertices[0].Position.Y;
            var staleComplete = first.LastCompleteFrame!;

            // A later edit retains the old drawable region until a valid replacement arrives.
            serverLod.MarkEditedColumn(0, 0);
            server.ServeLodRequests();
            first.Drain();
            second.Drain();
            Assert.Same(oldResident, first.Lod.Regions[first.Target]);
            first.Request();
            server.ServeLodRequests();
            first.Drain();
            Assert.Same(oldResident, first.Lod.Regions[first.Target]);
            server.ServeLodRequests();
            first.Drain();
            Assert.Equal(2, first.Applied.Count);
            Assert.NotEqual(oldY, first.Applied[1].Vertices[0].Position.Y);
            Assert.NotSame(oldResident, first.Lod.Regions[first.Target]);
            AssertPayload(first.Applied[1], first.Target);
            first.FlushAck(LodTransfer.Applied);

            first.Dispatch(staleComplete); // old nonce/generation cannot replace the new region
            Assert.Equal(2, first.Applied.Count);
            Assert.Equal(4, builds);
        }
        finally
        {
            serverLod.RelevantColumnEdited -= server.OnEditedColumn;
            serverLod.ReleaseAll();
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    private static LodMeshData Fixture(int level, int x, int z, int revision)
    {
        Assert.Equal(0, level);
        var vertex = new Vertex(new Vector3(1, 2 + revision, 3), 255, 255, 255, 255,
            Vector2.Zero, 1);
        var vertices = new List<Vertex>(20_000);
        for (var i = 0; i < 20_000; i++) vertices.Add(vertex);
        var indices = new List<ushort>(30_000);
        for (var i = 0; i < 30_000; i++) indices.Add(0);

        var originX = x * LodRegion.Levels[level].RegionBlocks;
        var originZ = z * LodRegion.Levels[level].RegionBlocks;
        var proxy = new LodTreeProxy(originX + 2, originZ + 3, 32, 40, 52,
            originX, originX + 64, originZ, originZ + 64, 1, 11, 12, LodCrownProfile.Dome);
        Assert.True(LodTreeInstance.TryCreate(proxy, 0, 1, 2, false, out var instance));
        ushort[] faces = [1, 1, 1, 1, 1, 1];
        ushort[] absent = [LodTreeMaterial.NoTile, LodTreeMaterial.NoTile,
            LodTreeMaterial.NoTile, LodTreeMaterial.NoTile, LodTreeMaterial.NoTile,
            LodTreeMaterial.NoTile];
        Assert.True(LodTreeMaterial.TryCreate(faces, absent, null, out var material));
        var min = new int[16];
        var max = new int[16];
        Array.Fill(min, int.MaxValue);
        Array.Fill(max, int.MinValue);
        min[0] = 32;
        max[0] = 52;
        var trees = new LodTreeBuild([instance], [material, material],
            new LodTreeTintLattice(4, new Vector3[25], new Vector3[25]),
            [1UL], min, max);
        var data = new LodMeshData(level, x, z, vertices, indices, [], [], 0, 64,
            new int[16], new int[16]) { Trees = trees };
        return data;
    }

    private static void AssertPayload(LodMeshData data, (int Level, int X, int Z) key)
    {
        Assert.Equal(key, (data.Level, data.X, data.Z));
        Assert.Equal(20_000, data.Vertices.Count);
        Assert.Equal(30_000, data.Indices.Count);
        Assert.Equal(0, (int)data.Indices[0]);
        Assert.Single(data.Trees.Instances);
        Assert.Equal(2, data.Trees.Materials.Length);
        Assert.Equal(1UL, data.Trees.GeneratedTiles[0]);
        Assert.Equal(32, data.Trees.ProxyYMin[0]);
        Assert.Equal(52, data.Trees.ProxyYMax[0]);
    }

    private sealed class Peer : IDisposable
    {
        private readonly ServerState _state;
        private readonly StreamRequests _server;
        private readonly TcpClient _client;
        private readonly NetworkStream _clientStream;
        private readonly List<(byte[] Frame, byte Status)> _acks = [];
        private int _sentRequests;

        internal (int Level, int X, int Z) Target { get; }
        internal ClientConnection Connection { get; }
        internal LodManager Lod { get; } = new(null);
        internal ClientLodStreamer Streamer { get; }
        internal List<LodMeshData> Applied { get; } = [];
        internal int Pages { get; private set; }
        internal int ScheduledRequests { get; private set; }
        internal LodTransfer.Begin? LastBegin { get; private set; }
        internal byte LastCompleteStatus { get; private set; }
        internal byte[]? LastCompleteFrame { get; private set; }
        internal IReadOnlyList<(byte[] Frame, byte Status)> PendingAcks => _acks;

        private Peer(ServerState state, StreamRequests server, TcpClient client,
            ClientConnection connection, (int Level, int X, int Z) target)
        {
            _state = state;
            _server = server;
            _client = client;
            _clientStream = client.GetStream();
            Connection = connection;
            Target = target;
            _clientStream.ReadTimeout = 2_000;
            _clientStream.WriteTimeout = 2_000;
            connection.Stream.ReadTimeout = 2_000;
            connection.Stream.WriteTimeout = 2_000;
            Streamer = new ClientLodStreamer(Lod, OnRequest, OnAck, apply: OnApply);
        }

        internal static async Task<Peer> Connect(ServerState state, StreamRequests server,
            int id, (int Level, int X, int Z) target)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient { NoDelay = true };
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var accept = listener.AcceptTcpClientAsync();
                await client.ConnectAsync(IPAddress.Loopback, port)
                    .WaitAsync(TimeSpan.FromSeconds(3));
                var socket = await accept.WaitAsync(TimeSpan.FromSeconds(3));
                socket.NoDelay = true;
                var connection = new ClientConnection(socket, id, new ConcurrentQueue<Inbound>());
                state.Clients.Add(id, connection);
                return new Peer(state, server, client, connection, target);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        internal void Request()
        {
            var before = _sentRequests;
            Streamer.Update(new Vector3(Target.X * 64, 0, Target.Z * 64), 0);
            Assert.Equal(before + 1, _sentRequests);
            using var frame = new PacketReader(NetFraming.ReadFrame(Connection.Stream)
                ?? throw new EndOfStreamException());
            Assert.Equal(NetMessage.LodRegionRequest, frame.Message);
            _server.OnLodRegionRequest(Connection, frame);
        }

        private void OnRequest(int level, int x, int z, int nonce)
        {
            ScheduledRequests++;
            if ((level, x, z) != Target) return;
            using var writer = new PacketWriter(NetMessage.LodRegionRequest);
            writer.Write(level).Write(x).Write(z).Write(nonce);
            NetFraming.WriteFrame(_clientStream, writer.ToArray());
            _sentRequests++;
        }

        private void OnAck(int nonce, LodTransfer.Key key, int generation, byte status)
        {
            using var writer = new PacketWriter(NetMessage.LodRegionAck);
            writer.Write(nonce).Write(key.Level).Write(key.X).Write(key.Z)
                .Write(generation).Write(status);
            _acks.Add((writer.ToArray(), status));
        }

        private void OnApply(LodMeshData data)
        {
            Applied.Add(data);
            // Production decode/revision/ACK remains in ClientLodStreamer. The headless
            // manager stores the same source/tree/bounds, without a GL terrain upload.
            Lod.AddStreamedRegion(data with
            {
                Vertices = [],
                Indices = [],
                WaterVertices = [],
                WaterIndices = []
            });
        }

        internal void Drain()
        {
            for (var i = 0; i < 128 && _clientStream.DataAvailable; i++)
            {
                var bytes = NetFraming.ReadFrame(_clientStream)
                    ?? throw new EndOfStreamException();
                Dispatch(bytes);
            }
            Assert.False(_clientStream.DataAvailable); // never leave an unbounded frame tail
        }

        internal void Dispatch(byte[] bytes)
        {
            using (var peek = new PacketReader(bytes))
            {
                if (peek.Message == NetMessage.LodRegionBegin)
                {
                    LastBegin = LodTransfer.ReadBegin(peek);
                    Assert.InRange(LastBegin.Value.Length, 8 * LodTransfer.PageBytes + 1,
                        11 * LodTransfer.PageBytes);
                    Assert.InRange(LastBegin.Value.Pages, 9, 11);
                }
                else if (peek.Message == NetMessage.LodRegionPage)
                    Pages++;
                else if (peek.Message == NetMessage.LodRegionComplete)
                {
                    _ = peek.ReadInt();
                    _ = peek.ReadInt();
                    _ = peek.ReadInt();
                    _ = peek.ReadInt();
                    _ = peek.ReadInt();
                    LastCompleteStatus = peek.ReadByte();
                    LastCompleteFrame = bytes;
                }
            }
            using var reader = new PacketReader(bytes);
            switch (reader.Message)
            {
                case NetMessage.LodRegionBegin: Streamer.Begin(reader); break;
                case NetMessage.LodRegionPage: Streamer.Page(reader); break;
                case NetMessage.LodRegionComplete: Streamer.Complete(reader); break;
                case NetMessage.LodRegionInvalidate: Streamer.Invalidate(reader); break;
                default: throw new InvalidDataException($"Unexpected LOD frame {reader.Message}.");
            }
        }

        internal void FlushAck(byte status)
        {
            var index = _acks.FindIndex(ack => ack.Status == status);
            Assert.True(index >= 0);
            var frame = _acks[index].Frame;
            _acks.RemoveAt(index);
            NetFraming.WriteFrame(_clientStream, frame);
            using var reader = new PacketReader(NetFraming.ReadFrame(Connection.Stream)
                ?? throw new EndOfStreamException());
            Assert.Equal(NetMessage.LodRegionAck, reader.Message);
            _server.OnLodAck(Connection, reader);
        }

        internal void SendAck(int nonce, int generation, byte status)
        {
            using var writer = new PacketWriter(NetMessage.LodRegionAck);
            writer.Write(nonce).Write(Target.Level).Write(Target.X).Write(Target.Z)
                .Write(generation).Write(status);
            NetFraming.WriteFrame(_clientStream, writer.ToArray());
            using var reader = new PacketReader(NetFraming.ReadFrame(Connection.Stream)
                ?? throw new EndOfStreamException());
            Assert.Equal(NetMessage.LodRegionAck, reader.Message);
            _server.OnLodAck(Connection, reader);
        }

        public void Dispose()
        {
            Streamer.CancelAll();
            _server.RemoveClient(Connection);
            _state.Clients.Remove(Connection.Id);
            Lod.ReleaseAll();
            _client.Dispose();
            Connection.Close();
        }
    }
}
