using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using VoxelEngine.Chunks;
using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Saves;

namespace VoxelEngine.Server;

/// <summary>
/// The multiplayer server: a TCP listener, one reader thread per connection, and a main-thread
/// <see cref="Pump"/> that owns every piece of <see cref="World"/> state. Reader threads never
/// touch the world - they only frame inbound bytes onto a concurrent queue, so the game simulation
/// stays single-threaded exactly like the client. Outbound sends are lock-guarded per connection.
/// <para>
/// The server is authoritative: clients send their input state and intents, the server applies them
/// to its headless <see cref="World"/> and broadcasts the result (player list, chat, block
/// changes, station state, the day clock).
/// </para>
/// <para>
/// A client never writes a cell. Every world action it takes is a <see cref="WorldIntent"/> (mining
/// start and completion, placement, a claimed right-click, a throw, a landed attack) carrying a
/// per-player sequence id and the eye and aim it was made from. The server checks the eye against
/// the player's authoritative position, that the aim reaches and sees the named cell
/// (<see cref="World.RaycastFor"/>), the player's edit rate and, for a break, its own count of the
/// hits since the start (<see cref="BreakProgress"/>), then replays the action for that player
/// through the singleplayer rules (<see cref="ServerPlayer.Context"/>). Every intent is answered, in
/// order: the authoritative cells around its target (<see cref="NetMessage.BlockCorrection"/>, for
/// one that can edit cells), the player's inventory when the intent can change it or was refused,
/// and an <see cref="NetMessage.IntentResult"/>; consequences beyond those cells (a felled crown, a
/// blast) reach everyone through the cell journal.
/// </para>
/// <para>
/// Every connected player is a <see cref="ServerPlayer"/> with an inventory of its own. A client
/// sends inventory intents (<see cref="InventoryIntent"/>), the server replays each against that
/// player's inventory (<see cref="InventoryIntentRules"/>) and always answers with a full
/// <see cref="NetMessage.InventoryState"/> snapshot to that player only, so a refused intent
/// rubberbands the client's optimistic copy; a pickup sends one too. Players are saved per name
/// in the world folder (<see cref="ServerPlayerSaves"/>) on leaving, on stop and on the autosave
/// interval, and restored when they join again.
/// </para>
/// <para>
/// A player's status is server-owned too: its health (<see cref="ServerPlayer.Status"/>), billed by
/// falls, lava and mob hits, its death and its respawn. Each <see cref="Pump"/> ticks every
/// player's status and sends a <see cref="NetMessage.PlayerStatus"/> (modes, health, dead, the
/// queued knockback) to whoever's changed, and one at join, so the client only mirrors it. A dead
/// player stays where it died and every intent of its is refused until it sends a
/// <see cref="NetIntentKind.Respawn"/>, which is judged ahead of every other rule (its eye may
/// have drifted with a corpse that kept falling on the client): the player is brought back at its
/// respawn eye, and a <see cref="NetMessage.PlayerCorrection"/> snaps the client there.
/// </para>
/// <para>
/// A player's position is the server's to accept: each <see cref="NetMessage.PlayerState"/> is
/// judged by <see cref="MovementRules"/>. A too-fast move is clamped toward the report at the speed
/// cap; a move into a solid cell, a climb too steep or a hover without the flight flag is rejected,
/// and the client is sent a numbered <see cref="NetMessage.PlayerCorrection"/> back to where the
/// player stood. Until a state acks that number its position is ignored, so the states the client
/// sent before it snapped can never trip a second correction.
/// </para>
/// <para>
/// Creative (and with it flight, which the movement rules treat as noclip) is a per-player
/// permission: a client's creative bit is only a request, granted to everyone while
/// <see cref="AllowCreative"/> is on and otherwise only to the <see cref="Operators"/>
/// (<see cref="MayBeCreative"/>), at join and on every state. A refused request is answered once
/// with a <see cref="NetMessage.PlayerStatus"/> carrying the modes the player really has.
/// </para>
/// <para>
/// The world streams chunks around every connected player (<see cref="World.Focus"/>, fed from
/// <see cref="World.ServerPlayers"/>), so players far apart each get their own terrain and a chunk
/// unloads only once it is outside every player's radius. The server's own camera stays at the
/// world spawn: it is the streaming focus only while nobody is online, and where joiners appear.
/// </para>
/// </summary>
public sealed class GameServer : IDisposable
{
    private readonly ServerState _state;
    private readonly ServerTransport _transport;
    private readonly ServerBroadcasts _broadcasts;
    private readonly StreamRequests _streaming;
    private readonly ServerChat _chat;
    private readonly PlayerSessions _sessions;
    private readonly IntentReplay _intents;
    private double _playerSaveTimer;
    private double _sweepTimer;
    private double _listTimer;
    private double _timeTimer;
    private double _entityTimer;

