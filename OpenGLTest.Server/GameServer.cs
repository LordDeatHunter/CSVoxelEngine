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
    private readonly World _world;
    private readonly string _worldName;
    private readonly int _seed;
    private readonly TcpListener _listener;

    // Reader threads hand inbound frames here; only Pump (main thread) drains it.
    private readonly ConcurrentQueue<Inbound> _inbound = new();
    private readonly Dictionary<int, ClientConnection> _clients = new();
    private readonly Dictionary<int, ServerPlayer> _players = new();
    // The same players as a list, handed to the world once (World.ServerPlayers) for its pickup
    private readonly List<ServerPlayer> _playerList = [];
    private double _playerSaveTimer;

    // Connections the accept thread opened, handed to Pump, which keeps the ones that have not
    // said hello yet in _pending so the idle sweep can drop a peer that never does.
    private readonly ConcurrentQueue<ClientConnection> _accepted = new();
    private readonly List<ClientConnection> _pending = [];
    private readonly List<ClientConnection> _idleScratch = [];
    private double _sweepTimer;

    // Far-LOD region requests waiting to be built, served NetLodBuildsPerPump per Pump. Each
    // connection's own set dedupes a region it re-asked for while still queued.
    private readonly Queue<(ClientConnection Client, int Level, int Rx, int Rz)> _lodQueue = new();

    // Where joiners spawn: the server camera's position at the first hello. The camera never
    // follows the players (chunks stream around each of them through World.Focus; the camera is
    // the focus only while nobody is online), so it still stands at the world spawn, and later
    // joiners never inherit where whoever is online happens to be, which can sit inside rock.
    private Vector3? _joinSpawn;

    private Thread? _acceptThread;
    // Long-lived, coalesced cell changes the server's own simulation produced (explosions, fluid
    // flow, decay, felling), drained and broadcast in batches.
    private readonly ConcurrentQueue<CellChange> _cellJournal = new();
    private readonly Dictionary<(int X, int Y, int Z), (byte Id, byte State)> _cellCoalesce = new();
    private const int MaxCellChangesPerBroadcast = 8192;

    private readonly record struct CellChange(int X, int Y, int Z, byte Id, byte State);

    // Scratch for one correction's cells
    private readonly List<NetCellChange> _correctionScratch = [];

    // Whether the last station broadcast carried anything, so the one after the last station went
    // away still goes out and clears the clients' mirrors
    private bool _stationsSent;

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
        _world.ServerPlayers = _playerList;
    }

    public int Port { get; }

    /// <summary>
    /// Whether every player may be creative here. Without it only the <see cref="Operators"/> may
    /// (<see cref="MayBeCreative"/>). Defaults to <see cref="WorldGenerationConfig.ServerAllowCreative"/>.
    /// </summary>
    public bool AllowCreative { get; set; } = WorldGenerationConfig.ServerAllowCreative;

    /// <summary>
    /// The players who may be creative while <see cref="AllowCreative"/> is off: the world
    /// folder's <see cref="OperatorList.FileName"/> plus every <c>--op</c> on the command line.
    /// Empty by default, so nobody is.
    /// </summary>
    public OperatorList Operators { get; set; } = new();

    /// <summary>
    /// Whether the player joining as <paramref name="name"/> may be creative: everyone while
    /// <see cref="AllowCreative"/> is on, otherwise only an operator. A player's creative flag
    /// (requested through the flags of its <see cref="NetMessage.PlayerState"/>, restored from its
    /// save) is only ever set while this holds, flight only with creative, and creative grants and
    /// clears are refused without it.
    /// </summary>
    public bool MayBeCreative(string name) => AllowCreative || Operators.Contains(name);

    /// <summary>The connected player with this id (tests and diagnostics; main thread only).</summary>
    public bool TryGetPlayer(int id, out ServerPlayer player) => _players.TryGetValue(id, out player!);

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
        SendChangedInventories();
        TickPlayers(deltaTime);

        _playerSaveTimer += deltaTime;
        if (_playerSaveTimer >= WorldGenerationConfig.AutosaveIntervalSeconds)
        {
            _playerSaveTimer = 0;
            SaveAllPlayers();
        }

        _sweepTimer += deltaTime;
        if (_sweepTimer >= 1.0)
        {
            _sweepTimer = 0;
            SweepIdleConnections();
        }

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
            BroadcastStations();
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

            // Unique among the players online, since the name keys the player's save file: two
            // clients both called "Player" must not load (and overwrite) the same inventory
            client.Name = UniqueName(CleanName(reader.ReadString()));
            client.Hello = true;
            _pending.Remove(client);
            // The server camera stands at the world spawn and never follows the players, so every
            // joiner starts there, never wherever the players online have wandered.
            var joinSpawn = _joinSpawn ??= _world.Camera.Position;
            var player = new ServerPlayer(client.Id, client.Name, joinSpawn) { LastStateAt = _elapsed };
            // A returning player gets back their inventory and modes, and starts where they left
            var restored = player.Restore(ServerPlayerSaves.Load(_world.SaveStore, client.Name), MayBeCreative(client.Name));
            var spawn = restored ? player.Position : joinSpawn;
            // A player with no saved respawn eye comes back where it first stood, like singleplayer's
            // first settled position; the teleport starts its fall tracking there
            player.RespawnEye ??= spawn;
            player.Teleport(spawn);
            _clients[client.Id] = client;
            _players[client.Id] = player;
            _playerList.Add(player);
            SendServerHello(client, spawn);
            SendInventory(client, player);
            // A returning player's health (or death) is mirrored at once
            SendPlayerStatus(client, player);
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
                // The newest PlayerCorrection the client has applied: until it acks the newest one
                // it was sent, its states still carry the positions the correction undid
                var correctionAck = reader.ReadInt();
                if (_players.TryGetValue(client.Id, out var current))
                {
                    // The client's creative bit (its C toggle) is only a request: this server
                    // grants it to everyone while it allows creative, otherwise to its operators
                    // only, and flight only with it. Granted first, so this very state's move is
                    // judged under the flight it was made in.
                    var requested = (NetPlayerFlags)flags;
                    current.Creative = MayBeCreative(client.Name) && requested.HasFlag(NetPlayerFlags.Creative);
                    current.Flying = current.Creative && requested.HasFlag(NetPlayerFlags.Flying);
                    current.Yaw = yaw;
                    current.Pitch = pitch;
                    current.Flags = flags;

                    // A dead player stays where it died until it respawns, whatever its client
                    // simulated meanwhile, and a corrected one where it was snapped to until its
                    // client acks the correction; the look, flags and modes are still recorded.
                    if (!current.IsDead && current.AcceptsStatePosition(correctionAck))
                    {
                        // A teleport-scale move is clamped toward the report at the speed cap, so a
                        // burst of fast states converges instead of freezing the player at a stale
                        // point; an illegal one (into a solid cell, a climb or hover without flight)
                        // is rejected and the client snapped back (MovementRules). The timestamp
                        // always advances, so refused states cannot accumulate time until a
                        // teleport fits.
                        var chunks = _world.GetChunkManager();
                        var gap = _elapsed - current.LastStateAt;
                        var verdict = MovementRules.Judge(current.Position, position, gap, current.Flying, chunks,
                            current.Movement, out var accepted);
                        current.Position = accepted;
                        switch (verdict)
                        {
                            case MoveVerdict.Rejected:
                                // Once per correction: the states that follow are ignored until
                                // the client acks it
                                Log.Warn($"net: corrected an illegal move from {client.Name} (#{client.Id})");
                                current.ResetFall();
                                SendPlayerCorrection(client, current.IssueCorrection(), accepted);
                                break;
                            case MoveVerdict.Clamped:
                                Log.Warn($"net: clamped a move from {client.Name} (#{client.Id})");
                                current.ObserveAcceptedState(chunks, gap);
                                break;
                            default:
                                current.ObserveAcceptedState(chunks, gap);
                                break;
                        }
                    }
                    current.LastStateAt = _elapsed;
                    // Between intents the context stands where the player does (a pour bound to
                    // the player follows it)
                    current.Aim(current.Position, current.Forward);
                    // A refused request is answered once, so the client stops acting on modes it
                    // does not have
                    if (current.ShouldReportModes(requested))
                        SendPlayerStatus(client, current);
                }
                break;
            }

            case NetMessage.BlockEdit:
            {
                // Retired: a client never writes a cell. The edit is refused whatever it names, and
                // a cell in reach is answered with its authoritative state.
                var x = reader.ReadInt();
                var y = reader.ReadInt();
                var z = reader.ReadInt();
                reader.ReadByte();
                reader.ReadByte();
                Log.Warn($"net: refused a raw block edit from {client.Name} (#{client.Id})");
                if (_players.TryGetValue(client.Id, out var player)
                    && NetValidation.InEditReach(player.Position, x, y, z, WorldGenerationConfig.NetMaxEditReachBlocks))
                    SendCellCorrection(client, player.LastSeq, x, y, z, radius: 0);
                break;
            }

            case NetMessage.WorldIntent:
            {
                var intent = WorldIntentCodec.Read(reader, out var seq);
                if (_players.TryGetValue(client.Id, out var player))
                    HandleWorldIntent(client, player, seq, intent);
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
                // Exactly what this server's own mesher reads for the column (heights, water
                // levels, corner climate) from the world's source - worldgen, or an imported map
                // where it supplies the column - so a client derives nothing from worldgen.
                _world.GetChunkManager().ColumnSource.TryGet(cx, cz, out var terrain);
                using var writer = new PacketWriter(NetMessage.ColumnTerrain);
                writer.Write(cx);
                writer.Write(cz);
                writer.WriteBytes(ChunkNetCodec.EncodeColumn(terrain));
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
                var seq = reader.ReadInt();
                var intent = InventoryIntentCodec.Read(reader);
                if (!_players.TryGetValue(client.Id, out var player))
                    break;
                // Replayed against this player's own inventory only, and only when it is newer than
                // everything the player sent before. A Q-drop leaves from the player's eye along
                // their look, never from the server's own camera (which stands at the world spawn).
                var accepted = player.AcceptSeq(seq);
                if (accepted)
                {
                    switch (intent.Action)
                    {
                        case NetInventoryAction.DropSelected:
                        case NetInventoryAction.DropAllSelected:
                            accepted = _world.ThrowSelected(intent.Action == NetInventoryAction.DropAllSelected,
                                player.Position, player.Forward, player.Inventory);
                            break;
                        default:
                            accepted = InventoryIntentRules.Apply(player.Inventory, intent, player.Creative);
                            break;
                    }
                }

                // Always, accepted or not: the snapshot is the truth the client's optimistic copy
                // snaps back to when the server refused what it did.
                SendInventory(client, player);
                SendIntentResult(client, seq, accepted);
                break;
            }

            default:
                Log.Warn($"net: ignoring {message} from {client.Name} (#{client.Id})");
                break;
        }
    }

    /// <summary>
    /// Judges and applies one world intent for <paramref name="player"/>, then answers it: the
    /// authoritative cells around its target when it could edit cells (in reach of the player),
    /// the inventory when it could change it or was refused, and its result, in that order.
    /// A dead player is judged before every other rule: only a respawn is accepted, however far
    /// its eye drifted, and anything else is refused.
    /// </summary>
    private void HandleWorldIntent(ClientConnection client, ServerPlayer player, int seq, in WorldIntent intent)
    {
        var accepted = player.AcceptSeq(seq)
                       && (player.IsDead
                           ? intent.Kind == NetIntentKind.Respawn && RespawnFor(client, player)
                           : ApplyWorldIntent(client, player, intent));
        if (intent.EditsCells && intent.HasCell
            && NetValidation.InEditReach(player.Position, intent.X, intent.Y, intent.Z, WorldGenerationConfig.NetMaxEditReachBlocks))
            SendCellCorrection(client, seq, intent.X, intent.Y, intent.Z, radius: 1);
        if (intent.PredictsInventory || !accepted)
            SendInventory(client, player);
        SendIntentResult(client, seq, accepted);
    }

    /// <summary>
    /// The rules every world intent of a living player passes before the world replays it: the
    /// world is playing, the intent was aimed from (near) where the player is, and an edit fits the
    /// player's edit rate. A living player has nothing to respawn from, so its respawn is refused.
    /// </summary>
    private bool ApplyWorldIntent(ClientConnection client, ServerPlayer player, in WorldIntent intent)
    {
        if (intent.Kind == NetIntentKind.Respawn)
            return false;
        if (!_world.IsPlaying)
            return false;
        if (!NetValidation.EyeWithin(player.Position, intent.Eye, WorldGenerationConfig.NetMaxIntentEyeDriftBlocks))
            return false;
        if (intent.IsRateLimited && !player.EditLimiter.TryTake(_elapsed))
            return false;

        player.Aim(intent.Eye, intent.Direction);
        var context = player.Context;
        // Other clients see the swing even when the hit itself is refused.
        switch (intent.Kind)
        {
            case NetIntentKind.BreakStart:
            case NetIntentKind.BreakComplete:
                player.NoteAction(NetPlayerAction.Punch);
                break;
            case NetIntentKind.Throw:
                player.NoteAction(NetPlayerAction.Throw);
                break;
            case NetIntentKind.Attack:
                player.NoteAction(intent.Attack switch
                {
                    NetAttackKind.Slice => NetPlayerAction.Slice,
                    NetAttackKind.SliceReturn => NetPlayerAction.SliceReturn,
                    _ => NetPlayerAction.Punch,
                });
                break;
        }
        switch (intent.Kind)
        {
            case NetIntentKind.BreakStart:
                return StartBreak(player, intent);
            case NetIntentKind.BreakComplete:
                return CompleteBreak(player, intent);
            case NetIntentKind.Place:
                return PlaceFor(player, intent);
            case NetIntentKind.Use:
                // The right-click chain short of its placement fallback: a click the client did
                // not place with never places here either
                return _world.TryUseSelected(World.InteractionReach, context, out _, allowPlace: false);
            case NetIntentKind.Throw:
                return _world.ThrowFor(context, intent.Charge);
            case NetIntentKind.Attack:
                return AttackFor(player, intent);
            default:
                return false;
        }
    }

    // The player's own ray from the intent's eye along its aim lands on the cell the intent names:
    // within reach, with nothing in the way
    private bool AimsAt(ServerPlayer player, in WorldIntent intent, out Vector3 normal) =>
        _world.RaycastFor(player.Context, includeFluids: false, out var hit, out normal) && hit == intent.Cell;

    // A loaded cell holding a block that can be mined: not air, not a fluid
    private bool TryMineable(in WorldIntent intent, out byte id, out byte state) =>
        _world.GetChunkManager().TryGetCell(intent.X, intent.Y, intent.Z, out id, out state)
        && id != 0 && !BlockType.IsFluid(id);

    private bool StartBreak(ServerPlayer player, in WorldIntent intent)
    {
        player.Breaking.Reset();
        if (!AimsAt(player, intent, out _) || !TryMineable(intent, out var id, out var state))
            return false;
        var inventory = player.Inventory;
        player.Breaking.Start(intent.X, intent.Y, intent.Z, id, state, inventory.HeldItem,
            inventory.SelectedBlockId, inventory.SelectedToolInstance, _elapsed);
        return true;
    }

    private bool CompleteBreak(ServerPlayer player, in WorldIntent intent)
    {
        if (!AimsAt(player, intent, out _) || !TryMineable(intent, out var id, out _))
        {
            player.Breaking.Reset();
            return false;
        }

        var inventory = player.Inventory;
        var verdict = player.Breaking.Complete(intent.X, intent.Y, intent.Z, id, inventory.SelectedBlockId,
            inventory.SelectedToolInstance, player.Creative, _elapsed, WorldGenerationConfig.NetBreakSlackSeconds);
        return verdict == BreakVerdict.Break && _world.BreakBlockFor(player.Context, intent.Cell);
    }

    private bool PlaceFor(ServerPlayer player, in WorldIntent intent)
    {
        // The face placed against: the server ray's own, or another face of the same cell turned
        // toward the eye (a third-person aim reports the camera ray's face). Never a face the eye
        // cannot see, which would place through the block.
        if (!AimsAt(player, intent, out var normal))
            return false;
        var face = intent.Normal;
        if (face != normal && !NetValidation.FaceFacesEye(intent.Cell, face, intent.Eye))
            return false;
        return _world.TryPlaceSelectedBlockAt(player.Context, intent.Cell, face);
    }

    private bool AttackFor(ServerPlayer player, in WorldIntent intent)
    {
        // A slice needs a slicing weapon in hand, a punch anything else, and either one only at its
        // own pace. A swing that strikes nothing is still a legal swing.
        var held = player.Inventory.HeldItem;
        var slicing = held is { Attack: AttackPattern.Slice };
        if (slicing != (intent.Attack != NetAttackKind.Punch))
            return false;
        if (!player.TryAttack(intent.Attack, held, _elapsed, WorldGenerationConfig.NetAttackSlackSeconds))
            return false;
        if (intent.Attack == NetAttackKind.Punch)
            _world.PunchFor(player.Context);
        else
            _world.SliceFor(player.Context, intent.Attack == NetAttackKind.SliceReturn);
        return true;
    }

    /// <summary>
    /// Brings a dead <paramref name="player"/> back (full health, at its respawn eye or else the
    /// join spawn), snaps its client there with a <see cref="NetMessage.PlayerCorrection"/> whose
    /// ack its states must carry before they move it again, and tells it its fresh status.
    /// </summary>
    private bool RespawnFor(ClientConnection client, ServerPlayer player)
    {
        player.Respawn(_joinSpawn ?? _world.Camera.Position);
        SendPlayerCorrection(client, player.IssueCorrection(), player.Position);
        SendPlayerStatus(client, player);
        Log.WriteLine($"net: {client.Name} (#{client.Id}) respawned");
        return true;
    }

    private static void SendIntentResult(ClientConnection client, int seq, bool accepted)
    {
        using var writer = new PacketWriter(NetMessage.IntentResult);
        writer.Write(seq);
        writer.Write(accepted);
        client.Send(writer.ToArray());
    }

    /// <summary>
    /// Tells <paramref name="client"/> its player's server-owned status: the granted modes, the
    /// health, whether it is dead, and the knockback queued since the last status (handed over and
    /// cleared here, zero when none). Clears <see cref="ServerPlayer.StatusDirty"/>.
    /// </summary>
    private static void SendPlayerStatus(ClientConnection client, ServerPlayer player)
    {
        player.TakePendingKnockback(out var knockback, out var lift);
        using var writer = new PacketWriter(NetMessage.PlayerStatus);
        PlayerStatusCodec.Write(writer,
            new PlayerStatusData(player.GrantedModes, player.Status.Health, player.IsDead, knockback, lift));
        client.Send(writer.ToArray());
        player.StatusDirty = false;
    }

    /// <summary>
    /// Tells <paramref name="client"/> to snap its camera to <paramref name="eye"/>, numbered
    /// <paramref name="seq"/> (<see cref="ServerPlayer.IssueCorrection"/>) so its later states can
    /// ack it.
    /// </summary>
    private static void SendPlayerCorrection(ClientConnection client, int seq, Vector3 eye)
    {
        using var writer = new PacketWriter(NetMessage.PlayerCorrection);
        writer.Write(seq);
        writer.Write(eye);
        client.Send(writer.ToArray());
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

            // From the world's own source, so an imported map's far terrain is what clients see.
            var data = LodRegion.BuildMeshData(request.Level, request.Rx, request.Rz,
                source: _world.GetChunkManager().WorldSource);
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

    /// <summary>Sends <paramref name="player"/>'s whole inventory to its own client, and only to it,
    /// tagged with the newest intent it reflects.</summary>
    private static void SendInventory(ClientConnection client, ServerPlayer player)
    {
        player.InventoryChanged = false;
        using var writer = new PacketWriter(NetMessage.InventoryState);
        writer.Write(player.LastSeq);
        writer.WriteBytes(InventoryNetCodec.Encode(player.Inventory.Export()));
        client.Send(writer.ToArray());
    }

    // A pickup (or anything else the world credited) changed a player's inventory since its last
    // snapshot: send that player a fresh one.
    private void SendChangedInventories()
    {
        foreach (var player in _playerList)
        {
            if (player.InventoryChanged && _clients.TryGetValue(player.Id, out var client))
                SendInventory(client, player);
        }
    }

    // Every player's per-tick status (lava, the damage flash), then a fresh status to each one whose
    // status changed since its last, whether here or by a mob hit or a fall during the frame
    private void TickPlayers(double deltaTime)
    {
        var chunks = _world.GetChunkManager();
        foreach (var player in _playerList)
        {
            player.TickStatus((float)deltaTime, chunks);
            if (player.StatusDirty && _clients.TryGetValue(player.Id, out var client))
                SendPlayerStatus(client, player);
        }
    }

    // Queues every connected player's file (nothing without a save store)
    private void SaveAllPlayers()
    {
        foreach (var player in _playerList)
            ServerPlayerSaves.Save(_world.SaveStore, player);
    }

    /// <summary>
    /// <paramref name="requested"/>, or "<c>requested n</c>" for the smallest n &gt;= 2 whose save
    /// slug no player online has, so two connected players never share a save file.
    /// </summary>
    private string UniqueName(string requested)
    {
        var name = requested;
        for (var n = 2; SlugTaken(name); n++)
            name = $"{requested} {n}";
        return name;
    }

    private bool SlugTaken(string name)
    {
        var slug = WorldNames.Slug(name);
        foreach (var player in _playerList)
            if (string.Equals(WorldNames.Slug(player.Name), slug, StringComparison.Ordinal))
                return true;
        return false;
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
            writer.Write((byte)player.LastAction);
            writer.Write(player.ActionSeq);
        }
        Broadcast(writer.ToArray());
    }

    private void BroadcastTime()
    {
        using var writer = new PacketWriter(NetMessage.TimeSync);
        writer.Write(_world.LightingTime);
        Broadcast(writer.ToArray());
    }

    /// <summary>
    /// Broadcasts every live mob, drop, primed TNT and falling block (<see cref="NetMessage.EntityList"/>),
    /// each under a stable id by object identity. The TNT and falling cubes are drawn by clients as
    /// display-only mirrors; only this world ticks, detonates and lands them.
    /// </summary>
    private void BroadcastEntities()
    {
        var mobs = _world.Mobs;
        var drops = _world.Drops;
        var primed = _world.PrimedTnt;
        var falling = _world.Falling;
        _entitySeen.Clear();

        using var writer = new PacketWriter(NetMessage.EntityList);
        writer.Write(mobs.Count + drops.Count + primed.Count + falling.Count);
        for (var i = 0; i < mobs.Count; i++)
        {
            var mob = mobs[i];
            var kind = mob is Slime ? NetEntityKind.Slime : NetEntityKind.Goblin;
            // The int field is a goblin's ammo and a slime's size (NetEntity)
            var ammo = mob switch
            {
                Goblin goblin => goblin.Ammo,
                Slime slime => slime.Size,
                _ => 0,
            };
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

        // The fuse travels in milliseconds in the two int fields, so the client's flash and swell
        // follow the server's fuse
        for (var i = 0; i < primed.Count; i++)
        {
            var tnt = primed[i];
            writer.Write((byte)NetEntityKind.PrimedTnt);
            writer.Write(IdFor(tnt));
            writer.Write(tnt.Position);
            writer.Write(0f);
            writer.Write((int)MathF.Round(tnt.FuseRemaining * 1000f));
            writer.Write((int)MathF.Round(tnt.FuseDuration * 1000f));
            writer.Write(tnt.BlockId);
            writer.Write(Vector3.Zero);
        }

        for (var i = 0; i < falling.Count; i++)
        {
            var block = falling[i];
            writer.Write((byte)NetEntityKind.FallingBlock);
            writer.Write(IdFor(block));
            writer.Write(block.Position);
            writer.Write(0f);
            writer.Write(0);
            writer.Write(0);
            writer.Write(block.BlockId);
            writer.Write(Vector3.Zero);
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

    /// <summary>
    /// Sends the authoritative state of every loaded cell within <paramref name="radius"/> (on each
    /// axis) of (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>), tagged with the
    /// intent <paramref name="seq"/> it answers: whatever the client predicted around that target
    /// (the cell itself, the face neighbour it placed into, a popped pebble above, a structure's
    /// cells) snaps to what the server has, accepted or not.
    /// </summary>
    private void SendCellCorrection(ClientConnection client, int seq, int x, int y, int z, int radius)
    {
        var manager = _world.GetChunkManager();
        _correctionScratch.Clear();
        for (var dx = -radius; dx <= radius; dx++)
        for (var dy = -radius; dy <= radius; dy++)
        for (var dz = -radius; dz <= radius; dz++)
        {
            if (manager.TryGetCell(x + dx, y + dy, z + dz, out var id, out var state))
                _correctionScratch.Add(new NetCellChange(x + dx, y + dy, z + dz, id, state));
        }

        using var writer = new PacketWriter(NetMessage.BlockCorrection);
        writer.Write(seq);
        writer.Write(_correctionScratch.Count);
        foreach (var cell in _correctionScratch)
        {
            writer.Write(cell.X);
            writer.Write(cell.Y);
            writer.Write(cell.Z);
            writer.Write(cell.Id);
            writer.Write(cell.State);
        }
        client.Send(writer.ToArray());
    }

    /// <summary>
    /// Broadcasts every campfire, quern, kiln and cast mold the server runs, which the clients draw
    /// in place of their own; nothing while there are none (after one last empty snapshot).
    /// </summary>
    private void BroadcastStations()
    {
        var any = _world.HasStations;
        if (!any && !_stationsSent)
            return;
        _stationsSent = any;
        using var writer = new PacketWriter(NetMessage.StationState);
        _world.WriteStations(writer);
        Broadcast(writer.ToArray());
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
        if (_players.Remove(client.Id, out var player))
        {
            _playerList.Remove(player);
            // Anything bound to the player (a pour from its crucible) pauses from here on
            player.Context.Present = false;
            player.Breaking.Reset();
            ServerPlayerSaves.Save(_world.SaveStore, player);
        }
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
        // Before the world's SaveAndDispose shuts its store down: everyone still online is saved
        SaveAllPlayers();

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
        _playerList.Clear();
    }

    public void Dispose() => Stop();

    private readonly record struct Inbound(ClientConnection Client, byte[]? Payload);

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
