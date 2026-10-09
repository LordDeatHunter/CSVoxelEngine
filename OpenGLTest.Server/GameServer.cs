using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// The multiplayer server: a TCP listener, one reader thread per connection, and a main-thread
/// <see cref="Pump"/> that owns every piece of <see cref="World"/> state. Reader threads never
/// touch the world - they only frame inbound bytes onto a concurrent queue, so the game simulation
/// stays single-threaded exactly like the client. Outbound sends are lock-guarded per connection.
/// <para>
/// The server is authoritative: clients send their input state and edits, the server applies them
/// to its headless <see cref="World"/> and broadcasts the result (player list, chat, block
/// changes, the day clock).
/// </para>
/// </summary>
public sealed class GameServer : IDisposable
{
    private readonly World _world;
    private readonly string _worldName;
    private readonly int _seed;
    private readonly TcpListener _listener;

    // Reader threads hand inbound frames here; only Pump (main thread) drains it.
    private readonly ConcurrentQueue<Inbound> _inbound = new();
    private readonly Dictionary<int, ClientConnection> _clients = new();
    private readonly Dictionary<int, PlayerSnapshot> _players = new();

    // Connections the accept thread opened, handed to Pump, which keeps the ones that have not
    // said hello yet in _pending so the idle sweep can drop a peer that never does.
    private readonly ConcurrentQueue<ClientConnection> _accepted = new();
    private readonly List<ClientConnection> _pending = [];
    private readonly List<ClientConnection> _idleScratch = [];
    private double _sweepTimer;

    // Far-LOD region requests waiting to be built, served NetLodBuildsPerPump per Pump. Each
    // connection's own set dedupes a region it re-asked for while still queued.
    private readonly Queue<(ClientConnection Client, int Level, int Rx, int Rz)> _lodQueue = new();

    // Where joiners spawn: the server camera's position at the first hello, before any player
    // moved the chunk focus (SetServerFocus only runs with players present). Later joiners must
    // not inherit the centroid of whoever is online, which can sit inside rock.
    private Vector3? _joinSpawn;

    private Thread? _acceptThread;
    // Long-lived, coalesced cell changes the server's own simulation produced (explosions, fluid
    // flow, decay, felling), drained and broadcast in batches.
    private readonly ConcurrentQueue<CellChange> _cellJournal = new();
    private readonly Dictionary<(int X, int Y, int Z), (byte Id, byte State)> _cellCoalesce = new();
    private const int MaxCellChangesPerBroadcast = 8192;

    private readonly record struct CellChange(int X, int Y, int Z, byte Id, byte State);

    private volatile bool _running;
    private int _nextId;
    private double _listTimer;
    private double _timeTimer;
    private double _entityTimer;
    private double _elapsed;

    // Stable network ids for the live mobs and drops, keyed by object identity (reference equality).
    private readonly Dictionary<object, int> _entityIds = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _entitySeen = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> _entityStale = [];
    private int _nextEntityId = 1;

    public GameServer(World world, string worldName, int seed, int port)
    {
        _world = world;
        _worldName = worldName;
        _seed = seed;
        Port = port;
        // Dual-mode (IPv6 any, with IPv4 mapped), so [::1]:port works like 127.0.0.1:port.
        _listener = TcpListener.Create(port);
    }

    public int Port { get; }

    /// <summary>The port actually bound (resolves an ephemeral port 0), for tests and logs.</summary>
    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int ClientCount => _clients.Count;