    public GameServer(World world, string worldName, int seed, int port)
    {
        _state = new ServerState(world);
        Port = port;
        _transport = new ServerTransport(port);
        _broadcasts = new ServerBroadcasts(_state);
        _streaming = new StreamRequests(_state);
        _chat = new ServerChat(_state, _broadcasts);
        _sessions = new PlayerSessions(_state, _chat, worldName, seed);
        _intents = new IntentReplay(_state);
        _state.World.ServerPlayers = _state.PlayerList;
    }

    public int Port { get; }

    /// <summary>
    /// Whether every player may be creative here. Without it only the <see cref="Operators"/> may
    /// (<see cref="MayBeCreative"/>). Defaults to <see cref="NetworkConfig.ServerAllowCreative"/>.
    /// </summary>
    public bool AllowCreative { get => _state.AllowCreative; set => _state.AllowCreative = value; }

    /// <summary>
    /// The players who may be creative while <see cref="AllowCreative"/> is off: the world
    /// folder's <see cref="OperatorList.FileName"/> plus every <c>--op</c> on the command line.
    /// Empty by default, so nobody is.
    /// </summary>
    public OperatorList Operators { get => _state.Operators; set => _state.Operators = value; }

    /// <summary>
    /// Whether the player joining as <paramref name="name"/> may be creative: everyone while
    /// <see cref="AllowCreative"/> is on, otherwise only an operator. A player's creative flag
    /// (requested through the flags of its <see cref="NetMessage.PlayerState"/>, restored from its
    /// save) is only ever set while this holds, flight only with creative, and creative grants and
    /// clears are refused without it.
    /// </summary>
    public bool MayBeCreative(string name) => _state.MayBeCreative(name);

    /// <summary>The connected player with this id (tests and diagnostics; main thread only).</summary>
    public bool TryGetPlayer(int id, out ServerPlayer player) => _state.Players.TryGetValue(id, out player!);

    /// <summary>The port actually bound (resolves an ephemeral port 0), for tests and logs.</summary>
    public int BoundPort => ((IPEndPoint)_transport.Listener.LocalEndpoint).Port;

    public int ClientCount => _state.Clients.Count;

    public void Start()
    {
        _transport.Listener.Start();
        _transport.Running = true;
        // Every raw cell write on the server (an edit or a simulation step) is journaled and sent
        // to clients, so explosions, fluid flow, decay and felling all reach them.
        Chunk.CellWritten = _broadcasts.OnCellWritten;
        _transport.StartAcceptThread();
    }

