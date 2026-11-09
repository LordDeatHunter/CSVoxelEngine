using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using VoxelEngine.App;
using VoxelEngine.Audio;
using VoxelEngine.Platform;
using VoxelEngine.Player;
using VoxelEngine.Rendering;
using VoxelEngine.Saves;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.Menus;
using VoxelEngine.UI.Waystone;

namespace VoxelEngine.Networking.Client;

/// <summary>The multiplayer client's side of a session: the connection and its handshake, the chunk and far-LOD streamers, the prediction, the send cadences (state, keep-alive, respawn retry), the server-message dispatch and the teardown back to the server list. Owned by Program; main thread only (the connect itself runs on its own thread and touches only the NetworkClient).</summary>
internal sealed class ClientSession
{
    private readonly WindowManager windowManager;
    private readonly InputManager inputManager;
    private readonly RenderManager renderManager;
    private readonly SfxManager? sfx;
    private readonly LoadingScreenView loadingView;
    private readonly ChatView chatView;
    private readonly DevDiagnostics diagnostics;

    private World? ActiveWorld
    {
        get => Program.ActiveWorld;
        set => Program.ActiveWorld = value;
    }

    private WorldInfo? CurrentWorld => Program.CurrentWorld;


    // The live multiplayer connection, null in singleplayer and on the menu
    private NetworkClient? netClient;
    private ClientChunkStreamer? clientStreamer;
    private ClientLodStreamer? clientLodStreamer;
    private int netPlayerId = -1;
    private readonly List<RemotePlayerSnapshot> remoteScratch = new();
    private readonly List<NetEntity> netEntitiesScratch = new();
    private readonly List<NetCellChange> netCellsScratch = new();
    // The server values a message lets through to the world (the rest wait behind a prediction)
    private readonly List<NetCellChange> netApplyScratch = new();
    private readonly List<WaystoneNetRow> waystoneRowsScratch = new();
    // What this client predicted and the server has not answered yet; one per connection
    private ClientPrediction prediction = new();
    private double netStateTimer;
    private double netKeepAliveTimer;
    // The sequence id of the newest PlayerCorrection applied, echoed in every PlayerState so the
    // server knows the states it reads were sent from the corrected position
    private int netCorrectionAck;
    // Whether the server's status says this player is dead, and the time since the last Respawn
    // intent sent while it does
    private bool netDead;
    private float netRespawnTimer;
    // The client's death screen: it asks the server to respawn only once the player chooses to
    private readonly RespawnPrompt respawnPrompt = new();

    // The BlockChanges filter, made once in the constructor: it reads the live prediction field, and a lambda written at the call would capture this session and allocate a delegate per message
    private readonly Func<NetCellChange, bool> offerServerWrite;

    internal ClientSession(WindowManager windowManager, InputManager inputManager, RenderManager renderManager, SfxManager? sfx, LoadingScreenView loadingView, ChatView chatView, DevDiagnostics diagnostics)
    {
        this.windowManager = windowManager;
        this.inputManager = inputManager;
        this.renderManager = renderManager;
        this.sfx = sfx;
        this.loadingView = loadingView;
        this.chatView = chatView;
        this.diagnostics = diagnostics;
        offerServerWrite = change => prediction.Offer(change, null);
    }

    /// <summary>The connection, null in singleplayer, on the menu and after a disconnect.</summary>
    internal NetworkClient? Client => netClient;

    /// <summary>The waystone rows, the activation answer and the trip the server is handling for this player (empty
    /// while not connected).</summary>
    internal WaystoneMirror Waystones { get; } = new();

    /// <summary>The death screen's state for the running session: this connection's while connected, else the world's own (singleplayer).</summary>
    internal RespawnPrompt RespawnPromptOf(World world) =>
        netClient != null ? respawnPrompt : world.LocalPlayer.RespawnPrompt;

