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

    private Thread? _acceptThread;
    private volatile bool _running;
    private int _nextId;
    private double _listTimer;
    private double _timeTimer;

    public GameServer(World world, string worldName, int seed, int port)
    {
        _world = world;
        _worldName = worldName;
        _seed = seed;
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public int Port { get; }

    /// <summary>The port actually bound (resolves an ephemeral port 0), for tests and logs.</summary>
    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int ClientCount => _clients.Count;

    public void Start()
    {
        _listener.Start();
        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "net-accept" };
        _acceptThread.Start();
    }

    /// <summary>Main thread: applies every queued client message and runs the periodic broadcasts.</summary>
    public void Pump(double deltaTime)
    {
        while (_inbound.TryDequeue(out var item))
        {
            if (item.Payload == null)
                RemoveClient(item.Client);
            else
                Handle(item.Client, item.Payload);
        }

        if (_players.Count > 0)
            _world.SetServerFocus(Centroid());

        _listTimer += deltaTime;
        _timeTimer += deltaTime;
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
                return;
            }

            client.Name = CleanName(reader.ReadString());
            client.Hello = true;
            _clients[client.Id] = client;
            _players[client.Id] = new PlayerSnapshot(client.Name, _world.Camera.Position, 0f, 0f, 0);
            SendServerHello(client);
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
                    _players[client.Id] = current with { Position = position, Yaw = yaw, Pitch = pitch, Flags = flags };
                break;
            }

            case NetMessage.BlockEdit:
            {
                var x = reader.ReadInt();
                var y = reader.ReadInt();
                var z = reader.ReadInt();
                var blockId = reader.ReadByte();
                var state = reader.ReadByte();
                _world.ApplyNetworkBlockEdit(x, y, z, blockId, state);
                BroadcastBlockChange(x, y, z, blockId, state);
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
                writer.WriteBytes(ChunkNetCodec.EncodeHeights(Chunk.GetColumnHeights(cx, cz)));
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
                var data = LodRegion.BuildMeshData(level, rx, rz);
                using var writer = new PacketWriter(NetMessage.LodRegionData);
                writer.Write(level);
                writer.Write(rx);
                writer.Write(rz);
                writer.WriteBytes(LodNetCodec.Encode(data));
                client.Send(writer.ToArray());
                break;
            }

            default:
                Log.Warn($"net: ignoring {message} from {client.Name} (#{client.Id})");
                break;
        }
    }

    private void SendServerHello(ClientConnection client)
    {
        using var writer = new PacketWriter(NetMessage.ServerHello);
        writer.Write(NetFraming.ProtocolVersion);
        writer.Write(client.Id);
        writer.Write(_worldName);
        writer.Write(_seed);
        writer.Write(_world.LightingTime);
        writer.Write(_world.Camera.Position);
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

    private void BroadcastChat(ChatKind kind, string sender, string text)
    {
        using var writer = new PacketWriter(NetMessage.ChatMessage);
        writer.Write((byte)kind);
        writer.Write(sender);
        writer.Write(text);
        Broadcast(writer.ToArray());
    }

    private void BroadcastSystem(string text) => BroadcastChat(ChatKind.System, "", text);

    private void BroadcastBlockChange(int x, int y, int z, byte blockId, byte state)
    {
        using var writer = new PacketWriter(NetMessage.BlockChange);
        writer.Write(x);
        writer.Write(y);
        writer.Write(z);
        writer.Write(blockId);
        writer.Write(state);
        Broadcast(writer.ToArray());
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
            catch (ObjectDisposedException)
            {
                break;
            }

            tcp.NoDelay = true;
            var client = new ClientConnection(tcp, ++_nextId);
            client.StartReader(() => ReadLoop(client), _inbound);
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

        try { _listener.Stop(); } catch (SocketException) { }

        List<ClientConnection> clients;
        lock (_clients)
            clients = [.. _clients.Values];
        foreach (var client in clients)
            client.Close();

        _acceptThread?.Join(WorldGenerationConfig.WorkerStopJoinTimeoutMs);
        _acceptThread = null;
        _clients.Clear();
        _players.Clear();
    }

    public void Dispose() => Stop();

    private readonly record struct Inbound(ClientConnection Client, byte[]? Payload);

    private readonly record struct PlayerSnapshot(string Name, Vector3 Position, float Yaw, float Pitch, byte Flags);

    /// <summary>One connected socket: its id and name, and a lock-guarded framed send.</summary>
    private sealed class ClientConnection(TcpClient tcp, int id)
    {
        private readonly object _writeGate = new();
        private Thread? _reader;

        public int Id { get; } = id;
        public TcpClient Tcp { get; } = tcp;
        public NetworkStream Stream { get; } = tcp.GetStream();
        public string Name { get; set; } = "?";
        public bool Hello { get; set; }
        public string Remote { get; } = tcp.Client.RemoteEndPoint?.ToString() ?? "?";

        public void StartReader(Action readLoop, ConcurrentQueue<Inbound> queue)
        {
            _reader = new Thread(() => readLoop()) { IsBackground = true, Name = $"net-read-{Id}" };
            _reader.Start();
        }

        public void Send(byte[] payload)
        {
            lock (_writeGate)
            {
                try
                {
                    NetFraming.WriteFrame(Stream, payload);
                }
                catch (Exception e) when (e is System.IO.IOException or SocketException or ObjectDisposedException or InvalidOperationException)
                {
                }
            }
        }

        public void Close()
        {
            try { Tcp.Close(); } catch (SocketException) { }
        }
    }
}