    /// <summary>Main thread: applies every queued client message and runs the periodic broadcasts.</summary>
    public void Pump(double deltaTime)
    {
        _state.Elapsed += deltaTime;

        // Before the inbound frames: a connection is always queued here before its reader starts,
        // so its hello can never be handled before it is known. One that already said hello (its
        // hello landed between an earlier drain and this one) or was already removed is skipped.
        while (_transport.Accepted.TryDequeue(out var accepted))
        {
            if (!accepted.Hello && !accepted.Removed)
                _state.Pending.Add(accepted);
        }

        while (_transport.InboundQueue.TryDequeue(out var item))
        {
            if (item.Payload == null)
            {
                _sessions.RemoveClient(item.Client);
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
                _sessions.RemoveClient(item.Client);
            }
        }

        _streaming.ServeLodRequests();
        _broadcasts.DrainCellJournal();
        _sessions.SendChangedInventories();
        _sessions.TickPlayers(deltaTime);

        _playerSaveTimer += deltaTime;
        if (_playerSaveTimer >= SaveConfig.AutosaveIntervalSeconds)
        {
            _playerSaveTimer = 0;
            _sessions.SaveAllPlayers();
        }

        _sweepTimer += deltaTime;
        if (_sweepTimer >= 1.0)
        {
            _sweepTimer = 0;
            _sessions.SweepIdleConnections();
        }

        _listTimer += deltaTime;
        _timeTimer += deltaTime;
        _entityTimer += deltaTime;
        if (_listTimer >= NetworkConfig.NetPlayerListSeconds)
        {
            _listTimer = 0;
            _sessions.BroadcastPlayerList();
        }
        if (_timeTimer >= NetworkConfig.NetTimeSyncSeconds)
        {
            _timeTimer = 0;
            _broadcasts.BroadcastTime();
        }
        if (_entityTimer >= NetworkConfig.NetEntitySyncSeconds)
        {
            _entityTimer = 0;
            _broadcasts.BroadcastEntities();
            _broadcasts.BroadcastStations();
        }
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
                _sessions.RemoveClient(client);
                return;
            }

            var version = reader.ReadInt();
            if (version != NetFraming.ProtocolVersion)
            {
                Log.Warn($"net: client on protocol {version}, server on {NetFraming.ProtocolVersion}");
                client.Close();
                _sessions.RemoveClient(client);
                return;
            }

            _sessions.Join(client, reader);
            return;
        }

        switch (message)
        {
            case NetMessage.KeepAlive:
                break;

            case NetMessage.ChatSend:
                _chat.OnChatSend(client, reader);
                break;

            case NetMessage.PlayerState:
                _sessions.OnPlayerState(client, reader);
                break;

            case NetMessage.BlockEdit:
                _intents.OnBlockEdit(client, reader);
                break;

            case NetMessage.WorldIntent:
                _intents.OnWorldIntent(client, reader);
                break;

            case NetMessage.ChunkRequest:
                _streaming.OnChunkRequest(client, reader);
                break;

            case NetMessage.ColumnRequest:
                _streaming.OnColumnRequest(client, reader);
                break;

            case NetMessage.LodRegionRequest:
                _streaming.OnLodRegionRequest(client, reader);
                break;

            case NetMessage.InventoryRequest:
                _intents.OnInventoryRequest(client, reader);
                break;

            default:
                Log.Warn($"net: ignoring {message} from {client.Name} (#{client.Id})");
                break;
        }
    }

    public void Stop()
    {
        if (!_transport.Running)
            return;
        _transport.Running = false;
        Chunk.CellWritten = null;
        // Before the world's SaveAndDispose shuts its store down: everyone still online is saved
        _sessions.SaveAllPlayers();

        try { _transport.Listener.Stop(); } catch (SocketException) { }

        List<ClientConnection> clients;
        lock (_state.Clients)
            clients = [.. _state.Clients.Values];
        foreach (var client in clients)
            client.Close();

        _transport.JoinAcceptThread();

        // Connections that never said hello, including any the accept thread queued after the
        // last Pump, are closed too, or their reader threads would outlive the server.
        while (_transport.Accepted.TryDequeue(out var accepted))
            accepted.Close();
        foreach (var client in _state.Pending)
            client.Close();
        _state.Pending.Clear();
        _streaming.ClearLodQueue();
        _state.Clients.Clear();
        _state.Players.Clear();
        _state.PlayerList.Clear();
    }

    public void Dispose() => Stop();
}