    /// <summary>
    /// The player chose RESPAWN on the death screen. Connected, it sends the intent (and the retry
    /// clock keeps asking while the server still reports the player dead); in singleplayer the
    /// world's next health tick carries the respawn out.
    /// </summary>
    internal void RequestRespawn(World world)
    {
        if (netClient == null)
        {
            world.LocalPlayer.RespawnPrompt.Request();
            return;
        }
        if (respawnPrompt.Request())
            SendRespawn(world.Camera);
    }


    /// <summary>Starts the connect + handshake on a background thread; the loading screen polls it.</summary>
    internal void BeginConnect(ConnectTarget target)
    {
        netClient?.Dispose();
        prediction = new ClientPrediction();
        netClient = new NetworkClient(target.Address, chatView.LocalName,
            NetworkConfig.NetConnectTimeoutSeconds);
        var client = netClient;
        new Thread(() => client.Connect()) { IsBackground = true, Name = "net-connect" }.Start();
    }

    /// <summary>
    /// The connecting screen's frame: waits for the handshake, then builds the client world at the
    /// server's spawn and lets the normal loading path take over. A failure returns to the server list
    /// with the reason.
    /// </summary>
    internal void UpdateConnecting(float deltaTime)
    {
        var client = netClient;
        if (client == null)
            return;

        // Read once: the connect and reader threads publish it concurrently. A server that closes
        // right after its hello reaches Disconnected before this screen ever saw Handshaken, which
        // is a failed connect too, not a reason to wait forever.
        var status = client.Status;
        if (status is NetworkStatus.Failed or NetworkStatus.Disconnected)
        {
            var reason = client.Error ?? (status == NetworkStatus.Disconnected
                ? "The server closed the connection"
                : "Could not connect");
            DisconnectNetwork();
            Program.AbortConnect(reason);
            return;
        }

        if (status != NetworkStatus.Handshaken)
        {
            loadingView.Update(0f, false, deltaTime);
            return;
        }

        var server = client.Server!.Value;

        // The client derives nothing from the seed (terrain, water levels and tint all stream from
        // the server), so it needs neither WorldSeed.Apply nor the seed lock.
        try
        {
            // A multiplayer client renders, so it is a real world with the renderer's pools and the
            // window - but client mode: no worldgen (chunks stream from the server), no far LOD and
            // no local simulation, so a custom or premade server world is exactly what is shown. It
            // starts where the server says and sinks onto the streamed terrain there.
            ActiveWorld = new World(renderManager.gl, renderManager.OpaqueChunkGeometry,
                renderManager.TranslucentChunkGeometry, renderManager.LodGeometry,
                windowManager.Window, inputManager.InputContext, sfx, null,
                serverMode: false, clientMode: true, spawn: server.Spawn)
            {
                Player =
                {
                    Rig = renderManager.Players.PlayerRig
                },
                // Every action is predicted locally and sent as an intent; the cells it changed are
                // tagged with that intent, so the server's answer to it is told from older messages
                BlockEdited = StagePredictedCell,
                IntentRaised = SendWorldIntent
            };
            renderManager.LightSource = ActiveWorld.GetChunkManager();
            var fb = windowManager.Window.FramebufferSize;
            if (fb.Y > 0)
                ActiveWorld.Camera.SetAspectRatio(fb.X / (float)fb.Y);

            clientStreamer = new ClientChunkStreamer(ActiveWorld.GetChunkManager(), SendChunkRequest, SendColumnRequest);
            clientLodStreamer = new ClientLodStreamer(ActiveWorld.GetLodManager(), SendLodRequest);
        }
        catch (Exception e)
        {
            Log.Warn($"Multiplayer: could not build the world: {e.Message}");
            DisconnectNetwork();
            Program.AbortConnect("Could not start the world - see the console");
            return;
        }

        // A client follows the server's day clock from the hello.
        ActiveWorld.SetLightingTime(server.Time);
        netPlayerId = server.PlayerId;
        Program.ResetWorldViews();
        loadingView.Begin(LoadingScreenKind.Loading, server.WorldName, "Loading world...");
        Log.WriteLine($"Multiplayer: connected to {client.Address} as '{chatView.LocalName}' (#{server.PlayerId}) on '{server.WorldName}'");
    }

