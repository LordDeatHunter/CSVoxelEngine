using System.Collections.Generic;
using VoxelEngine.Lod;
using VoxelEngine.Networking;
using VoxelEngine.Saves;

namespace VoxelEngine.Server;

/// <summary>Answers a client's streaming requests from the loaded world: chunks, column terrain and far-LOD regions, the regions queued and built a bounded number per Pump. Main thread only.</summary>
internal sealed class StreamRequests
{
    private readonly ServerState _state;

    // Far-LOD region requests waiting to be built, served NetLodBuildsPerPump per Pump. Each
    // connection's own set dedupes a region it re-asked for while still queued.
    private readonly Queue<(ClientConnection Client, int Level, int Rx, int Rz)> _lodQueue = new();

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
            if (level < 0 || level >= LodRegion.Levels.Length)
                return;
            // Queued, not built here: a joining client asks for hundreds of regions, and
            // ServeLodRequests builds a bounded number per Pump. A region already queued for
            // this client is not queued twice.
            if (client.LodQueued.Add((level, rx, rz)))
                _lodQueue.Enqueue((client, level, rx, rz));
    }

    /// <summary>
    /// Builds and sends at most <see cref="NetworkConfig.NetLodBuildsPerPump"/> queued
    /// far-LOD regions, oldest first; an entry whose client has gone since is dropped unbuilt.
    /// </summary>
    internal void ServeLodRequests()
    {
        var built = 0;
        while (built < NetworkConfig.NetLodBuildsPerPump && _lodQueue.TryDequeue(out var request))
        {
            var client = request.Client;
            client.LodQueued.Remove((request.Level, request.Rx, request.Rz));
            if (client.Removed || !_state.Clients.ContainsKey(client.Id))
                continue;

            // From the world's own source, so an imported map's far terrain is what clients see.
            var data = LodRegion.BuildMeshData(request.Level, request.Rx, request.Rz,
                source: _state.World.GetChunkManager().WorldSource);
            using var writer = new PacketWriter(NetMessage.LodRegionData);
            writer.Write(request.Level);
            writer.Write(request.Rx);
            writer.Write(request.Rz);
            writer.WriteBytes(LodNetCodec.Encode(data));
            client.Send(writer.ToArray());
            built++;
        }
    }

    internal void ClearLodQueue() => _lodQueue.Clear();

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
