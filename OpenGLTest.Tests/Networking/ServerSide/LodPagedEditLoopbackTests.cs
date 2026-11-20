using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using VoxelEngine.Effects.Climate;
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
    private enum ReceiptKind { FirstEight, Complete, RejectedComplete, TargetInvalidate }

    [Fact]
    public async Task TwoClientsSharePagedRawOwnershipAndMatchingCredit()
    {
        var state = new ServerState(null!);
        var builds = 0;
        var originals = new List<LodMeshData>();
        var server = new StreamRequests(state, (level, x, z) =>
        {
            var data = Fixture(level, x, z, ++builds);
            originals.Add(data);
            return data;
        });
        using var first = await Peer.Connect(state, server, 1, (0, 0, 0));
        using var second = await Peer.Connect(state, server, 2, (0, 0, 0));

        first.Request();
        second.Request();
        using (var receipt = first.Expect(ReceiptKind.FirstEight))
        {
            server.ServeLodRequests();
            await receipt.DispatchAsync();
        }
        Assert.Equal(8, first.Pages);
        Assert.Equal(0, second.Pages);
        Assert.Empty(first.Applied);
        Assert.True(first.Connection.LodLedger.OwnsRawBytes);

        state.Elapsed += NetworkConfig.NetLodResendSeconds + 1;
        using (var receipt = second.Expect(ReceiptKind.FirstEight))
        {
            server.ServeLodRequests();
            await receipt.DispatchAsync();
        }
        Assert.False(first.Connection.LodLedger.OwnsRawBytes); // expiry released raw bytes
        Assert.False(first.Connection.LodLedger.Available); // still waiting for a matching cancel
        Assert.Equal(8, second.Pages);
        Assert.Equal(2, builds); // second peer advanced while first ACK was withheld

        first.SendAck(first.LastBegin!.Value.Nonce + 1, first.LastBegin.Value.Generation,
            LodTransfer.Applied);
        Assert.False(first.Connection.LodLedger.Available);

        first.Streamer.CancelAll();
        first.FlushAck(LodTransfer.Canceled, first.LastBegin.Value.Nonce);
        Assert.True(first.Connection.LodLedger.Available);

        using (var receipt = second.Expect(ReceiptKind.Complete))
        {
            server.ServeLodRequests();
            await receipt.DispatchAsync();
        }
        Assert.Single(second.Applied);
        AssertPayload(second.Applied[0], originals[1]);
        Assert.Equal(second.LastBegin!.Value.Pages, second.Pages);
        second.FlushAck(LodTransfer.Applied);
        Assert.True(second.Connection.LodLedger.Available);

        first.Request();
        using (var receipt = first.Expect(ReceiptKind.FirstEight))
        {
            server.ServeLodRequests();
            await receipt.DispatchAsync();
        }
        using (var receipt = first.Expect(ReceiptKind.Complete))
        {
            server.ServeLodRequests();
            await receipt.DispatchAsync();
        }
        Assert.Single(first.Applied);
        AssertPayload(first.Applied[0], originals[2]);
        Assert.Equal(first.LastBegin!.Value.Pages + 8, first.Pages);
        Assert.False(first.Connection.LodLedger.OwnsRawBytes);
        Assert.False(first.Connection.LodLedger.Available); // Applied ACK is still held
        first.FlushAck(LodTransfer.Applied);
        Assert.True(first.Connection.LodLedger.Available);
        Assert.Equal(3, builds);
    }

    [Fact]
    public async Task EditInvalidationRejectsStalePagesAndReplacesResidentRegion()
    {
        var priorSeed = WorldSeed.Value;
        var priorProfile = WorldSeed.Profile;
        TestContent.EnsureLoaded(); // OnEditedColumn reads the real tree reach table.
        WorldSeed.Apply(WorldSeed.Default);
        var state = new ServerState(null!);
        var builds = 0;
        var originals = new List<LodMeshData>();
        var server = new StreamRequests(state, (level, x, z) =>
        {
            var data = Fixture(level, x, z, ++builds);
            originals.Add(data);
            return data;
        });
        var serverLod = new LodManager(null);
        serverLod.RelevantColumnEdited += server.OnEditedColumn;
        try
        {
            using var first = await Peer.Connect(state, server, 1, (0, 0, 0));
            using var second = await Peer.Connect(state, server, 2, (0, 1, 0));
            first.Request();
            second.Request();
            using (var receipt = first.Expect(ReceiptKind.FirstEight))
            {
                server.ServeLodRequests();
                await receipt.DispatchAsync();
            }
            Assert.Equal(8, first.Pages);
            Assert.Empty(first.Applied);

            // The actual relevant-edit event invalidates the active transfer before its
            // remaining pages. Region (1,0) is outside this column's level-0 reach.
            serverLod.MarkEditedColumn(0, 0);
            using (var firstReceipt = first.Expect(ReceiptKind.RejectedComplete))
            using (var secondReceipt = second.Expect(ReceiptKind.FirstEight))
            {
                server.ServeLodRequests();
                await firstReceipt.DispatchAsync();
                await secondReceipt.DispatchAsync();
            }
            Assert.Empty(first.Applied);
            Assert.Equal(LodTransfer.Rejected, first.LastCompleteStatus);
            Assert.Equal(8, second.Pages);
            first.FlushAck(LodTransfer.Canceled);
            Assert.True(first.Connection.LodLedger.Available);

            first.Request(); // the invalidation removed its old nonce
            using (var receipt = second.Expect(ReceiptKind.Complete))
            {
                server.ServeLodRequests();
                await receipt.DispatchAsync();
            }
            Assert.Single(second.Applied);
            AssertPayload(second.Applied[0], originals[1]);
            second.FlushAck(LodTransfer.Applied);
            using (var receipt = first.Expect(ReceiptKind.FirstEight))
            {
                server.ServeLodRequests();
                await receipt.DispatchAsync();
            }
            Assert.Equal(16, first.Pages);
            using (var receipt = first.Expect(ReceiptKind.Complete))
            {
                server.ServeLodRequests();
                await receipt.DispatchAsync();
            }
            Assert.Single(first.Applied);
            AssertPayload(first.Applied[0], originals[2]);
            first.FlushAck(LodTransfer.Applied);
            var oldResident = first.Lod.Regions[first.Target];
            var oldPayload = first.Applied[0];
            var oldY = oldPayload.Vertices[0].Position.Y;
            var staleComplete = first.LastCompleteFrame!;

            // A later edit retains the old drawable region until a valid replacement arrives.
            serverLod.MarkEditedColumn(0, 0);
            using (var receipt = first.Expect(ReceiptKind.TargetInvalidate))
            {
                server.ServeLodRequests();
                await receipt.DispatchAsync();
            }
            Assert.Same(oldResident, first.Lod.Regions[first.Target]);
            Assert.Single(first.Applied);
            Assert.Same(oldPayload, first.Applied[0]);
            first.Request();
            using (var receipt = first.Expect(ReceiptKind.FirstEight))
            {
                server.ServeLodRequests();
                await receipt.DispatchAsync();
            }
            Assert.Same(oldResident, first.Lod.Regions[first.Target]);
            Assert.Single(first.Applied);
            Assert.Same(oldPayload, first.Applied[0]);
            using (var receipt = first.Expect(ReceiptKind.Complete))
            {
                server.ServeLodRequests();
                await receipt.DispatchAsync();
            }
            Assert.Equal(2, first.Applied.Count);
            Assert.NotEqual(oldY, first.Applied[1].Vertices[0].Position.Y);
            Assert.NotSame(oldResident, first.Lod.Regions[first.Target]);
            AssertPayload(first.Applied[1], originals[3]);
            first.FlushAck(LodTransfer.Applied);

            first.Dispatch(staleComplete); // old nonce/generation cannot replace the new region
            Assert.Equal(2, first.Applied.Count);
            Assert.Equal(4, builds);
        }
        finally
        {
            try
            {
                serverLod.RelevantColumnEdited -= server.OnEditedColumn;
                serverLod.ReleaseAll();
            }
            finally { WorldSeed.Apply(priorSeed, priorProfile); }
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
        ushort[] woodFaces = [11, 12, 13, 14, 15, 16];
        ushort[] leafFaces = [21, 22, 23, 24, 25, 26];
        ushort[] leafTint = [31, 32, 33, 34, 35, 36];
        ushort[] absent = [LodTreeMaterial.NoTile, LodTreeMaterial.NoTile,
            LodTreeMaterial.NoTile, LodTreeMaterial.NoTile, LodTreeMaterial.NoTile,
            LodTreeMaterial.NoTile];
        Assert.True(LodTreeMaterial.TryCreate(woodFaces, absent, null, out var wood));
        Assert.True(LodTreeMaterial.TryCreate(leafFaces, leafTint,
            BiomeTintPalette.Foliage, out var leaves));
        var grass = new Vector3[25];
        var foliage = new Vector3[25];
        Array.Fill(grass, new Vector3(0.3f, 0.5f, 0.7f));
        Array.Fill(foliage, new Vector3(0.7f, 0.4f, 0.2f));
        var min = new int[16];
        var max = new int[16];
        Array.Fill(min, int.MaxValue);
        Array.Fill(max, int.MinValue);
        min[0] = 32;
        max[0] = 52;
        var trees = new LodTreeBuild([instance], [wood, leaves],
            new LodTreeTintLattice(4, grass, foliage),
            [1UL], min, max);
        var data = new LodMeshData(level, x, z, vertices, indices, [], [], 0, 64,
            new int[16], new int[16]) { Trees = trees };
        return data;
    }

    private static void AssertPayload(LodMeshData data, LodMeshData original)
    {
        Assert.Equal((original.Level, original.X, original.Z), (data.Level, data.X, data.Z));
        Assert.Equal(20_000, data.Vertices.Count);
        Assert.Equal(30_000, data.Indices.Count);
        Assert.Equal(original.Vertices[0], data.Vertices[0]);
        Assert.Equal(original.Vertices[^1], data.Vertices[^1]);
        Assert.Equal(original.Indices[0], data.Indices[0]);
        Assert.Equal(original.Indices[^1], data.Indices[^1]);
        Assert.Single(data.Trees.Instances);
        Assert.Equal(2, data.Trees.Materials.Length);
        Span<byte> actualInstance = stackalloc byte[LodTreeInstance.ByteSize];
        Span<byte> originalInstance = stackalloc byte[LodTreeInstance.ByteSize];
        data.Trees.Instances[0].WriteLittleEndian(actualInstance);
        original.Trees.Instances[0].WriteLittleEndian(originalInstance);
        Assert.True(actualInstance.SequenceEqual(originalInstance));
        Span<byte> actualMaterial = stackalloc byte[LodTreeMaterial.ByteSize];
        Span<byte> originalMaterial = stackalloc byte[LodTreeMaterial.ByteSize];
        for (var i = 0; i < 2; i++)
        {
            data.Trees.Materials[i].WriteLittleEndian(actualMaterial);
            original.Trees.Materials[i].WriteLittleEndian(originalMaterial);
            Assert.True(actualMaterial.SequenceEqual(originalMaterial));
        }
        Assert.Equal((ushort)11, data.Trees.Materials[0].BaseAt(0));
        Assert.Equal((ushort)21, data.Trees.Materials[1].BaseAt(0));
        Assert.Equal((ushort)31, data.Trees.Materials[1].TintedAt(0));
        Assert.Equal(BiomeTintPalette.Foliage, data.Trees.Materials[1].Palette);
        Assert.NotNull(data.Trees.Tint);
        Assert.NotNull(original.Trees.Tint);
        Assert.Equal(original.Trees.Tint!.Grass[0], data.Trees.Tint!.Grass[0]);
        Assert.Equal(original.Trees.Tint.Foliage[^1], data.Trees.Tint.Foliage[^1]);
        Assert.Equal(original.Trees.GeneratedTiles, data.Trees.GeneratedTiles);
        Assert.Equal(original.Trees.ProxyYMin, data.Trees.ProxyYMin);
        Assert.Equal(original.Trees.ProxyYMax, data.Trees.ProxyYMax);
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
            TcpClient? socket = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
                socket = await listener.AcceptTcpClientAsync(deadline.Token);
                socket.NoDelay = true;
                var connection = new ClientConnection(socket, id, new ConcurrentQueue<Inbound>());
                state.Clients.Add(id, connection);
                try { return new Peer(state, server, client, connection, target); }
                catch
                {
                    state.Clients.Remove(id);
                    connection.Close();
                    throw;
                }
            }
            catch
            {
                client.Dispose();
                socket?.Dispose();
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

        internal Receipt Expect(ReceiptKind kind) => new(this, kind);

        internal sealed class Receipt : IDisposable
        {
            private readonly Peer _peer;
            private readonly Task<List<byte[]>> _reader;
            private bool _done;

            internal Receipt(Peer peer, ReceiptKind kind)
            {
                _peer = peer;
                var expectedNonce = peer.LastBegin?.Nonce;
                _reader = Task.Run(() => Read(peer, kind, expectedNonce));
            }

            private static List<byte[]> Read(Peer peer, ReceiptKind kind, int? expectedNonce)
            {
                var frames = new List<byte[]>();
                var pages = 0;
                var beginNonce = 0;
                for (var i = 0; i < 64; i++)
                {
                    var bytes = NetFraming.ReadFrame(peer._clientStream)
                        ?? throw new EndOfStreamException();
                    frames.Add(bytes);
                    using var reader = new PacketReader(bytes);
                    switch (reader.Message)
                    {
                        case NetMessage.LodRegionBegin:
                            var begin = LodTransfer.ReadBegin(reader);
                            Assert.Equal(peer.Target, (begin.Region.Level, begin.Region.X, begin.Region.Z));
                            if (kind != ReceiptKind.FirstEight)
                                throw new InvalidDataException("Unexpected LOD begin.");
                            beginNonce = begin.Nonce;
                            break;
                        case NetMessage.LodRegionPage:
                            var nonce = reader.ReadInt();
                            if (nonce != (kind == ReceiptKind.FirstEight ? beginNonce : expectedNonce))
                                throw new InvalidDataException("Unexpected LOD page identity.");
                            pages++;
                            if (kind == ReceiptKind.FirstEight && pages == 8) return frames;
                            break;
                        case NetMessage.LodRegionComplete:
                            var completeNonce = reader.ReadInt();
                            var key = (reader.ReadInt(), reader.ReadInt(), reader.ReadInt());
                            _ = reader.ReadInt();
                            var status = reader.ReadByte();
                            Assert.Equal(peer.Target, key);
                            Assert.Equal(expectedNonce, completeNonce);
                            if (kind == ReceiptKind.Complete && status == LodTransfer.Applied ||
                                kind == ReceiptKind.RejectedComplete && status == LodTransfer.Rejected)
                                return frames;
                            throw new InvalidDataException("Unexpected LOD completion.");
                        case NetMessage.LodRegionInvalidate:
                            var invalidated = (reader.ReadInt(), reader.ReadInt(), reader.ReadInt());
                            _ = reader.ReadInt();
                            if (kind == ReceiptKind.TargetInvalidate && invalidated == peer.Target)
                                return frames;
                            break;
                        default:
                            throw new InvalidDataException("Unexpected frame in LOD receipt.");
                    }
                }
                throw new InvalidDataException("LOD receipt exceeded its 64-frame phase bound.");
            }

            internal async Task DispatchAsync()
            {
                List<byte[]> frames;
                try { frames = await _reader.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch
                {
                    _peer.CloseSockets();
                    try { await _reader; } catch { /* preserve original failure */ }
                    _done = true;
                    throw;
                }
                _done = true;
                foreach (var frame in frames) _peer.Dispatch(frame);
            }

            public void Dispose()
            {
                if (_done) return;
                _peer.CloseSockets();
                try { _reader.GetAwaiter().GetResult(); } catch { /* caller failed first */ }
                _done = true;
            }
        }

        private void CloseSockets()
        {
            _client.Dispose();
            Connection.Close();
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

        internal void FlushAck(byte status, int? nonce = null)
        {
            var index = _acks.FindIndex(ack =>
            {
                if (ack.Status != status) return false;
                using var reader = new PacketReader(ack.Frame);
                var ackNonce = reader.ReadInt();
                var key = (reader.ReadInt(), reader.ReadInt(), reader.ReadInt());
                return key == Target && (nonce == null || ackNonce == nonce);
            });
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
            try { Streamer.CancelAll(); }
            finally
            {
                try { _server.RemoveClient(Connection); }
                finally
                {
                    _state.Clients.Remove(Connection.Id);
                    try { Lod.ReleaseAll(); }
                    finally { CloseSockets(); }
                }
            }
        }
    }
}