    /// <summary>Sends periodics and drains every server message onto the world and the chat.
    /// False when a bad message dropped the connection and returned to the menu (the world is
    /// gone then, so the caller must stop the frame).</summary>
    internal bool UpdateNetwork(NetworkClient client, float deltaTime)
    {
        var w = ActiveWorld!;
        clientStreamer?.Update(w.Camera.Position, deltaTime);
        clientLodStreamer?.Update(w.Camera.Position, deltaTime);
        netStateTimer += deltaTime;
        if (netStateTimer >= NetworkConfig.NetPlayerStateSeconds)
        {
            netStateTimer = 0;
            var camera = w.Camera;
            using var writer = new PacketWriter(NetMessage.PlayerState);
            writer.Write(camera.Position);
            writer.Write(camera.Yaw);
            writer.Write(camera.Pitch);
            // The modes ride along as a request: the server decides whether this player may be
            // creative (and with it, whether its catalog grants are accepted)
            // The water pose follows the body's depth like the local model: a wader walks, a floater
            // takes the surface float, and both it and a submerged swimmer swim
            var waterPhase = camera.IsFlying ? WaterPhase.Dry : camera.WaterPhase;
            var supported = camera.IsGrounded || camera.IsFlying || waterPhase >= WaterPhase.Float;
            writer.Write((byte)((camera.IsCreative ? NetPlayerFlags.Creative : NetPlayerFlags.None)
                | (camera.IsFlying ? NetPlayerFlags.Flying : NetPlayerFlags.None)
                | (camera.IsSneaking ? NetPlayerFlags.Sneaking : NetPlayerFlags.None)
                | (waterPhase >= WaterPhase.Float ? NetPlayerFlags.Swimming : NetPlayerFlags.None)
                | (waterPhase == WaterPhase.Float ? NetPlayerFlags.Floating : NetPlayerFlags.None)
                | (camera.IsSprinting ? NetPlayerFlags.Sprinting : NetPlayerFlags.None)
                | (!supported ? NetPlayerFlags.Airborne : NetPlayerFlags.None)));
            writer.Write(netCorrectionAck);
            client.Send(writer.ToArray());
        }

        // A dead player comes back only through a Respawn intent, sent when the player chooses
        // RESPAWN on the death screen and asked again while the server still reports them dead
        respawnPrompt.Update((float)deltaTime, netDead);
        if (netDead && respawnPrompt.Requested)
        {
            netRespawnTimer += (float)deltaTime;
            if (netRespawnTimer >= NetworkConfig.NetRespawnRetrySeconds)
                SendRespawn(w.Camera);
        }

        netKeepAliveTimer += deltaTime;
        if (netKeepAliveTimer >= NetworkConfig.NetKeepAliveSeconds)
        {
            netKeepAliveTimer = 0;
            using var writer = new PacketWriter(NetMessage.KeepAlive);
            client.Send(writer.ToArray());
        }

        while (client.Inbound.TryDequeue(out var payload))
        {
            // A truncated or malformed message from the server must not crash the game: leave
            // the server and say why instead.
            try
            {
                HandleNetworkMessage(w, payload);
            }
            catch (Exception e) when (e is EndOfStreamException or IOException
                                          or InvalidDataException or FormatException
                                          or ArgumentException)
            {
                Log.Warn($"Multiplayer: bad message from the server: {e.Message}");
                DisconnectToMenu("The server sent bad data");
                return false;
            }
        }

        return true;
    }

