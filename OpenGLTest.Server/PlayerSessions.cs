using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Player;
using VoxelEngine.Saves;

namespace VoxelEngine.Server;

/// <summary>The connected players' sessions: the join (hello), each PlayerState judged by the movement rules, the status ticks, the saves, unique names, the player list, leaving and the idle sweep, and the per-client status, correction and inventory senders the other components share. Main thread only.</summary>
internal sealed class PlayerSessions
{
    private readonly ServerState _state;
    private readonly ServerChat _chat;
    private readonly string _worldName;
    private readonly int _seed;

    private readonly List<ClientConnection> _idleScratch = [];

    internal PlayerSessions(ServerState state, ServerChat chat, string worldName, int seed)
    {
        _state = state;
        _chat = chat;
        _worldName = worldName;
        _seed = seed;
    }

    internal void Join(ClientConnection client, PacketReader reader)
    {
        // Unique among the players online, since the name keys the player's save file: two
        // clients both called "Player" must not load (and overwrite) the same inventory
        client.Name = UniqueName(CleanName(reader.ReadString()));
        client.Hello = true;
        _state.Pending.Remove(client);
        // The server camera stands at the world spawn and never follows the players, so every
        // joiner starts there, never wherever the players online have wandered.
        var joinSpawn = _state.JoinSpawn ??= _state.World.Camera.Position;
        var player = new ServerPlayer(client.Id, client.Name, joinSpawn) { LastStateAt = _state.Elapsed };
        // A returning player gets back their inventory and modes, and starts where they left
        var restored = player.Restore(ServerPlayerSaves.Load(_state.World.SaveStore, client.Name), _state.MayBeCreative(client.Name));
        var spawn = restored ? player.Position : joinSpawn;
        // A player with no saved respawn eye comes back where it first stood, like singleplayer's
        // first settled position; the teleport starts its fall tracking there
        player.RespawnEye ??= spawn;
        player.Teleport(spawn);
        _state.Clients[client.Id] = client;
        _state.Players[client.Id] = player;
        _state.PlayerList.Add(player);
        SendServerHello(client, spawn);
        SendInventory(client, player);
        // A returning player's health (or death) is mirrored at once
        SendPlayerStatus(client, player);
        BroadcastPlayerList();
        _chat.BroadcastSystem($"{client.Name} joined the game");
        Log.WriteLine($"net: {client.Name} (#{client.Id}) joined from {client.Remote}");
    }

