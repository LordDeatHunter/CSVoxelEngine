using System;
using System.Collections.Generic;
using System.IO;
using VoxelEngine.Lod;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Networking.ServerSide;

/// <summary>Answers a client's streaming requests from the loaded world: chunks, column terrain and far-LOD regions, the regions queued and built a bounded number per Pump. Main thread only.</summary>
internal sealed class StreamRequests
{
    private readonly ServerState _state;
    private readonly Func<int, int, int, LodMeshData>? _meshProducer;

    // Far-LOD region requests waiting to be built, served NetLodBuildsPerPump per Pump. Each
    // connection's own set dedupes a region it re-asked for while still queued.
    private readonly LinkedList<(ClientConnection Client, int Level, int Rx, int Rz, int Nonce)> _lodQueue = new();
    private readonly Dictionary<(ClientConnection Client, int Level, int Rx, int Rz),
        LinkedListNode<(ClientConnection Client, int Level, int Rx, int Rz, int Nonce)>> _queuedNodes = [];
    private readonly LodEditInvalidations _invalidations = new();
    private Outbound? _active;
    private const int MaxQueueScanPerPump = 64;
    private const int MaxPagesPerPump = 8;

    private sealed class Outbound(ClientConnection client, LodTransfer.Begin begin, byte[] bytes)
    {
        internal ClientConnection Client = client;
        internal LodTransfer.Begin Header = begin;
        internal byte[] Bytes = bytes;
        internal int NextPage;
        internal bool Began;
        internal bool Stale;
    }

    internal void OnEditedColumn(int x, int z)
    {
        Span<int> reaches = stackalloc int[LodRegion.Levels.Length];
        var disturbed = TreeCanopy.IsDisturbed(x, z);
        for (var level = 0; level < reaches.Length; level++)
            reaches[level] = LodTreeAnchor.EditReach(LodRegion.Levels[level],
                TreeGenerator.ReachHorizontal, disturbed);
        _invalidations.AddColumn(x, z, reaches);
        if (_active is { } active && _invalidations.Affects(active.Header.Region))
            active.Stale = true;
        foreach (var client in _state.Clients.Values)
            if (client.LodFailedKey is { } failed &&
                _invalidations.Affects(new LodTransfer.Key(failed.Level, failed.Rx, failed.Rz)))
                client.LodFailedUntil = 0;
    }

    internal StreamRequests(ServerState state, Func<int, int, int, LodMeshData>? meshProducer = null)
    {
        _state = state;
        _meshProducer = meshProducer;
    }

    internal void OnChunkRequest(ClientConnection client, PacketReader reader)
    {
            var cx = reader.ReadInt();
            var cy = reader.ReadInt();
            var cz = reader.ReadInt();
            // Only loaded chunks are sent: a request never forces generation, so it cannot
            // make the server build chunks it would not already have loaded around the players.
            if (_state.World.GetChunkManager().TryCaptureChunk(cx, cy, cz, out var snapshot))
                SendChunk(client, cx, cy, cz, snapshot);
    }

    internal void OnColumnRequest(ClientConnection client, PacketReader reader)
    {
            var cx = reader.ReadInt();
            var cz = reader.ReadInt();
            // Exactly what this server's own mesher reads for the column (heights, water
            // levels, corner climate) from the world's source - worldgen, or an imported map
            // where it supplies the column - so a client derives nothing from worldgen.
            _state.World.GetChunkManager().ColumnSource.TryGet(cx, cz, out var terrain);
            using var writer = new PacketWriter(NetMessage.ColumnTerrain);
            writer.Write(cx);
            writer.Write(cz);
            writer.WriteBytes(ChunkNetCodec.EncodeColumn(terrain));
            client.Send(writer.ToArray());
    }