    private void HandleNetworkMessage(World w, byte[] payload)
    {
        using var reader = new PacketReader(payload);
        switch (reader.Message)
        {
            case NetMessage.ChatMessage:
            {
                var message = ChatMessageCodec.Read(reader);
                chatView.AddMessage(message.Kind, message.Sender, message.Text, message.Link);
                break;
            }
            case NetMessage.TimeSync:
                w.SetLightingTime(reader.ReadDouble());
                break;
            case NetMessage.PlayerList:
            {
                var count = reader.ReadInt();
                remoteScratch.Clear();
                Span<byte> worn = stackalloc byte[VoxelEngine.Config.GearConfig.ArmorSlotCount];
                for (var i = 0; i < count; i++)
                {
                    var id = reader.ReadInt();
                    var name = reader.ReadString();
                    var eye = reader.ReadVector3();
                    var yaw = reader.ReadFloat();
                    var pitch = reader.ReadFloat();
                    var flags = (NetPlayerFlags)reader.ReadByte();
                    var action = (NetPlayerAction)reader.ReadByte();
                    var actionSeq = reader.ReadByte();
                    for (var slot = 0; slot < worn.Length; slot++) worn[slot] = reader.ReadByte();
                    remoteScratch.Add(new RemotePlayerSnapshot(id, name, eye, yaw, pitch, flags, action, actionSeq,
                        VoxelEngine.Player.ArmorLook.FromWire(worn)));
                }
                w.ClientMirror.SyncRemotePlayers(remoteScratch, netPlayerId);
                break;
            }
            case NetMessage.BlockCorrection:
            {
                // The server's state of the cells around an intent's target, after judging it: it
                // overrides the prediction that intent made, but never a newer one
                var seq = reader.ReadInt();
                ReadCellChanges(reader);
                netApplyScratch.Clear();
                foreach (var change in netCellsScratch.Where(change => prediction.Offer(change, seq)))
                    netApplyScratch.Add(change);
                w.ClientMirror.ApplyNetworkBlockChanges(netApplyScratch);
                break;
            }
            case NetMessage.IntentResult:
            {
                // The intent is settled either way: what was held back behind its prediction (the
                // newest value the server sent for those cells) lands now
                var seq = reader.ReadInt();
                var accepted = reader.ReadBool();
                netApplyScratch.Clear();
                prediction.Resolve(seq, netApplyScratch);
                w.ClientMirror.ApplyNetworkBlockChanges(netApplyScratch);
                if (Waystones.NoteResult(seq, accepted) && !accepted)
                    w.Toast.Show(WaystoneText.TripRefused);
                if (!accepted && diagnostics.DebugEnabled)
                    Log.WriteLine($"Multiplayer: the server refused intent #{seq}");
                break;
            }
            case NetMessage.StationState:
                w.ClientMirror.ApplyNetworkStations(reader);
                break;
            case NetMessage.PlayerStatus:
            {
                // The server owns this player's status: take the modes it granted, mirror its
                // health, let this client's physics apply a mob hit's shove, and ask to respawn
                // as soon as it reports the player dead
                var status = PlayerStatusCodec.Read(reader);
                w.Camera.SetModes(status.Modes.HasFlag(NetPlayerFlags.Creative),
                    status.Modes.HasFlag(NetPlayerFlags.Flying));
                var healthBefore = w.Health;
                w.ClientMirror.ApplyNetworkHealth(status.Health);
                w.ClientMirror.ApplyNetworkAir(status.Air);
                w.ClientMirror.ApplyNetworkMana(status.Mana, status.MaxMana);
                w.ClientMirror.ApplyNetworkEffects(status.EffectMask);
                w.ClientMirror.ApplyNetworkCaveBiome(status.CaveBiome);
                var knockback = status.Knockback;
                // AddKnockback normalizes the direction itself and keeps a lift-only hit's pop
                if (knockback.LengthSquared() > 0f || status.Lift > 0f)
                    w.Camera.AddKnockback(knockback, knockback.Length(), status.Lift);
                // A shove that came with lost health was a blow: the damage arc points back at it
                if (status.Health < healthBefore)
                    w.Camera.AddDamageDirection(knockback);
                netDead = status.Dead;
                break;
            }
            case NetMessage.PlayerCorrection:
            {
                // The server put this player back (an illegal move, or a respawn): snap there and
                // ack it, at once, so the server takes positions again from the next state
                var seq = reader.ReadInt();
                var eye = reader.ReadVector3();
                if (!float.IsFinite(eye.X) || !float.IsFinite(eye.Y) || !float.IsFinite(eye.Z))
                    throw new System.IO.InvalidDataException("player correction eye is not finite");
                w.Camera.TestTeleport(eye, w.Camera.Yaw, w.Camera.Pitch);
                netCorrectionAck = seq;
                netStateTimer = NetworkConfig.NetPlayerStateSeconds;
                // A waystone trip's landing: held there, facing away from the waystone, until this side's chunks are in
                if (Waystones.TryTakeLanding(out var destination))
                    w.WaystoneTravel.BeginLanding(eye, destination);
                break;
            }
            case NetMessage.ChunkData:
            {
                var cx = reader.ReadInt();
                var cy = reader.ReadInt();
                var cz = reader.ReadInt();
                clientStreamer?.ApplyChunk(cx, cy, cz, reader.ReadBytes());
                break;
            }
            case NetMessage.ColumnTerrain:
            {
                var cx = reader.ReadInt();
                var cz = reader.ReadInt();
                clientStreamer?.ApplyColumn(cx, cz, reader.ReadBytes());
                break;
            }
            case NetMessage.LodRegionData:
            {
                reader.ReadInt();
                reader.ReadInt();
                reader.ReadInt();
                clientLodStreamer?.Apply(reader.ReadBytes());
                break;
            }
            case NetMessage.EntityList:
            {
                var count = reader.ReadInt();
                netEntitiesScratch.Clear();
                for (var i = 0; i < count; i++)
                    netEntitiesScratch.Add(NetEntityCodec.Read(reader));
                w.ClientMirror.ApplyNetworkEntities(netEntitiesScratch);
                break;
            }
            case NetMessage.BlockChanges:
            {
                // The server's own writes (anyone's edits, blasts, flow, decay); a cell this client
                // predicted waits until the server has answered that prediction
                ReadCellChanges(reader);
                netApplyScratch.Clear();
                foreach (var change in netCellsScratch.Where(offerServerWrite))
                    netApplyScratch.Add(change);
                w.ClientMirror.ApplyNetworkBlockChanges(netApplyScratch);
                break;
            }
            case NetMessage.ChestContents:
                ApplyChestContents(w, reader);
                break;
            case NetMessage.WaystoneRows:
                WaystoneNetCodec.ReadRows(reader, waystoneRowsScratch);
                Waystones.Replace(waystoneRowsScratch);
                break;
            case NetMessage.WaystoneOpened:
                // The server discovered the waystone this player right-clicked: the waystone screen opens on it
                Waystones.Offer(WaystoneNetCodec.ReadOpened(reader));
                break;
            case NetMessage.InventoryState:
            {
                // A snapshot answering an older intent than the newest one that changed the
                // inventory here would undo that change for a moment: the newer answer follows
                var seq = reader.ReadInt();
                var data = reader.ReadBytes();
                if (prediction.AcceptsInventory(seq))
                    w.Inventory.Import(InventoryNetCodec.Decode(data));
                break;
            }
        }
    }