    internal void OnPlayerState(ClientConnection client, PacketReader reader)
    {
        var position = reader.ReadVector3();
        var yaw = reader.ReadFloat();
        var pitch = reader.ReadFloat();
        var flags = reader.ReadByte();
        // The newest PlayerCorrection the client has applied: until it acks the newest one
        // it was sent, its states still carry the positions the correction undid
        var correctionAck = reader.ReadInt();
        if (_state.Players.TryGetValue(client.Id, out var current))
        {
            // The client's creative bit (its C toggle) is only a request: this server
            // grants it to everyone while it allows creative, otherwise to its operators
            // only, and flight only with it. Granted first, so this very state's move is
            // judged under the flight it was made in.
            var requested = (NetPlayerFlags)flags;
            current.Creative = _state.MayBeCreative(client.Name) && requested.HasFlag(NetPlayerFlags.Creative);
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
                var chunks = _state.World.GetChunkManager();
                // A rider standing on an iceberg is grounded, never a hoverer
                var solids = _state.World.BodySolids;
                var gap = _state.Elapsed - current.LastStateAt;
                var verdict = MovementRules.Judge(current.Position, position, gap, current.Flying, chunks,
                    current.Movement, out var accepted, solids, airJumps: current.AirJumps);
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
                        PumpOnJump(current, current.ObserveAcceptedState(chunks, gap, solids));
                        break;
                    default:
                        PumpOnJump(current, current.ObserveAcceptedState(chunks, gap, solids));
                        break;
                }
            }
            current.LastStateAt = _state.Elapsed;
            // Between intents the context stands where the player does (a pour bound to
            // the player follows it)
            current.Aim(current.Position, current.Forward);
            // A refused request is answered once, so the client stops acting on modes it
            // does not have
            if (current.ShouldReportModes(requested))
                SendPlayerStatus(client, current);
        }
    }

    /// <summary>
    /// Tells <paramref name="client"/> its player's server-owned status: the granted modes, the
    /// health, whether it is dead, the knockback queued since the last status (handed over and
    /// cleared here, zero when none), the air and the mana. Clears <see cref="ServerPlayer.StatusDirty"/>.
    /// </summary>
    /// <summary>A jump of <paramref name="player"/> (see <see cref="ServerPlayer.ObserveAcceptedState"/>) works the
    /// bellows of every lit bloomery within reach of it.</summary>
    private void PumpOnJump(ServerPlayer player, bool jumped)
    {
        if (jumped)
            _state.World.PumpBellows(player.Position);
    }

    internal static void SendPlayerStatus(ClientConnection client, ServerPlayer player)
    {
        player.TakePendingKnockback(out var knockback, out var lift);
        using var writer = new PacketWriter(NetMessage.PlayerStatus);
        PlayerStatusCodec.Write(writer,
            new PlayerStatusData(player.GrantedModes, player.Status.Health, player.IsDead, knockback, lift, player.Status.Breath.NetStep,
                player.Status.Mana.NetMana, player.Status.Mana.Max, player.Status.Effects.Mask));
        client.Send(writer.ToArray());
        player.StatusDirty = false;
    }

    /// <summary>
    /// Tells <paramref name="client"/> to snap its camera to <paramref name="eye"/>, numbered
    /// <paramref name="seq"/> (<see cref="ServerPlayer.IssueCorrection"/>) so its later states can
    /// ack it.
    /// </summary>
    internal static void SendPlayerCorrection(ClientConnection client, int seq, Vector3 eye)
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
        writer.Write(_state.World.LightingTime);
        writer.Write(spawn);
        client.Send(writer.ToArray());
    }

    /// <summary>
    /// Drops every connection, hello'd or still pending, that sent nothing for
    /// <see cref="NetworkConfig.NetIdleTimeoutSeconds"/>: a peer that vanished without a
    /// FIN, or one that connected and never said hello. Runs about once a second.
    /// </summary>
    internal void SweepIdleConnections()
    {
        var now = Environment.TickCount64;
        var limitMs = (long)(NetworkConfig.NetIdleTimeoutSeconds * 1000.0);
        _idleScratch.Clear();
        foreach (var client in _state.Clients.Values)
            if (now - client.LastInboundAt > limitMs)
                _idleScratch.Add(client);
        foreach (var client in _state.Pending)
            if (now - client.LastInboundAt > limitMs)
                _idleScratch.Add(client);

        foreach (var client in _idleScratch)
        {
            Log.Warn($"net: dropping {client.Name} (#{client.Id}) from {client.Remote}: idle for {NetworkConfig.NetIdleTimeoutSeconds:0} s");
            client.Close();
            RemoveClient(client);
        }
        _idleScratch.Clear();
    }

    /// <summary>Sends <paramref name="player"/>'s whole inventory to its own client, and only to it,
    /// tagged with the newest intent it reflects.</summary>
    internal static void SendInventory(ClientConnection client, ServerPlayer player)
    {
        player.InventoryChanged = false;
        using var writer = new PacketWriter(NetMessage.InventoryState);
        writer.Write(player.LastSeq);
        writer.WriteBytes(InventoryNetCodec.Encode(player.Inventory.Export()));
        client.Send(writer.ToArray());
    }

    // A pickup (or anything else the world credited) changed a player's inventory since its last
    // snapshot: send that player a fresh one.
    internal void SendChangedInventories()
    {
        foreach (var player in _state.PlayerList)
        {
            if (player.InventoryChanged && _state.Clients.TryGetValue(player.Id, out var client))
                SendInventory(client, player);
        }
    }

    // Every player's per-tick status (lava, the damage flash), then a fresh status to each one whose
    // status changed since its last, whether here or by a mob hit or a fall during the frame
    internal void TickPlayers(double deltaTime)
    {
        var chunks = _state.World.GetChunkManager();
        foreach (var player in _state.PlayerList)
        {
            player.TickStatus((float)deltaTime, chunks);
            // A death is announced in the chat once, and re-armed by the respawn
            if (player.Status.IsDead && !player.DeathAnnounced)
            {
                player.DeathAnnounced = true;
                _chat.BroadcastSystem(DeathMessages.For(player.Name, player.Status.LastSource,
                    player.Status.LastKiller));
            }
            else if (!player.Status.IsDead)
            {
                player.DeathAnnounced = false;
            }
            if (player.StatusDirty && _state.Clients.TryGetValue(player.Id, out var client))
                SendPlayerStatus(client, player);
        }
    }

    // Queues every connected player's file (nothing without a save store)
    internal void SaveAllPlayers()
    {
        foreach (var player in _state.PlayerList)
            ServerPlayerSaves.Save(_state.World.SaveStore, player);
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
        foreach (var player in _state.PlayerList)
            if (string.Equals(WorldNames.Slug(player.Name), slug, StringComparison.Ordinal))
                return true;
        return false;
    }

    internal void BroadcastPlayerList()
    {
        using var writer = new PacketWriter(NetMessage.PlayerList);
        writer.Write(_state.Players.Count);
        Span<byte> worn = stackalloc byte[VoxelEngine.Config.GearConfig.ArmorSlotCount];
        foreach (var (id, player) in _state.Players)
        {
            writer.Write(id);
            writer.Write(player.Name);
            writer.Write(player.Position);
            writer.Write(player.Yaw);
            writer.Write(player.Pitch);
            writer.Write(player.Flags);
            writer.Write((byte)player.LastAction);
            writer.Write(player.ActionSeq);
            VoxelEngine.Player.ArmorLook.From(player.Inventory.Equipment).CopyTo(worn);
            foreach (var piece in worn) writer.Write(piece);
        }
        _state.Broadcast(writer.ToArray());
    }

    internal void RemoveClient(ClientConnection client)
    {
        client.Close();
        client.Removed = true;
        _state.Pending.Remove(client);
        if (!_state.Clients.Remove(client.Id))
            return;
        if (_state.Players.Remove(client.Id, out var player))
        {
            _state.PlayerList.Remove(player);
            // Anything bound to the player (a pour from its crucible) pauses from here on
            player.Context.Present = false;
            player.Breaking.Reset();
            ServerPlayerSaves.Save(_state.World.SaveStore, player);
        }
        _chat.BroadcastSystem($"{client.Name} left the game");
        BroadcastPlayerList();
        Log.WriteLine($"net: {client.Name} (#{client.Id}) disconnected");
    }

    private static string CleanName(string? requested)
    {
        var name = requested?.Trim();
        if (string.IsNullOrEmpty(name))
            return "Player";
        return name.Length > WorldNames.MaxNameLength ? name[..WorldNames.MaxNameLength] : name;
    }
}