    internal void OnLodRegionRequest(ClientConnection client, PacketReader reader)
    {
            var level = reader.ReadInt();
            var rx = reader.ReadInt();
            var rz = reader.ReadInt();
            var nonce = reader.ReadInt();
            if (level < 0 || level >= LodRegion.Levels.Length || nonce <= 0)
                return;
            var key = (level, rx, rz);
            if (client.LodFailedKey == key && _state.Elapsed < client.LodFailedUntil)
                return;
            if (client.LodLedger.Nonces.TryGetValue(key, out var oldNonce))
            {
                if (oldNonce == nonce) return;
                RemoveQueued(client, key, oldNonce);
            }
            if (!client.LodLedger.Request(key, nonce, NetworkConfig.NetLodInFlightMax)) return;
            // Queued, not built here: a joining client asks for hundreds of regions, and
            // ServeLodRequests builds a bounded number per Pump. A region already queued for
            // this client is not queued twice.
            Enqueue(client, level, rx, rz, nonce);
    }

    internal void OnLodAck(ClientConnection client, PacketReader reader)
    {
        var nonce = reader.ReadInt();
        var key = (reader.ReadInt(), reader.ReadInt(), reader.ReadInt());
        var generation = reader.ReadInt();
        var status = reader.ReadByte();
        if (status is not (LodTransfer.Applied or LodTransfer.Rejected or LodTransfer.Canceled))
            return;
        if (status == LodTransfer.Canceled && client.LodLedger.CancelQueued(key, nonce))
        {
            RemoveQueued(client, key, nonce);
            return;
        }
        if (!client.LodLedger.Acknowledge(key, nonce, generation, status))
            return;
        if (_active is { } active && ReferenceEquals(active.Client, client) &&
            active.Header.Nonce == nonce)
            _active = null;
    }

    private void RemoveQueued(ClientConnection client, (int Level, int Rx, int Rz) key, int nonce)
    {
        var lookup = (client, key.Level, key.Rx, key.Rz);
        if (_queuedNodes.TryGetValue(lookup, out var node) && node.Value.Nonce == nonce)
        { _lodQueue.Remove(node); _queuedNodes.Remove(lookup); }
    }

    private void Enqueue(ClientConnection client, int level, int rx, int rz, int nonce)
    {
        var lookup = (client, level, rx, rz);
        _queuedNodes[lookup] = _lodQueue.AddLast((client, level, rx, rz, nonce));
    }

    internal void RemoveClient(ClientConnection client)
    {
        foreach (var key in client.LodLedger.Queued)
            RemoveQueued(client, key, client.LodLedger.Nonces.GetValueOrDefault(key));
        client.LodLedger.Clear();
        if (_active is { } active && ReferenceEquals(active.Client, client)) _active = null;
    }