    /// <summary>Reads a count-prefixed batch of cell changes into <c>netCellsScratch</c>.</summary>
    private void ReadCellChanges(PacketReader reader)
    {
        var count = reader.ReadInt();
        if (count < 0)
            throw new InvalidDataException($"cell batch of {count}");
        netCellsScratch.Clear();
        for (var i = 0; i < count; i++)
        {
            var x = reader.ReadInt();
            var y = reader.ReadInt();
            var z = reader.ReadInt();
            var blockId = reader.ReadByte();
            var state = reader.ReadByte();
            netCellsScratch.Add(new NetCellChange(x, y, z, blockId, state));
        }
    }

    /// <summary>Asks the server for one chunk (a streamed client loads no chunks itself).</summary>
    private void SendChunkRequest(int cx, int cy, int cz)
    {
        if (netClient is not { Status: NetworkStatus.Handshaken } client)
            return;
        using var writer = new PacketWriter(NetMessage.ChunkRequest);
        writer.Write(cx);
        writer.Write(cy);
        writer.Write(cz);
        client.Send(writer.ToArray());
    }

    /// <summary>Asks the server for one chunk column's terrain heights (the client's light seeding).</summary>
    private void SendColumnRequest(int cx, int cz)
    {
        if (netClient is not { Status: NetworkStatus.Handshaken } client)
            return;
        using var writer = new PacketWriter(NetMessage.ColumnRequest);
        writer.Write(cx);
        writer.Write(cz);
        client.Send(writer.ToArray());
    }

