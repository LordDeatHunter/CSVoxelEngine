using System;
using System.Collections.Generic;
using System.IO;
using VoxelEngine.Lod;
using VoxelEngine.Saves;

namespace VoxelEngine.Networking.ServerSide;

/// <summary>Answers a client's streaming requests from the loaded world: chunks, column terrain and far-LOD regions, the regions queued and built a bounded number per Pump. Main thread only.</summary>
internal sealed class StreamRequests
{
    private readonly ServerState _state;

    // Far-LOD region requests waiting to be built, served NetLodBuildsPerPump per Pump. Each
    // connection's own set dedupes a region it re-asked for while still queued.
    private readonly Queue<(ClientConnection Client, int Level, int Rx, int Rz)> _lodQueue = new();
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
    }

    internal StreamRequests(ServerState state)
    {
        _state = state;
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
            if (client.LodAwaitingAck == key) return;
            if (!client.LodQueued.Contains(key) && client.LodQueued.Count >= NetworkConfig.NetLodInFlightMax)
                return;
            client.LodRequestNonces[key] = nonce;
            // Queued, not built here: a joining client asks for hundreds of regions, and
            // ServeLodRequests builds a bounded number per Pump. A region already queued for
            // this client is not queued twice.
            if (client.LodQueued.Add(key))
                _lodQueue.Enqueue((client, level, rx, rz));
    }

    internal void OnLodAck(ClientConnection client, PacketReader reader)
    {
        var nonce = reader.ReadInt();
        var key = (reader.ReadInt(), reader.ReadInt(), reader.ReadInt());
        var status = reader.ReadByte();
        if (status is not (LodTransfer.Applied or LodTransfer.Rejected or LodTransfer.Canceled) ||
            client.LodActiveNonce != nonce || client.LodAwaitingAck != key)
            return;
        if (_active is { } active && ReferenceEquals(active.Client, client) &&
            active.Header.Nonce == nonce)
            _active = null;
        client.LodActiveNonce = 0;
        client.LodAwaitingAck = null;
        client.LodCredit = true;
        client.LodQueued.Remove(key);
        client.LodRequestNonces.Remove(key);
    }

    /// <summary>
    /// Builds and sends at most <see cref="NetworkConfig.NetLodBuildsPerPump"/> queued
    /// far-LOD regions, oldest first; an entry whose client has gone since is dropped unbuilt.
    /// </summary>
    internal void ServeLodRequests()
    {
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
                var request = _lodQueue.Dequeue();
                var client = request.Client;
                var key = (request.Level, request.Rx, request.Rz);
                if (client.Removed || !_state.Clients.ContainsKey(client.Id))
                    continue;
                if (!client.LodQueued.Contains(key) || !client.LodRequestNonces.TryGetValue(key, out var nonce))
                    continue;
                if (!client.LodCredit)
                {
                    _lodQueue.Enqueue(request);
                    continue;
                }
                // One build while no other logical region owns the serialization slot. The
                // existing NetLodBuildsPerPump upper bound is therefore respected.
                var data = LodRegion.BuildMeshData(request.Level, request.Rx, request.Rz,
                    source: _state.World.GetChunkManager().WorldSource);
                byte[] bytes;
                try { bytes = LodNetCodec.Encode(data); }
                catch (Exception e) when (e is InvalidDataException or OverflowException)
                {
                    using var error = new PacketWriter(NetMessage.LodRegionComplete);
                    error.Write(nonce).Write(request.Level).Write(request.Rx).Write(request.Rz)
                        .Write(LodTransfer.Rejected);
                    client.Send(error.ToArray());
                    client.LodQueued.Remove(key);
                    client.LodRequestNonces.Remove(key);
                    continue;
                }
                var begin = LodTransfer.MakeBegin(nonce,
                    new LodTransfer.Key(request.Level, request.Rx, request.Rz), bytes);
                client.LodCredit = false;
                client.LodActiveNonce = nonce;
                client.LodAwaitingAck = key;
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
        }
        var budget = MaxPagesPerPump;
        while (budget-- > 0 && transfer.NextPage < transfer.Header.Pages)
        {
            using var page = new PacketWriter(NetMessage.LodRegionPage);
            LodTransfer.WritePage(page, transfer.Header.Nonce, transfer.NextPage, transfer.Bytes);
            transfer.Client.Send(page.ToArray());
            transfer.NextPage++;
        }
        if (transfer.NextPage != transfer.Header.Pages) return;
        using (var complete = new PacketWriter(NetMessage.LodRegionComplete))
        {
            complete.Write(transfer.Header.Nonce).Write(transfer.Header.Region.Level)
                .Write(transfer.Header.Region.X).Write(transfer.Header.Region.Z)
                .Write(LodTransfer.Applied);
            transfer.Client.Send(complete.ToArray());
        }
        _active = null; // raw logical bytes released; client credit still awaits its ACK
    }

    internal void ClearLodQueue()
    {
        _active = null;
        _lodQueue.Clear();
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