    /// <summary>
    /// Builds and sends at most <see cref="NetworkConfig.NetLodBuildsPerPump"/> queued
    /// far-LOD regions, oldest first; an entry whose client has gone since is dropped unbuilt.
    /// </summary>
    internal void ServeLodRequests()
    {
        _invalidations.Drain((key, generation) =>
        {
            foreach (var client in _state.Clients.Values)
            {
                if (client.Removed) continue;
                using var invalidation = new PacketWriter(NetMessage.LodRegionInvalidate);
                invalidation.Write(key.Level).Write(key.X).Write(key.Z).Write(generation);
                client.Send(invalidation.ToArray());
            }
        });
        // A writer may edit a region between its build and its last page. The client
        // receives the invalidation first and the failed completion never publishes it.
        if (_active is { Stale: true } stale)
        {
            SendComplete(stale.Client, stale.Header, LodTransfer.Rejected);
            stale.Client.LodLedger.ReleaseRaw();
            _active = null;
        }
        foreach (var client in _state.Clients.Values)
            if (client.LodLedger.Expire(_state.Elapsed, NetworkConfig.NetLodResendSeconds))
            {
                if (_active is { } timed && ReferenceEquals(timed.Client, client))
                    _active = null;
            }
        if (_active is { } abandoned &&
            (abandoned.Client.Removed || !_state.Clients.ContainsKey(abandoned.Client.Id)))
            _active = null;
        if (_active == null)
        {
            // Rotate at most 64 small queue entries per Pump. A stalled client's missing ACK
            // cannot hold the one server-wide 12 MiB serialization slot against other clients.
            var scan = Math.Min(_lodQueue.Count, MaxQueueScanPerPump);
            for (var i = 0; i < scan; i++)
            {
                var node = _lodQueue.First!;
                var request = node.Value;
                _lodQueue.RemoveFirst();
                _queuedNodes.Remove((request.Client, request.Level, request.Rx, request.Rz));
                var client = request.Client;
                var key = (request.Level, request.Rx, request.Rz);
                if (client.Removed || !_state.Clients.ContainsKey(client.Id))
                    continue;
                if (!client.LodLedger.Queued.Contains(key) ||
                    !client.LodLedger.Nonces.TryGetValue(key, out var nonce) ||
                    nonce != request.Nonce)
                    continue;
                if (!client.LodLedger.Available)
                {
                    Enqueue(client, request.Level, request.Rx, request.Rz, nonce);
                    continue;
                }
                // One build while no other logical region owns the serialization slot. The
                // existing NetLodBuildsPerPump upper bound is therefore respected.
                byte[] bytes;
                LodTransfer.Begin begin;
                try
                {
                    var data = _meshProducer is null
                        ? LodRegion.BuildMeshData(request.Level, request.Rx, request.Rz,
                            source: _state.World.GetChunkManager().WorldSource)
                        : _meshProducer(request.Level, request.Rx, request.Rz);
                    bytes = LodNetCodec.Encode(data);
                    begin = LodTransfer.MakeBegin(nonce,
                        new LodTransfer.Key(request.Level, request.Rx, request.Rz), bytes,
                        _invalidations.Generation);
                }
                catch (Exception e) when (e is InvalidDataException or OverflowException or
                                           ArgumentException or InvalidOperationException)
                {
                    Log.Warn($"net: rejected LOD region {key}: {e.Message}");
                    client.LodFailedKey = key;
                    client.LodFailedUntil = _state.Elapsed + NetworkConfig.NetLodResendSeconds;
                    SendComplete(client,
                        new LodTransfer.Begin(nonce,
                            new LodTransfer.Key(request.Level, request.Rx, request.Rz),
                            _invalidations.Generation, 0, 0, []), LodTransfer.Rejected);
                    client.LodLedger.CancelQueued(key, nonce);
                    continue;
                }
                if (!client.LodLedger.Begin(key, nonce, begin.Generation, _state.Elapsed))
                    throw new InvalidOperationException("LOD credit changed during a main-thread build.");
                _active = new Outbound(client, begin, bytes);
                break;
            }
        }
        if (_active is not { } transfer) return;
        if (!transfer.Began)
        {
            using var begin = new PacketWriter(NetMessage.LodRegionBegin);
            LodTransfer.WriteBegin(begin, transfer.Header);
            transfer.Client.Send(begin.ToArray());
            transfer.Began = true;
            transfer.Client.LodLedger.Progress(_state.Elapsed);
        }
        var budget = MaxPagesPerPump;
        while (budget-- > 0 && transfer.NextPage < transfer.Header.Pages)
        {
            using var page = new PacketWriter(NetMessage.LodRegionPage);
            LodTransfer.WritePage(page, transfer.Header.Nonce, transfer.NextPage,
                transfer.Bytes, transfer.Header.Generation);
            transfer.Client.Send(page.ToArray());
            transfer.NextPage++;
            transfer.Client.LodLedger.Progress(_state.Elapsed);
        }
        if (transfer.NextPage != transfer.Header.Pages) return;
        SendComplete(transfer.Client, transfer.Header, LodTransfer.Applied);
        transfer.Client.LodLedger.ReleaseRaw();
        _active = null; // raw logical bytes released; client credit still awaits its ACK
    }

    private static void SendComplete(ClientConnection client, LodTransfer.Begin header, byte status)
    {
        using var complete = new PacketWriter(NetMessage.LodRegionComplete);
        complete.Write(header.Nonce).Write(header.Region.Level)
            .Write(header.Region.X).Write(header.Region.Z)
            .Write(header.Generation).Write(status);
        client.Send(complete.ToArray());
    }

    internal void ClearLodQueue()
    {
        _active = null;
        _lodQueue.Clear();
        _queuedNodes.Clear();
        _invalidations.Clear();
    }

    private static void SendChunk(ClientConnection client, int cx, int cy, int cz, ChunkSnapshot snapshot)
    {
        using var writer = new PacketWriter(NetMessage.ChunkData);
        writer.Write(cx);
        writer.Write(cy);
        writer.Write(cz);
        writer.WriteBytes(ChunkNetCodec.Encode(snapshot));
        client.Send(writer.ToArray());
    }
}