    /// <summary>Asks the server to build and send one far-LOD region (a client builds none itself).</summary>
    private void SendLodRequest(int level, int rx, int rz)
    {
        if (netClient is not { Status: NetworkStatus.Handshaken } client)
            return;
        using var writer = new PacketWriter(NetMessage.LodRegionRequest);
        writer.Write(level);
        writer.Write(rx);
        writer.Write(rz);
        client.Send(writer.ToArray());
    }

    /// <summary>
    /// Sends one inventory intent while connected and returns whether it went out. The server
    /// replays it against this player's inventory there and answers with the snapshot this client
    /// then imports as the truth, so a local change made optimistically alongside the intent stays
    /// only if the server agreed. False (nothing sent) in singleplayer.
    /// </summary>
    internal bool SendInventoryIntent(InventoryIntent intent)
    {
        if (netClient is not { Status: NetworkStatus.Handshaken } client)
            return false;
        using var writer = new PacketWriter(NetMessage.InventoryRequest);
        // Every inventory intent may change the inventory here, so snapshots answering an older
        // intent are dropped until this one's answer arrives
        writer.Write(prediction.Commit(predictsInventory: true));
        InventoryIntentCodec.Write(writer, intent);
        client.Send(writer.ToArray());
        return true;
    }

    /// <summary>Asks the server to open the chest at a cell; false when not connected. The answer is a
    /// <see cref="NetMessage.ChestContents"/>.</summary>
    internal bool SendChestOpen(int x, int y, int z)
    {
        if (netClient is not { Status: NetworkStatus.Handshaken } client) return false;
        using var writer = new PacketWriter(NetMessage.ChestOpen);
        ChestNetCodec.WriteOpen(writer, x, y, z);
        client.Send(writer.ToArray());
        return true;
    }

    /// <summary>Sends one move against the chest this player has open; false when not connected. Nothing is predicted:
    /// the panel shows what the server answers.</summary>
    internal bool SendChestMove(VoxelEngine.Simulation.Chests.ChestMove move)
    {
        if (netClient is not { Status: NetworkStatus.Handshaken } client) return false;
        using var writer = new PacketWriter(NetMessage.ChestMove);
        ChestNetCodec.WriteMove(writer, move);
        client.Send(writer.ToArray());
        return true;
    }

    /// <summary>Tells the server this player closed its chest panel; false when not connected.</summary>
    internal bool SendChestClose()
    {
        if (netClient is not { Status: NetworkStatus.Handshaken } client) return false;
        using var writer = new PacketWriter(NetMessage.ChestClose);
        client.Send(writer.ToArray());
        return true;
    }

    // The server's answer for the open chest: the contents the panel shows, or the closed answer, which takes the
    // chest part of the panel down (a snapshot of a chest the panel is no longer on is stale and ignored)
    private static void ApplyChestContents(World w, PacketReader reader)
    {
        var answer = ChestNetCodec.ReadContents(reader);
        if (answer is not { } open)
        {
            w.ChestPanel.Close();
            return;
        }
        if (w.ChestPanel is { IsOpen: true } panel && panel.Cell == open.Cell)
            panel.SetContents(open.Contents);
    }