    public void Start()
    {
        _listener.Start();
        _running = true;
        // Every raw cell write on the server (an edit or a simulation step) is journaled and sent
        // to clients, so explosions, fluid flow, decay and felling all reach them.
        Chunk.CellWritten = OnCellWritten;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "net-accept" };
        _acceptThread.Start();
    }

    /// <summary>Main thread: applies every queued client message and runs the periodic broadcasts.</summary>
    public void Pump(double deltaTime)
    {
        _elapsed += deltaTime;

        // Before the inbound frames: a connection is always queued here before its reader starts,
        // so its hello can never be handled before it is known. One that already said hello (its
        // hello landed between an earlier drain and this one) or was already removed is skipped.
        while (_accepted.TryDequeue(out var accepted))
        {
            if (!accepted.Hello && !accepted.Removed)
                _pending.Add(accepted);
        }

        while (_inbound.TryDequeue(out var item))
        {
            if (item.Payload == null)
            {
                RemoveClient(item.Client);
                continue;
            }

            if (item.Client.Removed)
                continue;

            // A short or malformed frame from one client must never take the server down: drop
            // that client and keep serving everyone else.
            try
            {
                Handle(item.Client, item.Payload);
            }
            catch (Exception e) when (e is System.IO.EndOfStreamException or System.IO.IOException
                                          or System.IO.InvalidDataException or FormatException
                                          or ArgumentException or ObjectDisposedException)
            {
                Log.Warn($"net: dropping {item.Client.Name} (#{item.Client.Id}) after a bad message: {e.Message}");
                item.Client.Close();
                RemoveClient(item.Client);
            }
        }

        ServeLodRequests();
        DrainCellJournal();

        _sweepTimer += deltaTime;
        if (_sweepTimer >= 1.0)
        {
            _sweepTimer = 0;
            SweepIdleConnections();
        }

        if (_players.Count > 0)
            _world.SetServerFocus(Centroid());

        _listTimer += deltaTime;
        _timeTimer += deltaTime;
        _entityTimer += deltaTime;
        if (_listTimer >= WorldGenerationConfig.NetPlayerListSeconds)
        {
            _listTimer = 0;
            BroadcastPlayerList();
        }
        if (_timeTimer >= WorldGenerationConfig.NetTimeSyncSeconds)
        {
            _timeTimer = 0;
            BroadcastTime();
        }
        if (_entityTimer >= WorldGenerationConfig.NetEntitySyncSeconds)
        {
            _entityTimer = 0;
            BroadcastEntities();
        }
    }

    private Vector3 Centroid()
    {
        var sum = Vector3.Zero;
        foreach (var player in _players.Values)
            sum += player.Position;
        return sum / _players.Count;
    }

    private void Handle(ClientConnection client, byte[] payload)
    {
        using var reader = new PacketReader(payload);
        var message = reader.Message;

        if (!client.Hello)
        {
            if (message != NetMessage.ClientHello)
            {
                client.Close();
                RemoveClient(client);
                return;
            }

            var version = reader.ReadInt();
            if (version != NetFraming.ProtocolVersion)
            {
                Log.Warn($"net: client on protocol {version}, server on {NetFraming.ProtocolVersion}");
                client.Close();
                RemoveClient(client);
                return;
            }

            client.Name = CleanName(reader.ReadString());
            client.Hello = true;
            _pending.Remove(client);
            // Captured before this player is added: with nobody online the server camera still
            // stands at the world spawn, so every joiner starts there, never at the centroid.
            var spawn = _joinSpawn ??= _world.Camera.Position;
            _clients[client.Id] = client;
            _players[client.Id] = new PlayerSnapshot(client.Name, spawn, 0f, 0f, 0, _elapsed);
            SendServerHello(client, spawn);
            SendInventory(client);
            BroadcastPlayerList();
            BroadcastSystem($"{client.Name} joined the game");
            Log.WriteLine($"net: {client.Name} (#{client.Id}) joined from {client.Remote}");
            return;
        }

        switch (message)
        {
            case NetMessage.KeepAlive:
                break;

            case NetMessage.ChatSend:
            {
                var text = reader.ReadString();
                if (text.Length > ChatView.MaxInputLength)
                    text = text[..ChatView.MaxInputLength];
                if (!string.IsNullOrWhiteSpace(text))
                {
                    BroadcastChat(ChatKind.Player, client.Name, text);
                    Log.WriteLine($"chat: <{client.Name}> {text}");
                }
                break;
            }

            case NetMessage.PlayerState:
            {
                var position = reader.ReadVector3();
                var yaw = reader.ReadFloat();
                var pitch = reader.ReadFloat();
                var flags = reader.ReadByte();
                if (_players.TryGetValue(client.Id, out var current))
                {
                    // A teleport-scale move is clamped: the authoritative position advances toward
                    // it at the speed cap (NetValidation.AcceptMove), so a burst of fast states
                    // converges instead of freezing the player at a stale point. The timestamp
                    // always advances, so rejected states cannot accumulate time until a teleport
                    // fits.
                    var delta = _elapsed - current.LastStateAt;
                    var ok = NetValidation.AcceptMove(current.Position, position, delta,
                        WorldGenerationConfig.NetMaxMoveBlocksPerSecond, out var bounded);
                    if (!ok)
                        Log.Warn($"net: rejected a move from {client.Name} (#{client.Id})");
                    _players[client.Id] = current with
                    {
                        Position = bounded,
                        Yaw = yaw,
                        Pitch = pitch,
                        Flags = flags,
                        LastStateAt = _elapsed,
                    };
                }
                break;
            }

            case NetMessage.BlockEdit:
            {
                var x = reader.ReadInt();
                var y = reader.ReadInt();
                var z = reader.ReadInt();
                var blockId = reader.ReadByte();
                var state = reader.ReadByte();
                // Out of reach, or an edit the world did not apply (unloaded chunk, occupied cell):
                // send the authoritative cell, so the client never keeps an optimistic edit the
                // server does not have.
                var applied = _players.TryGetValue(client.Id, out var player)
                    && NetValidation.InEditReach(player.Position, x, y, z, WorldGenerationConfig.NetMaxEditReachBlocks)
                    && _world.ApplyNetworkBlockEdit(x, y, z, blockId, state);
                if (!applied)
                    SendBlockCorrection(client, x, y, z);
                break;
            }

            case NetMessage.ChunkRequest:
            {
                var cx = reader.ReadInt();
                var cy = reader.ReadInt();
                var cz = reader.ReadInt();
                // Only loaded chunks are sent: a request never forces generation, so it cannot
                // make the server build chunks it would not already have loaded around the players.
                if (_world.GetChunkManager().TryCaptureChunk(cx, cy, cz, out var snapshot))
                    SendChunk(client, cx, cy, cz, snapshot);
                break;
            }

            case NetMessage.ColumnRequest:
            {
                var cx = reader.ReadInt();
                var cz = reader.ReadInt();
                using var writer = new PacketWriter(NetMessage.ColumnHeights);
                writer.Write(cx);
                writer.Write(cz);
                writer.WriteBytes(ChunkNetCodec.EncodeHeights(ColumnCache.GetHeights(cx, cz)));
                client.Send(writer.ToArray());
                break;
            }

            case NetMessage.LodRegionRequest:
            {
                var level = reader.ReadInt();
                var rx = reader.ReadInt();
                var rz = reader.ReadInt();
                if (level < 0 || level >= LodRegion.Levels.Length)
                    break;
                // Queued, not built here: a joining client asks for hundreds of regions, and
                // ServeLodRequests builds a bounded number per Pump. A region already queued for
                // this client is not queued twice.
                if (client.LodQueued.Add((level, rx, rz)))
                    _lodQueue.Enqueue((client, level, rx, rz));
                break;
            }

            case NetMessage.InventoryRequest:
            {
                var action = (NetInventoryAction)reader.ReadByte();
                var arg = reader.ReadInt();
                // A Q-drop leaves from the requesting player's eye along their look, never from
                // the server's own camera (the centroid of everyone online).
                var hasThrower = _players.TryGetValue(client.Id, out var thrower);
                switch (action)
                {
                    case NetInventoryAction.SelectSlot:
                        _world.Inventory.Select(arg);
                        break;
                    case NetInventoryAction.DropSelected when hasThrower:
                        _world.ThrowSelected(false, thrower.Position,
                            Vector3Utils.FromYawPitch(thrower.Yaw, thrower.Pitch));
                        break;
                    case NetInventoryAction.DropAllSelected when hasThrower:
                        _world.ThrowSelected(true, thrower.Position,
                            Vector3Utils.FromYawPitch(thrower.Yaw, thrower.Pitch));
                        break;
                }

                SendInventory(client);
                break;
            }

            default:
                Log.Warn($"net: ignoring {message} from {client.Name} (#{client.Id})");
                break;
        }
    }

    private void SendServerHello(ClientConnection client, Vector3 spawn)
    {
        using var writer = new PacketWriter(NetMessage.ServerHello);
        writer.Write(NetFraming.ProtocolVersion);
        writer.Write(client.Id);
        writer.Write(_worldName);
        writer.Write(_seed);
        writer.Write(_world.LightingTime);
        writer.Write(spawn);
        client.Send(writer.ToArray());
    }

    /// <summary>
    /// Builds and sends at most <see cref="WorldGenerationConfig.NetLodBuildsPerPump"/> queued
    /// far-LOD regions, oldest first; an entry whose client has gone since is dropped unbuilt.
    /// </summary>
    private void ServeLodRequests()
    {
        var built = 0;
        while (built < WorldGenerationConfig.NetLodBuildsPerPump && _lodQueue.TryDequeue(out var request))
        {
            var client = request.Client;
            client.LodQueued.Remove((request.Level, request.Rx, request.Rz));
            if (client.Removed || !_clients.ContainsKey(client.Id))
                continue;

            var data = LodRegion.BuildMeshData(request.Level, request.Rx, request.Rz);
            using var writer = new PacketWriter(NetMessage.LodRegionData);
            writer.Write(request.Level);
            writer.Write(request.Rx);
            writer.Write(request.Rz);
            writer.WriteBytes(LodNetCodec.Encode(data));
            client.Send(writer.ToArray());
            built++;
        }
    }

    /// <summary>
    /// Drops every connection, hello'd or still pending, that sent nothing for
    /// <see cref="WorldGenerationConfig.NetIdleTimeoutSeconds"/>: a peer that vanished without a
    /// FIN, or one that connected and never said hello. Runs about once a second.
    /// </summary>
    private void SweepIdleConnections()
    {
        var now = Environment.TickCount64;
        var limitMs = (long)(WorldGenerationConfig.NetIdleTimeoutSeconds * 1000.0);
        _idleScratch.Clear();
        foreach (var client in _clients.Values)
            if (now - client.LastInboundAt > limitMs)
                _idleScratch.Add(client);
        foreach (var client in _pending)
            if (now - client.LastInboundAt > limitMs)
                _idleScratch.Add(client);

        foreach (var client in _idleScratch)
        {
            Log.Warn($"net: dropping {client.Name} (#{client.Id}) from {client.Remote}: idle for {WorldGenerationConfig.NetIdleTimeoutSeconds:0} s");
            client.Close();
            RemoveClient(client);
        }
        _idleScratch.Clear();
    }

    private void SendInventory(ClientConnection client)
    {
        using var writer = new PacketWriter(NetMessage.InventoryState);
        writer.WriteBytes(InventoryNetCodec.Encode(_world.Inventory.Export()));
        client.Send(writer.ToArray());
    }

    private void BroadcastPlayerList()
    {
        using var writer = new PacketWriter(NetMessage.PlayerList);
        writer.Write(_players.Count);
        foreach (var (id, player) in _players)
        {
            writer.Write(id);
            writer.Write(player.Name);
            writer.Write(player.Position);
            writer.Write(player.Yaw);
            writer.Write(player.Pitch);
            writer.Write(player.Flags);
        }
        Broadcast(writer.ToArray());
    }

    private void BroadcastTime()
    {
        using var writer = new PacketWriter(NetMessage.TimeSync);
        writer.Write(_world.LightingTime);
        Broadcast(writer.ToArray());
    }

    private void BroadcastEntities()
    {
        var mobs = _world.Mobs;
        var drops = _world.Drops;
        _entitySeen.Clear();

        using var writer = new PacketWriter(NetMessage.EntityList);
        writer.Write(mobs.Count + drops.Count);
        for (var i = 0; i < mobs.Count; i++)
        {
            var mob = mobs[i];
            var kind = mob is Slime ? NetEntityKind.Slime : NetEntityKind.Goblin;
            var ammo = mob is Goblin goblin ? goblin.Ammo : 0;
            writer.Write((byte)kind);
            writer.Write(IdFor(mob));
            writer.Write(mob.Position);
            writer.Write(mob.Facing);
            writer.Write(mob.Health);
            writer.Write(ammo);
            writer.Write((byte)0);
            writer.Write(mob.Velocity);
        }

        for (var i = 0; i < drops.Count; i++)
        {
            var drop = drops[i];
            writer.Write((byte)NetEntityKind.Drop);
            writer.Write(IdFor(drop));
            writer.Write(drop.Position);
            writer.Write(0f);
            writer.Write(0);
            writer.Write(0);
            writer.Write(drop.BlockId);
            writer.Write(drop.Velocity);
        }

        Broadcast(writer.ToArray());
        PruneEntityIds();
    }

    private int IdFor(object entity)
    {
        _entitySeen.Add(entity);
        if (_entityIds.TryGetValue(entity, out var id))
            return id;
        id = _nextEntityId++;
        _entityIds[entity] = id;
        return id;
    }

    private void PruneEntityIds()
    {
        _entityStale.Clear();
        foreach (var key in _entityIds.Keys)
            if (!_entitySeen.Contains(key))
                _entityStale.Add(key);
        foreach (var key in _entityStale)
            _entityIds.Remove(key);
    }

    private void BroadcastChat(ChatKind kind, string sender, string text)
    {
        using var writer = new PacketWriter(NetMessage.ChatMessage);
        writer.Write((byte)kind);
        writer.Write(sender);
        writer.Write(text);
        Broadcast(writer.ToArray());
    }

    private void BroadcastSystem(string text) => BroadcastChat(ChatKind.System, "", text);

    private void OnCellWritten(int x, int y, int z, byte id, byte state) =>
        _cellJournal.Enqueue(new CellChange(x, y, z, id, state));

    // Reverts a client's optimistic edit the server refused by sending the authoritative cell.
    private void SendBlockCorrection(ClientConnection client, int x, int y, int z)
    {
        var manager = _world.GetChunkManager();
        using var writer = new PacketWriter(NetMessage.BlockChange);
        writer.Write(x);
        writer.Write(y);
        writer.Write(z);
        writer.Write(manager.GetBlockGlobal(x, y, z));
        writer.Write(manager.GetStateGlobal(x, y, z));
        client.Send(writer.ToArray());
    }

    // Coalesces this frame's journaled writes by cell (the last write wins) and sends one
    // BlockChanges batch; a burst past the cap leaves the rest for the next frame.
    private void DrainCellJournal()
    {
        if (_cellJournal.IsEmpty)
            return;

        _cellCoalesce.Clear();
        var drained = 0;
        while (drained < MaxCellChangesPerBroadcast && _cellJournal.TryDequeue(out var change))
        {
            _cellCoalesce[(change.X, change.Y, change.Z)] = (change.Id, change.State);
            drained++;
        }

        if (_cellCoalesce.Count == 0)
            return;

        using var writer = new PacketWriter(NetMessage.BlockChanges);
        writer.Write(_cellCoalesce.Count);
        foreach (var (cell, value) in _cellCoalesce)
        {
            writer.Write(cell.X);
            writer.Write(cell.Y);
            writer.Write(cell.Z);
            writer.Write(value.Id);
            writer.Write(value.State);
        }

        Broadcast(writer.ToArray());
        _cellCoalesce.Clear();
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

    private void Broadcast(byte[] payload)
    {
        foreach (var client in _clients.Values)
            client.Send(payload);
    }

    private void RemoveClient(ClientConnection client)
    {
        client.Close();
        client.Removed = true;
        _pending.Remove(client);
        if (!_clients.Remove(client.Id))
            return;
        _players.Remove(client.Id);
        BroadcastSystem($"{client.Name} left the game");
        BroadcastPlayerList();
        Log.WriteLine($"net: {client.Name} (#{client.Id}) disconnected");
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient tcp;
            try
            {
                tcp = _listener.AcceptTcpClient();
            }
            catch (SocketException) when (!_running)
            {
                break;
            }
            catch (SocketException e)
            {
                // A transient accept failure (a peer reset mid-handshake, a resource hiccup) must
                // not end the accept thread, which would leave the server deaf for the session.
                Log.Warn($"net: accept failed: {e.Message}");
                Thread.Sleep(100);
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            ClientConnection client;
            try
            {
                tcp.NoDelay = true;
                // A stalled peer must not freeze the main thread inside a blocking write: past the
                // timeout the send throws and the connection is faulted and removed.
                tcp.SendTimeout = WorldGenerationConfig.NetSendTimeoutMs;
                client = new ClientConnection(tcp, Interlocked.Increment(ref _nextId), _inbound);
                client.Stream.WriteTimeout = WorldGenerationConfig.NetSendTimeoutMs;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException
                                          or System.IO.IOException)
            {
                // The peer went away between the accept and the setup.
                try { tcp.Close(); } catch (SocketException) { }
                continue;
            }

            // Queued before the reader starts, so Pump always knows the connection before its
            // first frame (the idle sweep needs it even if no hello ever comes).
            _accepted.Enqueue(client);
            client.StartReader(() => ReadLoop(client));
        }
    }

    private void ReadLoop(ClientConnection client)
    {
        try
        {
            while (_running)
            {
                var payload = NetFraming.ReadFrame(client.Stream);
                if (payload == null)
                    break;
                client.TouchInbound();
                _inbound.Enqueue(new Inbound(client, payload));
            }
        }
        catch (Exception e) when (e is System.IO.IOException or SocketException or ObjectDisposedException)
        {
        }

        _inbound.Enqueue(new Inbound(client, null));
    }

    private static string CleanName(string? requested)
    {
        var name = requested?.Trim();
        if (string.IsNullOrEmpty(name))
            return "Player";
        return name.Length > WorldNames.MaxNameLength ? name[..WorldNames.MaxNameLength] : name;
    }

    public void Stop()
    {
        if (!_running)
            return;
        _running = false;
        Chunk.CellWritten = null;

        try { _listener.Stop(); } catch (SocketException) { }

        List<ClientConnection> clients;
        lock (_clients)
            clients = [.. _clients.Values];
        foreach (var client in clients)
            client.Close();

        _acceptThread?.Join(WorldGenerationConfig.WorkerStopJoinTimeoutMs);
        _acceptThread = null;

        // Connections that never said hello, including any the accept thread queued after the
        // last Pump, are closed too, or their reader threads would outlive the server.
        while (_accepted.TryDequeue(out var accepted))
            accepted.Close();
        foreach (var client in _pending)
            client.Close();
        _pending.Clear();
        _lodQueue.Clear();
        _clients.Clear();
        _players.Clear();
    }

    public void Dispose() => Stop();

    private readonly record struct Inbound(ClientConnection Client, byte[]? Payload);

    private readonly record struct PlayerSnapshot(string Name, Vector3 Position, float Yaw, float Pitch,
        byte Flags, double LastStateAt);

    /// <summary>
    /// One connected socket: its id and name, a lock-guarded framed send, the last time a frame
    /// arrived (written by the reader thread, read by the main thread's idle sweep) and the
    /// far-LOD regions queued for it.
    /// </summary>
    private sealed class ClientConnection(TcpClient tcp, int id, ConcurrentQueue<Inbound> inbound)
    {
        private readonly object _writeGate = new();
        private Thread? _reader;
        private long _lastInboundAt = Environment.TickCount64;
        private int _faulted;

        public int Id { get; } = id;
        public TcpClient Tcp { get; } = tcp;
        public NetworkStream Stream { get; } = tcp.GetStream();
        public string Name { get; set; } = "?";
        public bool Hello { get; set; }
        public string Remote { get; } = tcp.Client.RemoteEndPoint?.ToString() ?? "?";

        /// <summary>Set by <see cref="GameServer.RemoveClient"/>; main thread only.</summary>
        public bool Removed { get; set; }

        /// <summary>Far-LOD regions queued for this connection (main thread only).</summary>
        public HashSet<(int Level, int Rx, int Rz)> LodQueued { get; } = [];

        /// <summary><see cref="Environment.TickCount64"/> of the last inbound frame (or the accept).</summary>
        public long LastInboundAt => Volatile.Read(ref _lastInboundAt);

        /// <summary>Reader thread: records that a frame just arrived.</summary>
        public void TouchInbound() => Volatile.Write(ref _lastInboundAt, Environment.TickCount64);

        public void StartReader(Action readLoop)
        {
            _reader = new Thread(() => readLoop()) { IsBackground = true, Name = $"net-read-{Id}" };
            _reader.Start();
        }

        public void Send(byte[] payload)
        {
            if (Volatile.Read(ref _faulted) != 0)
                return;
            lock (_writeGate)
            {
                try
                {
                    NetFraming.WriteFrame(Stream, payload);
                }
                catch (Exception e) when (e is System.IO.IOException or SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    Fault();
                }
            }
        }

        // A failed (or timed-out) send leaves the stream in an unknown state: stop sending and
        // queue this connection's removal once. Only enqueues, so it is safe while Pump iterates
        // the client table.
        private void Fault()
        {
            if (Interlocked.Exchange(ref _faulted, 1) == 0)
                inbound.Enqueue(new Inbound(this, null));
        }

        public void Close()
        {
            try { Tcp.Close(); } catch (SocketException) { }
        }
    }
}