    /// <summary>Asks the server to rename the waystone at a cell (only its first discoverer may); false when not
    /// connected. The answer is fresh <see cref="NetMessage.WaystoneRows"/>.</summary>
    internal bool SendWaystoneRename((int X, int Y, int Z) cell, string name)
    {
        if (netClient is not { Status: NetworkStatus.Handshaken } client) return false;
        using var writer = new PacketWriter(NetMessage.WaystoneRename);
        WaystoneNetCodec.WriteRename(writer, cell, name);
        client.Send(writer.ToArray());
        return true;
    }

    /// <summary>A cell the world just changed as part of a predicted action (World.BlockEdited).</summary>
    private void StagePredictedCell(int x, int y, int z, byte blockId, byte state) =>
        prediction.Stage(x, y, z);

    /// <summary>
    /// Sends one world intent (World.IntentRaised) with the next sequence id, which also tags the
    /// cells the action predicted and, for an intent whose prediction spends or credits items, the
    /// inventory. Dropped (the prediction is rubberbanded by nothing) when not connected.
    /// </summary>
    private void SendWorldIntent(WorldIntent intent)
    {
        if (netClient is not { Status: NetworkStatus.Handshaken } client)
            return;
        using var writer = new PacketWriter(NetMessage.WorldIntent);
        var seq = prediction.Commit(intent.PredictsInventory);
        WorldIntentCodec.Write(writer, seq, intent);
        client.Send(writer.ToArray());
        // A waystone trip lands with a correction later; the session watches for it
        if (intent.Kind == NetIntentKind.Waystone)
            Waystones.NoteTripSent(seq, (intent.X, intent.Y, intent.Z));
    }

    /// <summary>
    /// Asks the server to respawn this (dead) player and restarts the retry clock. The server
    /// answers with a correction to the respawn point and a status that no longer reads dead.
    /// </summary>
    private void SendRespawn(Camera camera)
    {
        netRespawnTimer = 0f;
        SendWorldIntent(WorldIntent.Respawn(camera.Position, camera.LastAimDirection));
    }

    /// <summary>Tears the connection down and returns to the server list with a notice.</summary>
    internal void DisconnectToMenu(string notice)
    {
        DisconnectNetwork();
        // A client world is storeless; dispose it so its workers stop before the menu.
        if (CurrentWorld == null && ActiveWorld != null)
        {
            var clientWorld = ActiveWorld;
            ActiveWorld = null;
            clientWorld.SaveAndDispose(renderManager);
        }
        Program.ResetWorldViews();
        Program.AbortConnect(notice);
    }

    internal void DisconnectNetwork()
    {
        netClient?.Dispose();
        netClient = null;
        clientStreamer = null;
        clientLodStreamer = null;
        netPlayerId = -1;
        netStateTimer = 0;
        netKeepAliveTimer = 0;
        netCorrectionAck = 0;
        netDead = false;
        netRespawnTimer = 0f;
        respawnPrompt.Reset();
        prediction = new ClientPrediction();
        Waystones.Clear();
        if (ActiveWorld == null) return;
        ActiveWorld.ClientMirror.ClearRemotePlayers();
        ActiveWorld.BlockEdited = null;
        ActiveWorld.IntentRaised = null;
    }

    /// <summary>Sends one chat line: a local echo in singleplayer, the server in multiplayer
    /// (which broadcasts it back to every client, so it is never echoed twice).</summary>
    internal void SendChat(string text)
    {
        if (netClient is { Status: NetworkStatus.Handshaken } client)
        {
            using var writer = new PacketWriter(NetMessage.ChatSend);
            writer.Write(text);
            client.Send(writer.ToArray());
            return;
        }

        chatView.AddMessage(ChatKind.Player, chatView.LocalName, text);
    }
}
