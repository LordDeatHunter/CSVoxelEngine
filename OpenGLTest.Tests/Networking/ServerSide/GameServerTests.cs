using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using VoxelEngine.Audio;
using VoxelEngine.Commands;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Entities.CaveMobs;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Entities.SkyMobs;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Content.Spells;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Magic;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Player;
using VoxelEngine.Saves;
using VoxelEngine.Server;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.Status;
using VoxelEngine.Tests.Magic;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen.Structures;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.Networking.ServerSide;

/// <summary>
/// A real <see cref="GameServer"/> over loopback: a malformed frame drops only its sender, chunks
/// stream around each moving player while every joiner still spawns at the world spawn, far-LOD requests
/// are built a bounded number per Pump with a repeated request served once, and every player's
/// inventory is its own: intents are replayed against the sender's inventory only, refused ones
/// are corrected by the snapshot the sender gets back, and creative grants need the server's
/// creative flag, which a server that does not allow creative for everyone grants its operators
/// only. World intents are judged by the server for the sending player: a break needs the
/// tool the block asks for and the time its hits take, a placement spends exactly one owned block
/// under the placement rules with a state the server computes, a raw cell write is refused, a
/// blast one player set off reaches every client, and mined loot lands with the breaker. A player's
/// status is the server's: a joiner is told its health, and a dead player acts on nothing until a
/// respawn intent, judged ahead of the eye rule, brings it home. So is its position: a move into a
/// solid cell is corrected and the client's states are ignored until it acks the correction, while
/// a legal walk is never corrected.
/// </summary>
[Collection("Worldgen seed")]
public sealed class GameServerTests : IDisposable
{
    private readonly World _world;
    private readonly GameServer _server;
    private readonly Dictionary<NetworkClient, Inbox> _inboxes = new(ReferenceEqualityComparer.Instance);

    public GameServerTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, serverMode: true);
        _server = new GameServer(_world, "Test World", WorldSeed.Default, 0);
        _server.Start();
    }

    public void Dispose()
    {
        _server.Dispose();
        _world.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>What one test client received, decoded as it arrives.</summary>
    private sealed class Inbox
    {
        public int Seq;
        public PlayerSaveData.InventoryData? Inventory;
        public int InventorySeq;
        public bool FreshInventory;
        public readonly Dictionary<int, bool> Results = new();
        public readonly List<(int Seq, NetCellChange Cell)> Corrections = [];
        public readonly List<NetCellChange> Broadcast = [];
        public NetPlayerFlags? Status;
        public bool Operator;
        public int? Health;
        public bool Dead;
        public byte EffectMask;
        public readonly List<(int Seq, Vector3 Eye)> PlayerCorrections = [];

        /// <summary>The newest <see cref="NetMessage.EntityList"/> received, decoded.</summary>
        public List<NetEntity>? Entities;

        /// <summary>Each listed player's movement flags from the newest <see cref="NetMessage.PlayerList"/>, by name.</summary>
        public readonly Dictionary<string, NetPlayerFlags> ListedFlags = new();

        /// <summary>The newest <see cref="NetMessage.StationState"/> payload received, raw (message byte included).</summary>
        public byte[]? Stations;

        /// <summary>Every chat line received, decoded by the client's own reader.</summary>
        public readonly List<ChatMessage> Chat = [];

        /// <summary>Every world sound received, decoded by the client's own reader.</summary>
        public readonly List<SoundEvent> Sounds = [];

        public void Drain(NetworkClient client)
        {
            while (client.Inbound.TryDequeue(out var payload))
            {
                using var reader = new PacketReader(payload);
                switch (reader.Message)
                {
                    case NetMessage.InventoryState:
                        InventorySeq = reader.ReadInt();
                        Inventory = InventoryNetCodec.Decode(reader.ReadBytes());
                        FreshInventory = true;
                        break;
                    case NetMessage.IntentResult:
                    {
                        var seq = reader.ReadInt();
                        Results[seq] = reader.ReadBool();
                        break;
                    }
                    case NetMessage.BlockCorrection:
                    {
                        var seq = reader.ReadInt();
                        foreach (var cell in ReadCells(reader))
                            Corrections.Add((seq, cell));
                        break;
                    }
                    case NetMessage.BlockChanges:
                        Broadcast.AddRange(ReadCells(reader));
                        break;
                    case NetMessage.PlayerStatus:
                    {
                        var status = PlayerStatusCodec.Read(reader);
                        Status = status.Modes;
                        Operator = status.Operator;
                        Health = status.Health;
                        Dead = status.Dead;
                        EffectMask = status.EffectMask;
                        break;
                    }
                    case NetMessage.PlayerCorrection:
                    {
                        var seq = reader.ReadInt();
                        PlayerCorrections.Add((seq, reader.ReadVector3()));
                        break;
                    }
                    case NetMessage.EntityList:
                    {
                        // The client's own reader (ClientSession's EntityList case)
                        var count = reader.ReadInt();
                        var entities = new List<NetEntity>(count);
                        for (var i = 0; i < count; i++)
                            entities.Add(NetEntityCodec.Read(reader));
                        Entities = entities;
                        break;
                    }
                    case NetMessage.PlayerList:
                    {
                        // The record shape PlayerSessions.BroadcastPlayerList writes
                        var count = reader.ReadInt();
                        for (var i = 0; i < count; i++)
                        {
                            reader.ReadInt();
                            var name = reader.ReadString();
                            reader.ReadVector3();
                            reader.ReadFloat();
                            reader.ReadFloat();
                            ListedFlags[name] = (NetPlayerFlags)reader.ReadByte();
                            reader.ReadByte();
                            reader.ReadByte();
                            for (var slot = 0; slot < GearConfig.ArmorSlotCount; slot++)
                                reader.ReadId();
                            reader.ReadId();
                            reader.ReadBool();
                        }
                        break;
                    }
                    case NetMessage.StationState:
                        Stations = payload;
                        break;
                    case NetMessage.ChatMessage:
                        Chat.Add(ChatMessageCodec.Read(reader));
                        break;
                    case NetMessage.SoundEvents:
                    {
                        var sounds = new List<SoundEvent>();
                        SoundEventCodec.Read(reader, sounds);
                        Sounds.AddRange(sounds);
                        break;
                    }
                }
            }
        }

        private static List<NetCellChange> ReadCells(PacketReader reader)
        {
            var count = reader.ReadInt();
            var cells = new List<NetCellChange>(count);
            for (var i = 0; i < count; i++)
                cells.Add(NetCellChangeCodec.Read(reader));
            return cells;
        }

        public Inventory Mirror()
        {
            Assert.NotNull(Inventory);
            var mirror = VoxelEngine.UI.InventoryPanel.Inventory.Empty();
            mirror.Import(Inventory!);
            return mirror;
        }
    }

    private Inbox InboxOf(NetworkClient client)
    {
        if (!_inboxes.TryGetValue(client, out var inbox))
            _inboxes[client] = inbox = new Inbox();
        return inbox;
    }

    private void DrainAll()
    {
        foreach (var (client, inbox) in _inboxes)
            inbox.Drain(client);
    }

    private NetworkClient Connect(string name)
    {
        var client = new NetworkClient(new ServerAddress("127.0.0.1", _server.BoundPort), name, 5.0);
        new Thread(client.Connect) { IsBackground = true, Name = "test-connect" }.Start();
        var sw = Stopwatch.StartNew();
        while (client.Status == NetworkStatus.Connecting && sw.Elapsed.TotalSeconds < 15)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
        }

        Assert.Equal(NetworkStatus.Handshaken, client.Status);
        InboxOf(client);
        // A newcomer holds the starter kit's hint scroll; these flows start from an empty hand
        var joined = PlayerOf(client);
        joined.Inventory.RemoveAll(ItemRegistry.RequireItem(StarterKit.HintScrollName).Id);
        joined.InventoryChanged = true;
        return client;
    }

    private void PumpFor(double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
        }
    }

    /// <summary>One server frame with the world simulating, as the dedicated server runs it.</summary>
    private void Tick(double dt)
    {
        _world.Update(dt, null);
        _server.Pump(dt);
        Thread.Sleep(2);
        DrainAll();
    }

    private void SendIntent(NetworkClient client, InventoryIntent intent)
    {
        using var writer = new PacketWriter(NetMessage.InventoryRequest);
        writer.Write(++InboxOf(client).Seq);
        InventoryIntentCodec.Write(writer, intent);
        client.Send(writer.ToArray());
    }

    private int SendWorld(NetworkClient client, WorldIntent intent)
    {
        var seq = ++InboxOf(client).Seq;
        using var writer = new PacketWriter(NetMessage.WorldIntent);
        WorldIntentCodec.Write(writer, seq, intent);
        client.Send(writer.ToArray());
        return seq;
    }

    /// <summary>Ticks (the world too) until the server answered intent <paramref name="seq"/>.</summary>
    private bool AwaitResult(NetworkClient client, int seq, double dt = 0.01)
    {
        var inbox = InboxOf(client);
        var sw = Stopwatch.StartNew();
        while (!inbox.Results.ContainsKey(seq) && sw.Elapsed.TotalSeconds < 15)
            Tick(dt);
        Assert.True(inbox.Results.ContainsKey(seq), $"intent #{seq} was never answered");
        return inbox.Results[seq];
    }

    // A PlayerState standing at the join spawn (so no move is clamped), with the given mode request
    private static void SendModes(NetworkClient client, NetPlayerFlags flags) =>
        SendState(client, client.Server!.Value.Spawn, flags);

    // A PlayerState at eye with the given mode request, acking the newest correction applied
    private static void SendState(NetworkClient client, Vector3 eye, NetPlayerFlags flags, int correctionAck = 0)
    {
        using var writer = new PacketWriter(NetMessage.PlayerState);
        writer.Write(eye);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write((byte)flags);
        writer.Write(correctionAck);
        client.Send(writer.ToArray());
    }

    /// <summary>
    /// Pumps until <paramref name="client"/> has received an inventory snapshot it has not looked
    /// at yet (each intent is answered by one) and returns the newest as the mirror the client imports.
    /// </summary>
    private Inventory AwaitInventory(NetworkClient client)
    {
        var inbox = InboxOf(client);
        var sw = Stopwatch.StartNew();
        while (!inbox.FreshInventory && sw.Elapsed.TotalSeconds < 15)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
            inbox.Drain(client);
        }

        Assert.True(inbox.FreshInventory, "no inventory snapshot arrived");
        inbox.FreshInventory = false;
        return inbox.Mirror();
    }

    private ServerPlayer PlayerOf(NetworkClient client)
    {
        Assert.True(_server.TryGetPlayer(client.Server!.Value.PlayerId, out var player));
        return player;
    }

    private static ushort Stone => ContentRegistry.RequireBlock("stone").Id;
    private static ushort Dirt => ContentRegistry.RequireBlock("dirt").Id;

    /// <summary>
    /// A cleared pocket around the spawn eye: a dirt floor one row under the feet and air for four
    /// rows above it, seven blocks across, so every aim below is a clean line of sight. Ticks the
    /// world until the pocket's chunks have streamed in.
    /// </summary>
    private sealed record Arena(Vector3 Eye, int X, int Y, int Z)
    {
        /// <summary>The cell at eye height two blocks along +X, and the aim at its center.</summary>
        public Vector3 Front => new(X + 2, Y + 1, Z);
        public Vector3 AimAtFront => Vector3.Normalize(Front + new Vector3(0.5f) - Eye);

        /// <summary>The floor cell under <see cref="Front"/>'s column, and an aim landing on its top face.</summary>
        public Vector3 FloorAhead => new(X + 2, Y - 1, Z);
        public Vector3 AimAtFloorAhead =>
            Vector3.Normalize(new Vector3(X + 2.5f, Y - 0.001f, Z + 0.5f) - Eye);

        /// <summary>The floor cell under the player's own feet, aimed at straight down.</summary>
        public Vector3 FloorBelow => new(X, Y - 1, Z);
    }

    private Arena PrepareArena(Vector3 eye)
    {
        var manager = _world.GetChunkManager();
        // The feet row, robust to the eye height's float rounding
        var fx = (int)MathF.Floor(eye.X);
        var fy = (int)MathF.Floor(eye.Y - Camera.EyeHeight + 1e-3f);
        var fz = (int)MathF.Floor(eye.Z);

        bool Loaded()
        {
            for (var x = fx - 3; x <= fx + 3; x++)
            for (var y = fy - 1; y <= fy + 4; y++)
            for (var z = fz - 3; z <= fz + 3; z++)
                if (!manager.TryGetCell(x, y, z, out _, out _))
                    return false;
            return true;
        }

        var sw = Stopwatch.StartNew();
        while (!Loaded() && sw.Elapsed.TotalSeconds < 60)
            Tick(0.05);
        Assert.True(Loaded(), "the arena's chunks never loaded");

        for (var x = fx - 3; x <= fx + 3; x++)
        for (var y = fy - 1; y <= fy + 4; y++)
        for (var z = fz - 3; z <= fz + 3; z++)
            SetCell(new Vector3(x, y, z), y == fy - 1 ? Dirt : BlockType.Air);
        return new Arena(eye, fx, fy, fz);
    }

    private void SetCell(Vector3 cell, ushort id, byte state = 0)
    {
        var chunk = _world.GetChunkManager().ChunkAt((int)cell.X, (int)cell.Y, (int)cell.Z);
        Assert.NotNull(chunk);
        var (lx, ly, lz) = VoxelCoords.LocalOf((int)cell.X, (int)cell.Y, (int)cell.Z);
        chunk!.SetBlockRaw(lx, ly, lz, id, state);
    }

    private (ushort Id, byte State) CellAt(Vector3 cell)
    {
        Assert.True(_world.GetChunkManager().TryGetCell((int)cell.X, (int)cell.Y, (int)cell.Z, out var id, out var state));
        return (id, state);
    }

    /// <summary>Grants <paramref name="id"/> through a creative request and puts it in hand (a grant never lands on
    /// the hotbar by itself), then drops back to survival.</summary>
    private void GrantAsSurvival(NetworkClient client, ushort id, int count)
    {
        _server.AllowCreative = true;
        SendModes(client, NetPlayerFlags.Creative);
        SendIntent(client, InventoryIntent.CreativeGrant(id, count));
        AwaitInventory(client);
        SendIntent(client, InventoryIntent.PickBlock(id));
        AwaitInventory(client);
        SendModes(client, NetPlayerFlags.None);
        PumpFor(0.05);
        Assert.False(PlayerOf(client).Creative);
    }

    /// <summary>Seconds of server time the bare hand needs to mine <paramref name="block"/>.</summary>
    private static double BareHandSeconds(ushort block) =>
        BreakProgress.RequiredSeconds(Mining.HardnessOf(block, 0), Mining.BlockDamage(null), Mining.BlockHitGapSeconds(null));

    [Fact]
    public void EachPlayerHasAnInventoryOfItsOwn()
    {
        _server.AllowCreative = true;
        using var first = Connect("First");
        using var second = Connect("Second");
        AwaitInventory(first);
        AwaitInventory(second);

        SendModes(first, NetPlayerFlags.Creative);
        SendIntent(first, InventoryIntent.CreativeGrant(Stone, 5));
        var firstMirror = AwaitInventory(first);

        // The grant landed in the sender's inventory, and its mirror is exactly the server's copy
        Assert.Equal(5, PlayerOf(first).Inventory.CountFor(Stone));
        Assert.Equal(PlayerOf(first).Inventory.CountFor(Stone), firstMirror.CountFor(Stone));
        Assert.Equal(PlayerOf(first).Inventory.IdAt(PlayerOf(first).Inventory.SelectedIndex),
            firstMirror.IdAt(firstMirror.SelectedIndex));

        // ...and nowhere else: not the other player, not the world's own inventory
        Assert.Equal(0, PlayerOf(second).Inventory.CountFor(Stone));
        Assert.Equal(0, _world.Inventory.CountFor(Stone));

        // The other player's own intent answers with its own (still empty) inventory
        SendIntent(second, InventoryIntent.Select(3));
        var secondMirror = AwaitInventory(second);
        Assert.Equal(0, secondMirror.CountFor(Stone));
        Assert.Equal(3, secondMirror.SelectedIndex);
        Assert.NotEqual(3, PlayerOf(first).Inventory.SelectedIndex);
    }

    [Fact]
    public void EquippingSomethingNotOwnedIsRefusedAndTheSnapshotCorrectsIt()
    {
        using var client = Connect("Tester");
        AwaitInventory(client);

        // The client equipped stone optimistically; the server owns none of it for this player
        SendIntent(client, InventoryIntent.EquipStackable(2, Stone));
        var mirror = AwaitInventory(client);
        Assert.Equal(0, mirror.IdAt(2));
        Assert.Equal(0, PlayerOf(client).Inventory.IdAt(2));

        // Nor a tool instance that does not exist
        SendIntent(client, InventoryIntent.EquipTool(2, 12345));
        mirror = AwaitInventory(client);
        Assert.Equal(0, mirror.IdAt(2));

        // Once the player owns it, the same equip goes through
        _server.AllowCreative = true;
        SendModes(client, NetPlayerFlags.Creative);
        SendIntent(client, InventoryIntent.CreativeGrant(Stone, 1));
        AwaitInventory(client);
        SendIntent(client, InventoryIntent.EquipStackable(2, Stone));
        mirror = AwaitInventory(client);
        Assert.Equal(Stone, mirror.IdAt(2));
    }

    [Fact]
    public void CreativeGrantsNeedTheServersCreativeFlag()
    {
        using var client = Connect("Tester");
        AwaitInventory(client);

        // A client asking for creative on a server that does not allow it stays survival, and is
        // told so
        _server.AllowCreative = false;
        SendModes(client, NetPlayerFlags.Creative | NetPlayerFlags.Flying);
        SendIntent(client, InventoryIntent.CreativeGrant(Stone, 5));
        Assert.Equal(0, AwaitInventory(client).CountFor(Stone));
        Assert.False(PlayerOf(client).Creative);
        Assert.Equal(NetPlayerFlags.None, InboxOf(client).Status);

        // Allowed, but this player never asked: still refused
        _server.AllowCreative = true;
        SendModes(client, NetPlayerFlags.None);
        SendIntent(client, InventoryIntent.CreativeGrant(Stone, 5));
        Assert.Equal(0, AwaitInventory(client).CountFor(Stone));

        // Allowed and asked for: granted, and only then may the catalog clear it again
        SendModes(client, NetPlayerFlags.Creative);
        SendIntent(client, InventoryIntent.CreativeGrant(Stone, 5));
        Assert.Equal(5, AwaitInventory(client).CountFor(Stone));

        SendModes(client, NetPlayerFlags.None);
        SendIntent(client, InventoryIntent.CreativeClear(Stone));
        Assert.Equal(5, AwaitInventory(client).CountFor(Stone));

        SendModes(client, NetPlayerFlags.Creative);
        SendIntent(client, InventoryIntent.CreativeClear(Stone));
        Assert.Equal(0, AwaitInventory(client).CountFor(Stone));
    }

    [Fact]
    public void AnOperatorMayBeCreativeWhileOthersAreRefused()
    {
        _server.AllowCreative = false;
        _server.Operators.Add("Op");
        using var op = Connect("Op");
        using var guest = Connect("Guest");
        Assert.True(_server.MayBeCreative("Op"));
        Assert.False(_server.MayBeCreative("Guest"));

        // Both are told their status at join; forget it, so a status from here on can only be the
        // answer to the mode request
        foreach (var client in new[] { op, guest })
        {
            var inbox = InboxOf(client);
            var sw = Stopwatch.StartNew();
            while (inbox.Health == null && sw.Elapsed.TotalSeconds < 15)
            {
                _server.Pump(0.01);
                Thread.Sleep(5);
                inbox.Drain(client);
            }
            Assert.NotNull(inbox.Health);
            inbox.Status = null;
        }

        // Both ask for creative flight and a creative grant; the state is handled before the grant
        SendModes(op, NetPlayerFlags.Creative | NetPlayerFlags.Flying);
        SendModes(guest, NetPlayerFlags.Creative | NetPlayerFlags.Flying);
        SendIntent(op, InventoryIntent.CreativeGrant(Stone, 5));
        var opGrant = InboxOf(op).Seq;
        SendIntent(guest, InventoryIntent.CreativeGrant(Stone, 5));
        var guestGrant = InboxOf(guest).Seq;
        var opAccepted = AwaitResult(op, opGrant);
        var guestAccepted = AwaitResult(guest, guestGrant);

        // The operator has what it asked for, and was told nothing (there was nothing to refuse)
        Assert.True(PlayerOf(op).Creative);
        Assert.True(PlayerOf(op).Flying);
        Assert.Null(InboxOf(op).Status);
        Assert.True(opAccepted);
        Assert.Equal(5, PlayerOf(op).Inventory.CountFor(Stone));

        // The guest stays in survival, is told so, and its grant is refused
        Assert.False(PlayerOf(guest).Creative);
        Assert.False(PlayerOf(guest).Flying);
        Assert.Equal(NetPlayerFlags.None, InboxOf(guest).Status);
        Assert.False(guestAccepted);
        Assert.Equal(0, PlayerOf(guest).Inventory.CountFor(Stone));
        Assert.Equal(0, InboxOf(guest).Mirror().CountFor(Stone));
    }

    // Sends one chat line from client
    private static void SendLine(NetworkClient client, string text)
    {
        using var chat = new PacketWriter(NetMessage.ChatSend);
        chat.Write(text);
        client.Send(chat.ToArray());
    }

    // Pumps until client holds a system line matching the predicate, and returns the first
    private ChatMessage AwaitSystemLine(NetworkClient client, Func<ChatMessage, bool> matches)
    {
        var inbox = InboxOf(client);
        var sw = Stopwatch.StartNew();
        while (!inbox.Chat.Any(m => m.Kind == ChatKind.System && matches(m)) && sw.Elapsed.TotalSeconds < 15)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
            inbox.Drain(client);
        }
        var line = inbox.Chat.FirstOrDefault(m => m.Kind == ChatKind.System && matches(m));
        Assert.True(line.Kind == ChatKind.System && matches(line), "the expected system line never arrived");
        return line;
    }

    [Fact]
    public void ALocateCommandAnswersItsSenderWithATeleportLink()
    {
        // Not an operator: the default server allows creative to nobody, and /locate needs no rights
        using var client = Connect("Seeker");
        SendLine(client, "/locate tower");

        var answer = AwaitSystemLine(client, m => m.Link != null);
        var parts = answer.Link!.Value.Command.Split(' ');
        Assert.Equal("/tp", parts[0]);
        var landX = (int)MathF.Floor(float.Parse(parts[1], CultureInfo.InvariantCulture));
        var landZ = (int)MathF.Floor(float.Parse(parts[3], CultureInfo.InvariantCulture));

        // Searched from where the sender stands, so it lands beside the tower nearest that player
        var eye = PlayerOf(client).Position;
        Assert.True(StructureLocator.TryFindTower((int)MathF.Floor(eye.X), (int)MathF.Floor(eye.Z),
            WorldGenerationConfig.TowerLocateRings, out var location));
        Assert.Equal((location.LandX, location.LandZ), (landX, landZ));
    }

    [Fact]
    public void AnOperatorCommandIsRefusedToAPlayerWhoIsNoOperator()
    {
        using var guest = Connect("Guest");
        PumpFor(0.1);
        DrainAll();
        Assert.False(InboxOf(guest).Operator);

        SendLine(guest, "/give stone 5");

        AwaitSystemLine(guest, m => m.Text == CommandDispatcher.OperatorsOnly);
        Assert.Equal(0, PlayerOf(guest).Inventory.CountFor(Stone));
    }

    [Fact]
    public void AnOperatorTeleportsItselfToAnotherPlayer()
    {
        _server.Operators.Add("Op");
        using var op = Connect("Op");
        using var other = Connect("Other");
        PumpFor(0.1);
        DrainAll();
        Assert.True(InboxOf(op).Operator);
        Assert.False(InboxOf(other).Operator);
        PlayerOf(other).Teleport(PlayerOf(op).Position + new Vector3(40f, 3f, -25f));

        SendLine(op, "/tp Other");

        AwaitSystemLine(op, m => m.Text.StartsWith("Teleported", StringComparison.Ordinal));
        Assert.True(Vector3.Distance(PlayerOf(op).Position, PlayerOf(other).Position) < 0.01f);
        // The client is told where it now stands
        Assert.Contains(InboxOf(op).PlayerCorrections, c => Vector3.Distance(c.Eye, PlayerOf(other).Position) < 0.01f);
    }

    [Fact]
    public void GameModeGivesCreativeOnlyToAPlayerWhoMayBeCreative()
    {
        _server.Operators.Add("Op");
        using var op = Connect("Op");
        using var guest = Connect("Guest");

        SendLine(op, "/gamemode creative Guest");
        AwaitSystemLine(op, m => m.Text.Contains("may not be creative", StringComparison.Ordinal));
        Assert.False(PlayerOf(guest).Creative);

        SendLine(op, "/gamemode creative");
        AwaitSystemLine(op, m => m.Text.Contains("now in creative", StringComparison.Ordinal));
        Assert.True(PlayerOf(op).Creative);
        // The client learns its new mode from its status
        Assert.Equal(NetPlayerFlags.Creative, InboxOf(op).Status);
    }

    [Fact]
    public void CraftingSpendsOnlyTheCraftingPlayersIngredients()
    {
        var (recipe, members, grants) = FirstGrantableRecipe();
        _server.AllowCreative = true;
        using var crafter = Connect("Crafter");
        using var bystander = Connect("Bystander");
        AwaitInventory(crafter);
        AwaitInventory(bystander);

        foreach (var client in new[] { crafter, bystander })
        {
            SendModes(client, NetPlayerFlags.Creative);
            foreach (var (id, count) in grants)
            {
                SendIntent(client, InventoryIntent.CreativeGrant(id, count));
                AwaitInventory(client);
            }
        }

        SendIntent(crafter, InventoryIntent.Craft(recipe.Name, 1, members));
        var mirror = AwaitInventory(crafter);

        var crafted = PlayerOf(crafter).Inventory;
        var untouched = PlayerOf(bystander).Inventory;
        Assert.True(crafted.CountFor(recipe.ResultId) > untouched.CountFor(recipe.ResultId),
            "the crafter got no result");
        foreach (var (id, _) in grants)
            Assert.True(crafted.CountFor(id) < untouched.CountFor(id), $"the crafter spent no {id}");
        Assert.Equal(crafted.CountFor(recipe.ResultId), mirror.CountFor(recipe.ResultId));

        // A craft fed with a member the ingredient does not accept is refused and spends nothing
        var wrong = (ushort[])members.Clone();
        wrong[0] = 1;
        while (recipe.Ingredients[0].Accepts(wrong[0]))
            wrong[0]++;
        SendIntent(bystander, InventoryIntent.Craft(recipe.Name, 1, wrong));
        AwaitInventory(bystander);
        foreach (var (id, count) in grants)
            Assert.Equal(count, untouched.CountFor(id));
        Assert.Equal(0, untouched.CountFor(recipe.ResultId));
    }

    /// <summary>
    /// A recipe a creative player can afford from scratch: every ingredient fed with its first
    /// accepted member, the result not itself an ingredient, and one craft fitting an inventory that
    /// holds exactly the granted ingredients.
    /// </summary>
    private static (CraftingRecipe Recipe, ushort[] Members, List<(ushort Id, int Count)> Grants) FirstGrantableRecipe()
    {
        foreach (var recipe in ContentRegistry.CraftingRecipes)
        {
            var members = new ushort[recipe.Ingredients.Length];
            var totals = new Dictionary<ushort, int>();
            for (var i = 0; i < members.Length; i++)
            {
                var ingredient = recipe.Ingredients[i];
                members[i] = ingredient.AcceptedIds[0];
                totals[members[i]] = totals.GetValueOrDefault(members[i]) + ingredient.Count;
            }
            if (totals.ContainsKey(recipe.ResultId))
                continue;

            var dryRun = Inventory.Empty();
            var grants = new List<(ushort, int)>();
            var granted = true;
            foreach (var (id, count) in totals)
            {
                granted &= dryRun.Add(id, count) && dryRun.CountFor(id) == count;
                grants.Add((id, count));
            }
            if (granted && dryRun.CanCraftBatch(recipe, 1, (index, _) => members[index]))
                return (recipe, members, grants);
        }

        throw new InvalidOperationException("no crafting recipe can be afforded from creative grants");
    }

    [Fact]
    public void AMalformedFrameDropsItsSenderAndTheServerKeepsServing()
    {
        // A one-byte ClientHello: the version read runs off the end of the payload
        using var raw = new TcpClient();
        raw.Connect("127.0.0.1", _server.BoundPort);
        var stream = raw.GetStream();
        var frame = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 1);
        frame[4] = (byte)NetMessage.ClientHello;
        stream.Write(frame);
        stream.Flush();

        // Pump until the server has handled the frame and closed the socket (a readable socket
        // with nothing buffered is a closed one), bounded so a hang fails instead of blocking
        var sw = Stopwatch.StartNew();
        while (!raw.Client.Poll(0, SelectMode.SelectRead) && sw.Elapsed.TotalSeconds < 15)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
        }

        // The bad peer was closed: its read ends instead of blocking
        stream.ReadTimeout = 2000;
        int read;
        try
        {
            read = stream.Read(new byte[16]);
        }
        catch (System.IO.IOException e) when (e.InnerException is SocketException { SocketErrorCode: not SocketError.TimedOut })
        {
            read = 0; // a reset is a close too
        }
        Assert.Equal(0, read);

        using var good = Connect("Tester");
        Assert.Equal(1, _server.ClientCount);
    }

    [Fact]
    public void AMovingPlayerBecomesAStreamingFocusAndALaterJoinerStillSpawnsAtTheWorldSpawn()
    {
        using var first = Connect("First");
        var spawn = first.Server!.Value.Spawn;
        var camera = _world.Camera.Position;

        // The first player moves two chunks off. It flies (noclip), so the move is never judged
        // against whatever terrain stands beside the spawn, and the speed cap only paces it.
        _server.AllowCreative = true;
        PumpFor(0.1);
        var moved = spawn + new Vector3(2f * Chunk.Size, 0f, 0f);
        var movedChunk = (moved / Chunk.Size).Floor();
        Assert.NotEqual((spawn / Chunk.Size).Floor(), movedChunk);

        var sw = Stopwatch.StartNew();
        while (PlayerOf(first).Position != moved && sw.Elapsed.TotalSeconds < 15)
        {
            SendState(first, moved, NetPlayerFlags.Creative | NetPlayerFlags.Flying);
            Tick(0.05);
        }
        Assert.Equal(moved, PlayerOf(first).Position);

        for (var i = 0; i < 5; i++)
            Tick(0.05);

        // Chunks now stream around the player alone, while the server camera stays put: it is the
        // focus only while nobody is online. (The spawn search may still settle the camera
        // vertically, so only its horizontal position is compared.)
        Assert.Equal(new[] { movedChunk }, _world.Focus.Chunks);
        Assert.Equal(camera.X, _world.Camera.Position.X);
        Assert.Equal(camera.Z, _world.Camera.Position.Z);

        using var second = Connect("Second");
        Assert.Equal(spawn, second.Server!.Value.Spawn);
    }

    [Fact]
    public void LodRequestsAreBuiltABoundedNumberPerPumpAndARepeatOnce()
    {
        using var client = Connect("Tester");
        while (client.Inbound.TryDequeue(out _)) { }

        var distinct = NetworkConfig.NetLodBuildsPerPump + 2;
        void Request(int rx)
        {
            using var writer = new PacketWriter(NetMessage.LodRegionRequest);
            writer.Write(0);
            writer.Write(rx);
            writer.Write(0);
            client.Send(writer.ToArray());
        }

        // Region 0 asked three times while it is still queued
        Request(0);
        Request(0);
        for (var rx = 1; rx < distinct; rx++)
            Request(rx);
        Request(0);
        Thread.Sleep(300);

        var received = new List<int>();
        void Drain()
        {
            while (client.Inbound.TryDequeue(out var payload))
            {
                using var reader = new PacketReader(payload);
                if (reader.Message != NetMessage.LodRegionData)
                    continue;
                reader.ReadInt();
                received.Add(reader.ReadInt());
            }
        }

        _server.Pump(0.01);
        Thread.Sleep(200);
        Drain();
        Assert.True(received.Count <= NetworkConfig.NetLodBuildsPerPump,
            $"{received.Count} regions built in one Pump");

        var sw = Stopwatch.StartNew();
        while (received.Count < distinct && sw.Elapsed.TotalSeconds < 60)
        {
            _server.Pump(0.01);
            Thread.Sleep(20);
            Drain();
        }
        PumpFor(0.2);
        Drain();

        Assert.Equal(distinct, received.Count);
        Assert.Single(received, rx => rx == 0);
    }

    [Fact]
    public void ABreakWithoutTheToolTheBlockNeedsIsRefusedAndTheBlockRestored()
    {
        using var client = Connect("Miner");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        SetCell(arena.Front, Stone);
        Assert.False(Mining.CanMine(Stone, null));

        // Bare-handed, however long the mining took
        var start = SendWorld(client, WorldIntent.BreakStart(arena.Eye, arena.AimAtFront, arena.Front));
        Assert.True(AwaitResult(client, start));
        for (var i = 0; i < 20; i++)
            Tick(0.5);
        var complete = SendWorld(client, WorldIntent.BreakComplete(arena.Eye, arena.AimAtFront, arena.Front));
        Assert.False(AwaitResult(client, complete));

        // The stone stands, and the client was told so for that very intent
        Assert.Equal(Stone, CellAt(arena.Front).Id);
        Assert.Contains(InboxOf(client).Corrections,
            c => c.Seq == complete && c.Cell.X == (int)arena.Front.X && c.Cell.Y == (int)arena.Front.Y
                 && c.Cell.Z == (int)arena.Front.Z && c.Cell.Id == Stone);
    }

    [Fact]
    public void BreakingFasterThanTheHitsAllowIsRefusedAndAtPaceBreaks()
    {
        using var client = Connect("Miner");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        SetCell(arena.Front, Dirt);
        var required = BareHandSeconds(Dirt);
        Assert.True(required > NetworkConfig.NetBreakSlackSeconds, "dirt must take several hits");

        // Start and completion together: far fewer hits than the hardness needs
        SendWorld(client, WorldIntent.BreakStart(arena.Eye, arena.AimAtFront, arena.Front));
        var hurried = SendWorld(client, WorldIntent.BreakComplete(arena.Eye, arena.AimAtFront, arena.Front));
        Assert.False(AwaitResult(client, hurried));
        Assert.Equal(Dirt, CellAt(arena.Front).Id);

        // The same break after the time its hits take goes through
        var start = SendWorld(client, WorldIntent.BreakStart(arena.Eye, arena.AimAtFront, arena.Front));
        Assert.True(AwaitResult(client, start));
        for (var waited = 0.0; waited < required + 0.05; waited += 0.25)
            Tick(0.25);
        var complete = SendWorld(client, WorldIntent.BreakComplete(arena.Eye, arena.AimAtFront, arena.Front));
        Assert.True(AwaitResult(client, complete));
        Assert.Equal(0, CellAt(arena.Front).Id);
    }

    [Fact]
    public void MinedLootLandsInTheBreakersInventoryOnly()
    {
        using var breaker = Connect("Breaker");
        using var bystander = Connect("Bystander");
        var arena = PrepareArena(breaker.Server!.Value.Spawn);
        SetCell(arena.Front, Dirt);

        // The bystander flies well out of pickup range first (a few states, each within the speed
        // cap); the flight flag is what makes a ten-block climb in one state legal
        _server.AllowCreative = true;
        for (var lift = 10; lift <= 40; lift += 10)
        {
            SendState(bystander, arena.Eye + new Vector3(0f, lift, 0f), NetPlayerFlags.Creative | NetPlayerFlags.Flying);
            for (var i = 0; i < 3; i++)
                Tick(0.1);
        }
        Assert.True(Vector3.Distance(PlayerOf(bystander).Position, arena.Eye) > DropManager.PickupRange * 4f);

        var start = SendWorld(breaker, WorldIntent.BreakStart(arena.Eye, arena.AimAtFront, arena.Front));
        Assert.True(AwaitResult(breaker, start));
        for (var waited = 0.0; waited < BareHandSeconds(Dirt) + 0.05; waited += 0.25)
            Tick(0.25);
        var complete = SendWorld(breaker, WorldIntent.BreakComplete(arena.Eye, arena.AimAtFront, arena.Front));
        Assert.True(AwaitResult(breaker, complete));

        // The drop flies to the breaker and is credited to the breaker's own inventory
        var sw = Stopwatch.StartNew();
        while (PlayerOf(breaker).Inventory.CountFor(Dirt) == 0 && sw.Elapsed.TotalSeconds < 15)
            Tick(1.0 / 30);
        Assert.True(PlayerOf(breaker).Inventory.CountFor(Dirt) > 0, "the breaker never got the loot");
        Assert.Equal(0, PlayerOf(bystander).Inventory.CountFor(Dirt));

        // The breaker's client hears of it through a snapshot; nothing was rolled client side
        sw.Restart();
        while ((InboxOf(breaker).Inventory == null || InboxOf(breaker).Mirror().CountFor(Dirt) == 0)
               && sw.Elapsed.TotalSeconds < 15)
            Tick(0.05);
        Assert.Equal(PlayerOf(breaker).Inventory.CountFor(Dirt), InboxOf(breaker).Mirror().CountFor(Dirt));
    }

    [Fact]
    public void PlacingABlockThePlayerDoesNotOwnIsRefusedAndSpendsNothing()
    {
        using var client = Connect("Builder");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        AwaitInventory(client);
        var placed = arena.FloorAhead + Vector3.UnitY;

        var seq = SendWorld(client, WorldIntent.Place(arena.Eye, arena.AimAtFloorAhead, arena.FloorAhead, Vector3.UnitY));
        Assert.False(AwaitResult(client, seq));
        Assert.Equal(0, CellAt(placed).Id);

        // The refusal is answered with the truth: the empty inventory and the empty cell
        var inbox = InboxOf(client);
        Assert.Equal(seq, inbox.InventorySeq);
        Assert.Equal(0, inbox.Mirror().SelectedCount);
        Assert.Contains(inbox.Corrections, c => c.Seq == seq && c.Cell.X == (int)placed.X
            && c.Cell.Y == (int)placed.Y && c.Cell.Z == (int)placed.Z && c.Cell.Id == 0);
    }

    [Fact]
    public void APlacementSpendsExactlyOneAndFollowsThePlacementRules()
    {
        using var client = Connect("Builder");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        GrantAsSurvival(client, Dirt, 3);
        var player = PlayerOf(client);
        Assert.Equal(3, player.Inventory.CountFor(Dirt));

        var seq = SendWorld(client, WorldIntent.Place(arena.Eye, arena.AimAtFloorAhead, arena.FloorAhead, Vector3.UnitY));
        Assert.True(AwaitResult(client, seq));
        Assert.Equal(Dirt, CellAt(arena.FloorAhead + Vector3.UnitY).Id);
        Assert.Equal(2, player.Inventory.CountFor(Dirt));
        Assert.Equal(2, InboxOf(client).Mirror().CountFor(Dirt));

        // Into the player's own body: a solid block there is refused, and nothing is spent
        var body = arena.FloorBelow + Vector3.UnitY;
        seq = SendWorld(client, WorldIntent.Place(arena.Eye, -Vector3.UnitY, arena.FloorBelow, Vector3.UnitY));
        Assert.False(AwaitResult(client, seq));
        Assert.Equal(0, CellAt(body).Id);
        Assert.Equal(2, player.Inventory.CountFor(Dirt));
    }

    [Fact]
    public void AForgedOverwriteOfAnOccupiedCellIsRefused()
    {
        using var client = Connect("Forger");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        SetCell(arena.Front, Stone);
        GrantAsSurvival(client, Dirt, 3);
        var behind = arena.Front + Vector3.UnitX;
        SetCell(behind, Stone);

        // A raw cell write (the retired protocol) replacing the stone: refused, answered with it
        using (var writer = new PacketWriter(NetMessage.BlockEdit))
        {
            writer.Write((int)arena.Front.X);
            writer.Write((int)arena.Front.Y);
            writer.Write((int)arena.Front.Z);
            writer.Write(Dirt);
            writer.Write((byte)7);
            client.Send(writer.ToArray());
        }
        var sw = Stopwatch.StartNew();
        while (!InboxOf(client).Corrections.Exists(c => c.Cell.Id == Stone) && sw.Elapsed.TotalSeconds < 15)
            Tick(0.01);
        Assert.Equal((Stone, (byte)0), CellAt(arena.Front));
        Assert.Contains(InboxOf(client).Corrections, c => c.Cell.X == (int)arena.Front.X && c.Cell.Id == Stone);

        // A placement naming the far face of the stone (placing through it), or a cell the aim
        // never reaches (the one behind), is refused and spends nothing
        var throughFace = SendWorld(client, WorldIntent.Place(arena.Eye, arena.AimAtFront, arena.Front, Vector3.UnitX));
        Assert.False(AwaitResult(client, throughFace));
        var unseen = SendWorld(client, WorldIntent.Place(arena.Eye, arena.AimAtFront, behind, -Vector3.UnitX));
        Assert.False(AwaitResult(client, unseen));
        Assert.Equal(Stone, CellAt(behind).Id);
        Assert.Equal(0, CellAt(behind + Vector3.UnitX).Id);
        Assert.Equal(3, PlayerOf(client).Inventory.CountFor(Dirt));
    }

    [Fact]
    public void TheServerComputesThePlacedStateByte()
    {
        using var client = Connect("Builder");
        using var watcher = Connect("Watcher");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        GrantAsSurvival(client, log, 1);

        // A placement carries no state at all: a placed log is building material, detached
        var seq = SendWorld(client, WorldIntent.Place(arena.Eye, arena.AimAtFloorAhead, arena.FloorAhead, Vector3.UnitY));
        Assert.True(AwaitResult(client, seq));
        var placed = arena.FloorAhead + Vector3.UnitY;
        Assert.Equal((log, WoodState.Detached), CellAt(placed));

        // Every client sees the server's state byte
        var sw = Stopwatch.StartNew();
        while (!InboxOf(watcher).Broadcast.Exists(c => c.X == (int)placed.X && c.Y == (int)placed.Y
                   && c.Z == (int)placed.Z && c.Id == log) && sw.Elapsed.TotalSeconds < 15)
            Tick(0.01);
        Assert.Contains(InboxOf(watcher).Broadcast, c => c.X == (int)placed.X && c.Y == (int)placed.Y
            && c.Z == (int)placed.Z && c.Id == log && c.State == WoodState.Detached);
    }

    [Fact]
    public void AHammerHitIsJudgedByTheHammersGapOnTheServer()
    {
        using var client = Connect("Smith");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        var anvil = ContentRegistry.RequireBlock("anvil");
        var cell = arena.Front;
        SetCell(cell, anvil.Id);
        Assert.True(_world.Interactions.TryTrackAnvilAt(cell));
        Assert.True(_world.Smithing.Anvils.TryPlace(cell, anvil.Anvil!.InputId));
        var state = _world.Smithing.Anvils.StateAt((int)cell.X, (int)cell.Y, (int)cell.Z)!;

        // Bare-handed, the hit is refused and counts for nothing
        var bare = SendWorld(client, WorldIntent.Hammer(arena.Eye, arena.AimAtFront, cell));
        Assert.False(AwaitResult(client, bare));
        Assert.Equal(0, state.Hits);

        var hammer = ContentRegistry.RequireItem("smithing_hammer");
        GrantAsSurvival(client, hammer.Id, 1);
        var first = SendWorld(client, WorldIntent.Hammer(arena.Eye, arena.AimAtFront, cell));
        Assert.True(AwaitResult(client, first));
        Assert.Equal(1, state.Hits);

        // Right away again: the hammer's gap has not run down on the server
        var hurried = SendWorld(client, WorldIntent.Hammer(arena.Eye, arena.AimAtFront, cell));
        Assert.False(AwaitResult(client, hurried));
        Assert.Equal(1, state.Hits);

        // After the gap it lands
        var gap = Mining.HitGapSeconds(hammer);
        for (var waited = 0.0; waited < gap + 0.1; waited += 0.25)
            Tick(0.25);
        var later = SendWorld(client, WorldIntent.Hammer(arena.Eye, arena.AimAtFront, cell));
        Assert.True(AwaitResult(client, later));
        Assert.Equal(2, state.Hits);
    }

    [Fact]
    public void ALogIsStoodOnAStumpWobbledAndLiftedOffOnTheServer()
    {
        using var client = Connect("Woodsman");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        var cell = arena.Front;
        SetCell(cell, ContentRegistry.RequireBlock("oak_stump").Id);
        GrantAsSurvival(client, log, 1);
        var (x, y, z) = ((int)cell.X, (int)cell.Y, (int)cell.Z);
        // The stump is lower than the middle the arena's aim points at, so aim at its low top
        var aim = Vector3.Normalize(cell + new Vector3(0.5f, 0.2f, 0.5f) - arena.Eye);

        Assert.True(AwaitResult(client, SendWorld(client, WorldIntent.Use(arena.Eye, aim, cell))));
        Assert.Equal(log, _world.Stumps.LogAt(x, y, z)!.LogId);

        Tick(0.5);
        Assert.True(AwaitResult(client, SendWorld(client, WorldIntent.StumpHit(arena.Eye, aim, cell))));
        Assert.True(_world.Stumps.LogAt(x, y, z)!.WobbleLeft > 0f);

        Tick(0.5);
        Assert.True(AwaitResult(client, SendWorld(client, WorldIntent.Use(arena.Eye, aim, cell))));
        Assert.Null(_world.Stumps.LogAt(x, y, z));
    }

    [Fact]
    public void AJumpBesideALitBloomeryWorksItsBellowsForThatPlayer()
    {
        using var client = Connect("Smith");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        var bloomery = ContentRegistry.RequireBlock("bloomery");
        var anchor = arena.Front;
        Multiblock.Convert(_world.GetChunkManager(), anchor, 0, bloomery, new HashSet<Vector3>());
        var bloomeries = _world.Smithing.Bloomeries;
        bloomeries.Ensure(anchor, 0, bloomery.Id);
        Assert.True(bloomeries.TryAddOre(anchor, bloomery.Bloomery!.OreId));
        Assert.True(bloomeries.TryAddFuel(anchor, ContentRegistry.RequireItem("coal")));
        Assert.True(bloomeries.Ignite(anchor));
        var fire = bloomeries.StateAt((int)anchor.X, (int)anchor.Y, (int)anchor.Z)!;
        fire.Heat = 0.3f;

        // Standing, then rising off the ground: one jump
        SendState(client, arena.Eye, NetPlayerFlags.None);
        Tick(0.06);
        Tick(0.06);
        Assert.Equal(0.3f, fire.Heat, 0.02f);

        SendState(client, arena.Eye + new Vector3(0f, 0.6f, 0f), NetPlayerFlags.Airborne);
        Tick(0.06);
        Tick(0.06);
        Assert.True(fire.Heat > 0.3f + BloomeryConfig.PumpHeat * 0.5f, $"heat {fire.Heat}");
    }

    [Fact]
    public void TntOnePlayerPrimesDetonatesForEveryClient()
    {
        using var igniter = Connect("Igniter");
        using var watcher = Connect("Watcher");
        var arena = PrepareArena(igniter.Server!.Value.Spawn);
        var tnt = ContentRegistry.RequireBlock("tnt").Id;
        SetCell(arena.Front, tnt);
        GrantAsSurvival(igniter, ContentRegistry.RequireItem("firestarter").Id, 1);
        // The spark always catches, so the test is not a dice roll
        _world.UseRoll = static () => 0f;

        var seq = SendWorld(igniter, WorldIntent.Use(arena.Eye, arena.AimAtFront, arena.Front));
        Assert.True(AwaitResult(igniter, seq));
        Assert.Equal(0, CellAt(arena.Front).Id);
        Assert.NotEmpty(_world.PrimedTnt);

        // The fuse burns down on the server and the blast's cells reach the other client
        var floor = arena.FloorAhead;
        var sw = Stopwatch.StartNew();
        while (!InboxOf(watcher).Broadcast.Exists(c => c.X == (int)floor.X && c.Y == (int)floor.Y
                   && c.Z == (int)floor.Z && c.Id == 0) && sw.Elapsed.TotalSeconds < 30)
            Tick(0.1);
        Assert.Contains(InboxOf(watcher).Broadcast, c => c.X == (int)arena.Front.X && c.Y == (int)arena.Front.Y
            && c.Z == (int)arena.Front.Z && c.Id == 0);
        Assert.Contains(InboxOf(watcher).Broadcast, c => c.X == (int)floor.X && c.Y == (int)floor.Y
            && c.Z == (int)floor.Z && c.Id == 0);
        Assert.Equal(0, CellAt(floor).Id);
    }

    [Fact]
    public void APrimedTntReachesTheClientsEntityList()
    {
        using var igniter = Connect("Igniter");
        using var watcher = Connect("Watcher");
        var arena = PrepareArena(igniter.Server!.Value.Spawn);
        var tnt = ContentRegistry.RequireBlock("tnt").Id;
        SetCell(arena.Front, tnt);
        GrantAsSurvival(igniter, ContentRegistry.RequireItem("firestarter").Id, 1);
        _world.UseRoll = static () => 0f;

        var seq = SendWorld(igniter, WorldIntent.Use(arena.Eye, arena.AimAtFront, arena.Front));
        Assert.True(AwaitResult(igniter, seq));
        var primed = Assert.Single(_world.PrimedTnt);

        // The next entity broadcast carries the cube to the client that did not light it, with the
        // block that primed and the fuse the server runs, in milliseconds
        var inbox = InboxOf(watcher);
        NetEntity? carried = null;
        var sw = Stopwatch.StartNew();
        while (carried == null && sw.Elapsed.TotalSeconds < 5)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
            DrainAll();
            if (inbox.Entities?.Find(e => e.Kind == NetEntityKind.PrimedTnt) is { Kind: NetEntityKind.PrimedTnt } found)
                carried = found;
        }

        Assert.NotNull(carried);
        var entry = carried!.Value;
        Assert.Equal(primed.BlockId, entry.ItemId);
        Assert.Equal(tnt, entry.ItemId);
        Assert.Equal((int)MathF.Round(primed.FuseDuration * 1000f), entry.Ammo);
        Assert.InRange(entry.Health, 1, entry.Ammo);
        Assert.True(Vector3.Distance(primed.Position, entry.Position) < 1f,
            $"the broadcast cube sits at {entry.Position}, the server's at {primed.Position}");

        // Once the fuse has burnt out on the server, the broadcasts stop listing it
        sw.Restart();
        while (_world.PrimedTnt.Count > 0 && sw.Elapsed.TotalSeconds < 30)
            Tick(0.1);
        Assert.Empty(_world.PrimedTnt);
        // Pump past any snapshot still in flight from before the blast
        sw.Restart();
        while (sw.Elapsed.TotalSeconds < 0.5)
            Tick(0.05);
        Assert.NotNull(inbox.Entities);
        Assert.DoesNotContain(inbox.Entities!, e => e.Kind == NetEntityKind.PrimedTnt);
    }

    [Fact]
    public void AnIcebergReachesTheClientsEntityList()
    {
        using var client = Connect("Watcher");
        var spawn = client.Server!.Value.Spawn;
        var icebergs = _world.IcebergManager!;
        var chunks = _world.GetChunkManager();

        // A small berg hanging in the open air high over the player, once the cells there are loaded
        var center = spawn + new Vector3(0.5f, 40f, 0.5f);
        Iceberg? served = null;
        var sw = Stopwatch.StartNew();
        while (served == null && sw.Elapsed.TotalSeconds < 60)
        {
            served = icebergs.Spawn(chunks, 0, IcebergConfig.ScaleMax, 1, 77, center, IcebergConfig.LifeMaxSeconds);
            if (served == null)
                Tick(0.05);
        }
        Assert.NotNull(served);

        var inbox = InboxOf(client);
        NetEntity? carried = null;
        sw.Restart();
        while (carried == null && sw.Elapsed.TotalSeconds < 5)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
            DrainAll();
            if (inbox.Entities?.Find(e => e.Kind == NetEntityKind.Iceberg) is { Kind: NetEntityKind.Iceberg } found)
                carried = found;
        }

        Assert.NotNull(carried);
        var entry = carried!.Value;
        NetEntity.UnpackIcebergAmmo(entry.Ammo, out var scale, out _);
        Assert.Equal(served.Variant, entry.ItemId);
        Assert.Equal(served.Seed, entry.Health);
        Assert.Equal(served.Turns, NetEntity.TurnsFromYaw(entry.Yaw));
        Assert.Equal(served.Scale, scale, 2);
        Assert.True(Vector3.Distance(served.Center, entry.Position) < 1f);
    }

    [Fact]
    public void ASlimesSizeReachesTheClientsEntityListAndItsMirror()
    {
        using var client = Connect("Watcher");
        var spawn = client.Server!.Value.Spawn;
        Assert.True(_world.SpawnEntity("slime", spawn + new Vector3(3f, -Camera.EyeHeight, 0f), SlimeSize.Max));
        var served = Assert.Single(_world.Slimes);

        var inbox = InboxOf(client);
        NetEntity? carried = null;
        var sw = Stopwatch.StartNew();
        while (carried == null && sw.Elapsed.TotalSeconds < 5)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
            DrainAll();
            if (inbox.Entities?.Find(e => e.Kind == NetEntityKind.Slime) is { Kind: NetEntityKind.Slime } found)
                carried = found;
        }

        Assert.NotNull(carried);
        Assert.Equal(served.Size, carried!.Value.Ammo);

        // A client world mirrors the slime at the size the server runs it
        var mirror = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        try
        {
            mirror.GetChunkManager().Headless = true;
            mirror.ClientMirror.ApplyNetworkEntities(inbox.Entities!);
            var mirrored = Assert.Single(mirror.Slimes);
            Assert.Equal(served.Size, mirrored.Size);
            Assert.Equal(served.Health, mirrored.Health);
        }
        finally
        {
            mirror.SaveAndDispose();
        }
    }

    [Fact]
    public void SkyMobsReachTheClientsEntityListByKindAndAFlyerAtItsHeight()
    {
        using var client = Connect("Watcher");
        var spawn = client.Server!.Value.Spawn;
        var feet = spawn - new Vector3(0f, Camera.EyeHeight, 0f);
        Assert.True(_world.SpawnEntity(Galeraptor.EntityName, feet + new Vector3(3f, 12f, 0f)));
        Assert.True(_world.SpawnEntity(Cloudgolem.EntityName, feet + new Vector3(-3f, 0f, 0f)));
        var flyer = Assert.Single(_world.SkyMobs, m => m is Galeraptor);

        var inbox = InboxOf(client);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 5
               && (inbox.Entities?.Exists(e => e.Kind == NetEntityKind.Galeraptor) != true
                   || !inbox.Entities.Exists(e => e.Kind == NetEntityKind.Cloudgolem)))
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
            DrainAll();
        }

        var carried = Assert.Single(inbox.Entities!, e => e.Kind == NetEntityKind.Galeraptor);
        Assert.Single(inbox.Entities!, e => e.Kind == NetEntityKind.Cloudgolem);
        Assert.Equal(flyer.Position, carried.Position);

        // A client world mirrors both kinds, the flyer at the height the server flies it
        var mirror = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        try
        {
            mirror.GetChunkManager().Headless = true;
            mirror.ClientMirror.ApplyNetworkEntities(inbox.Entities!);
            Assert.Equal(flyer.Position.Y, Assert.Single(mirror.SkyMobs, m => m is Galeraptor).Position.Y);
            Assert.Single(mirror.SkyMobs, m => m is Cloudgolem);
        }
        finally
        {
            mirror.SaveAndDispose();
        }
    }

    [Fact]
    public void ASandwormReachesTheClientsEntityListWithItsPhaseAndItsMirrorFollowsIt()
    {
        using var client = Connect("Watcher");
        var spawn = client.Server!.Value.Spawn;
        var feet = spawn - new Vector3(0f, Camera.EyeHeight, 0f);
        Assert.True(_world.SpawnEntity(Sandworm.EntityName, feet + new Vector3(10f, 0f, 0f)));
        var worm = Assert.Single(_world.Sandworms);

        var inbox = InboxOf(client);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 5 && inbox.Entities?.Exists(e => e.Kind == NetEntityKind.Sandworm) != true)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
            DrainAll();
        }

        var carried = Assert.Single(inbox.Entities!, e => e.Kind == NetEntityKind.Sandworm);
        Assert.Equal(worm.Position, carried.Position);
        Assert.Equal((int)worm.Phase, carried.Ammo);

        var mirror = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        try
        {
            mirror.GetChunkManager().Headless = true;
            mirror.ClientMirror.ApplyNetworkEntities(inbox.Entities!);
            var copy = Assert.Single(mirror.Sandworms);
            Assert.True(copy.IsMirror);
            Assert.Equal(worm.Phase, copy.Phase);
            Assert.Equal(worm.Position, copy.Position);
        }
        finally
        {
            mirror.SaveAndDispose();
        }
    }

    [Fact]
    public void AClientSeesATowerDoorOpenOnceItsFloorIsCleared()
    {
        using var client = Connect("Climber");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        var inbox = InboxOf(client);

        // A one-floor tower around the arena: its band holds the arena's rows, a pile beside the player and a
        // two-cell door in front of it
        var door = new[] { (arena.X + 2, arena.Y, arena.Z), (arena.X + 2, arena.Y + 1, arena.Z) };
        var pile = (arena.X - 2, arena.Y, arena.Z);
        var record = TowerRecord.Build((900, 900), arena.X, arena.Z, 8,
            [(arena.Y - 1, arena.Y + 4, (IReadOnlyList<(int X, int Y, int Z)>)[pile], door)], hasSite: false);
        _world.Towers.Track(record);
        foreach (var (x, y, z) in door)
            SetCell(new Vector3(x, y, z), TowerPalette.DoorBlock);
        SetCell(new Vector3(pile.Item1, pile.Item2, pile.Item3), TowerPalette.PileBlock);

        bool Broadcasts(int from, (int X, int Y, int Z) cell, ushort id) =>
            inbox.Broadcast.Skip(from).Any(c => c.X == cell.X && c.Y == cell.Y && c.Z == cell.Z && c.Id == id);

        // The closed door reaches the client (pumping the server only, so the pile never wakes)
        var sw = Stopwatch.StartNew();
        while (!door.All(d => Broadcasts(0, d, TowerPalette.DoorBlock)) && sw.Elapsed.TotalSeconds < 10)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
            DrainAll();
        }
        Assert.True(door.All(d => Broadcasts(0, d, TowerPalette.DoorBlock)), "the door never reached the client");

        // Clearing the floor opens its door: the world writes the door cells to air and the client is told
        var seen = inbox.Broadcast.Count;
        Assert.True(_world.ClearTowerFloor(new Vector3(arena.X + 0.5f, arena.Y + 1f, arena.Z + 0.5f), 0));
        sw.Restart();
        while (!door.All(d => Broadcasts(seen, d, 0)) && sw.Elapsed.TotalSeconds < 10)
            Tick(0.05);
        Assert.True(door.All(d => Broadcasts(seen, d, 0)), "the opened door never reached the client");
        Assert.True(record.Floors[0].DoorOpen);

        // A station frame sent after the door opened carries it to the client's mirror
        inbox.Stations = null;
        sw.Restart();
        while (inbox.Stations == null && sw.Elapsed.TotalSeconds < 10)
            Tick(0.05);
        Assert.NotNull(inbox.Stations);

        var mirror = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        try
        {
            mirror.GetChunkManager().Headless = true;
            // ClientSession hands ApplyNetworkStations a reader already past the message byte
            using var reader = new PacketReader(inbox.Stations!);
            Assert.Equal(NetMessage.StationState, reader.Message);
            mirror.ClientMirror.ApplyNetworkStations(reader);
            var copy = mirror.Towers.RecordAt(arena.X, arena.Z);
            Assert.NotNull(copy);
            Assert.True(copy!.Floors[0].DoorOpen);
        }
        finally
        {
            mirror.SaveAndDispose();
        }
    }

    [Fact]
    public void HellMobsReachTheClientsEntityListAndItsMirrorKeepsTheirLayerAndAttacks()
    {
        using var client = Connect("Watcher");
        var spawn = client.Server!.Value.Spawn;
        // Straight under the player (the despawn rule is horizontal), deep enough to be a scaled layer
        var deep = new Vector3(spawn.X, WorldGenerationConfig.HellTopY - 3 * WorldGenerationConfig.HellLayerPeriodBlocks - 20,
            spawn.Z);
        Assert.True(_world.SpawnEntity(Hellbrute.EntityName, deep + new Vector3(3f, 0f, 0f)));
        Assert.True(_world.SpawnEntity(Cinderwitch.EntityName, deep + new Vector3(-3f, 0f, 0f)));
        var brute = Assert.IsType<Hellbrute>(Assert.Single(_world.HellMobs, m => m is Hellbrute));
        var caster = Assert.IsType<Cinderwitch>(Assert.Single(_world.HellMobs, m => m is Cinderwitch));
        Assert.True(brute.MaxHealth > EntityRegistry.Require(Hellbrute.EntityName).MaxHealth);
        brute.RestoreState(brute.MaxHealth - 5, 1.25f, Vector3.Zero);
        caster.RestoreState(caster.MaxHealth - 1, -0.5f, Vector3.Zero);
        // Stands in for three swings the server simulated: the counter its entry carries
        brute.MirrorAttackCount(3);

        var inbox = InboxOf(client);
        WaitForEntities(inbox, entities => entities.Exists(e => e.Kind == NetEntityKind.Hellbrute && e.Ammo == 3)
                                           && entities.Exists(e => e.Kind == NetEntityKind.Cinderwitch));
        foreach (var mob in new RealmMob[] { brute, caster })
        {
            var kind = mob is Hellbrute ? NetEntityKind.Hellbrute : NetEntityKind.Cinderwitch;
            var carried = Assert.Single(inbox.Entities!, e => e.Kind == kind);
            Assert.Equal(mob.Position, carried.Position);
            Assert.Equal(mob.Health, carried.Health);
            Assert.Equal(mob.Facing, carried.Yaw);
        }

        var mirror = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        try
        {
            mirror.GetChunkManager().Headless = true;
            mirror.ClientMirror.ApplyNetworkEntities(inbox.Entities!);
            var mirroredBrute = Assert.IsType<Hellbrute>(Assert.Single(mirror.HellMobs, m => m is Hellbrute));
            var mirroredCaster = Assert.IsType<Cinderwitch>(Assert.Single(mirror.HellMobs, m => m is Cinderwitch));
            // The layer follows from where the mirror stands, so the scaled pool and the wound match the server's
            Assert.Equal(brute.MaxHealth, mirroredBrute.MaxHealth);
            Assert.Equal(caster.MaxHealth, mirroredCaster.MaxHealth);
            Assert.Equal(brute.Health, mirroredBrute.Health);
            Assert.Equal(brute.Facing, mirroredBrute.Facing);
            Assert.False(mirroredBrute.Animation.IsPlaying(Hellbrute.MeleeClip));

            // The server starts another swing: the next snapshot makes the mirror play the brute's melee clip
            brute.MirrorAttackCount(4);
            WaitForEntities(inbox, entities => entities.Exists(e => e.Kind == NetEntityKind.Hellbrute && e.Ammo == 4));
            mirror.ClientMirror.ApplyNetworkEntities(inbox.Entities!);
            Assert.True(mirroredBrute.Animation.IsPlaying(Hellbrute.MeleeClip));
            Assert.False(mirroredCaster.Animation.IsPlaying(Cinderwitch.CastClip));
        }
        finally
        {
            mirror.SaveAndDispose();
        }
    }

    [Fact]
    public void SkeletonsReachTheClientsEntityListByKindWithTheirLook()
    {
        using var client = Connect("Archer");
        var spawn = client.Server!.Value.Spawn;
        // Straight under the player (the despawn rule is horizontal) and far below it, so it never reaches the player
        var deep = new Vector3(spawn.X, spawn.Y - 200f, spawn.Z + 3f);
        Assert.True(_world.SpawnEntity(Skeleton.EntityName, deep));
        var skeleton = Assert.Single(_world.Skeletons);
        skeleton.RestoreState(skeleton.MaxHealth - 3, 0.5f, Vector3.Zero);
        // Stands in for the attacks the server simulated: the counter its entry carries
        skeleton.MirrorAttackCount(3);

        var inbox = InboxOf(client);
        WaitForEntities(inbox, entities => entities.Exists(e => e.Kind == NetEntityKind.Skeleton && e.Ammo == 3));
        // Sent as a skeleton (never under the switch's goblin default), its role and outfit in the item id
        var carried = Assert.Single(inbox.Entities!, e => e.Kind == NetEntityKind.Skeleton);
        Assert.Equal(Skeleton.PackLook(skeleton.Role, skeleton.Outfit), carried.ItemId);
        Assert.Equal(skeleton.Health, carried.Health);

        var mirror = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        try
        {
            mirror.GetChunkManager().Headless = true;
            mirror.ClientMirror.ApplyNetworkEntities(inbox.Entities!);
            var mirrored = Assert.Single(mirror.Skeletons);
            Assert.Equal(skeleton.Role, mirrored.Role);
            Assert.Equal(skeleton.Outfit, mirrored.Outfit);
            Assert.Equal(skeleton.MaxHealth, mirrored.MaxHealth);
            Assert.Equal(skeleton.Health, mirrored.Health);
            // The first count only sets: an attack already under way when first seen is not replayed
            Assert.False(mirrored.Animation.IsPlaying(Skeleton.ShootClip));

            // The server shoots twice in a row: the count skips a melee and the mirror draws its bow
            skeleton.MirrorAttackCount(5);
            WaitForEntities(inbox, entities => entities.Exists(e => e.Kind == NetEntityKind.Skeleton && e.Ammo == 5));
            mirror.ClientMirror.ApplyNetworkEntities(inbox.Entities!);
            Assert.True(mirrored.Animation.IsPlaying(Skeleton.ShootClip));
            Assert.False(mirrored.Animation.IsPlaying(Skeleton.MeleeClip));
        }
        finally
        {
            mirror.SaveAndDispose();
        }
    }

    [Fact]
    public void CaveMobsReachTheClientsEntityListByKindAndAMirrorKeepsItsTierPool()
    {
        using var client = Connect("Delver");
        var spawn = client.Server!.Value.Spawn;
        // Straight under the player (the despawn rule is horizontal) and far below it, so it never reaches the player
        var deep = new Vector3(spawn.X, spawn.Y - 200f, spawn.Z + 3f);
        Assert.True(_world.SpawnEntity(Sporeling.EntityName, deep, tier: 3));
        var sporeling = Assert.IsType<Sporeling>(Assert.Single(_world.CaveMobs));
        Assert.Equal(3, sporeling.Tier);
        sporeling.RestoreState(sporeling.MaxHealth - 2, 0.5f, Vector3.Zero);
        // Stands in for the attacks the server simulated: the counter its entry carries
        sporeling.MirrorAttackCount(2);

        var inbox = InboxOf(client);
        WaitForEntities(inbox, entities => entities.Exists(e => e.Kind == NetEntityKind.Sporeling && e.Ammo == 2));
        // Sent as a sporeling (never under the switch's goblin default), its tier in the item id
        var carried = Assert.Single(inbox.Entities!, e => e.Kind == NetEntityKind.Sporeling);
        Assert.Equal(sporeling.Tier, carried.ItemId);
        Assert.Equal(sporeling.Health, carried.Health);

        var mirror = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        try
        {
            mirror.GetChunkManager().Headless = true;
            mirror.ClientMirror.ApplyNetworkEntities(inbox.Entities!);
            var mirrored = Assert.IsType<Sporeling>(Assert.Single(mirror.CaveMobs));
            Assert.Equal(sporeling.Tier, mirrored.Tier);
            Assert.Equal(sporeling.MaxHealth, mirrored.MaxHealth);
            Assert.Equal(sporeling.Health, mirrored.Health);
            Assert.False(mirrored.Animation.IsPlaying(Sporeling.MeleeClip));

            // The server bursts: the count moves and the mirror plays its melee
            sporeling.MirrorAttackCount(3);
            WaitForEntities(inbox, entities => entities.Exists(e => e.Kind == NetEntityKind.Sporeling && e.Ammo == 3));
            mirror.ClientMirror.ApplyNetworkEntities(inbox.Entities!);
            Assert.True(mirrored.Animation.IsPlaying(Sporeling.MeleeClip));
        }
        finally
        {
            mirror.SaveAndDispose();
        }
    }

    // A one-floor tower whose roof altar stands in the arena's front cell (the altar block written there), its door
    // open or held shut by an owed skeleton nothing can raise (it has no piles)
    private TowerRecord AltarTower(Arena arena, bool cleared)
    {
        var record = TowerRecord.Build((901, 901), (int)arena.Front.X, (int)arena.Front.Z, 8,
            [(arena.Y - 5, arena.Y, (IReadOnlyList<(int X, int Y, int Z)>)[], (IReadOnlyList<(int X, int Y, int Z)>)[])],
            hasSite: false);
        Assert.Equal(((int)arena.Front.X, (int)arena.Front.Y, (int)arena.Front.Z), record.AltarCell);
        if (cleared) record.Floors[0].DoorOpen = true;
        else record.Floors[0].Owed = 1;
        _world.Towers.Track(record);
        SetCell(arena.Front, TowerPalette.AltarBlock);
        return record;
    }

    [Fact]
    public void AClientRaisesTheSkeletonKingFromAClearedTowersAltar()
    {
        using var client = Connect("Challenger");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        var inbox = InboxOf(client);
        var record = AltarTower(arena, cleared: true);

        // The client's right-click is only its usual Use intent: the server's replay claims the altar and begins
        var seq = SendWorld(client, WorldIntent.Use(arena.Eye, arena.AimAtFront, arena.Front));
        AwaitResult(client, seq);
        Assert.NotEqual(TowerAltarState.Dormant, record.AltarState);

        // Once the ritual has run its course the king rises, and the client's EntityList carries it by kind
        var sw = Stopwatch.StartNew();
        while (!(inbox.Entities?.Exists(e => e.Kind == NetEntityKind.SkeletonKing) ?? false)
               && sw.Elapsed.TotalSeconds < MobConfig.SkeletonKingRitualSeconds + 15)
            Tick(0.05);
        var carried = Assert.Single(inbox.Entities!, e => e.Kind == NetEntityKind.SkeletonKing);
        var king = Assert.Single(_world.SkeletonKings);
        Assert.Equal(record.Key, king.Home.Tower);
        Assert.Equal(TowerAltarState.Awake, record.AltarState);
        Assert.InRange(carried.ItemId, 1, 3);

        // The client mirrors it as a boss, and a station frame tells its tower record the king walks
        inbox.Stations = null;
        sw.Restart();
        while (inbox.Stations == null && sw.Elapsed.TotalSeconds < 10)
            Tick(0.05);
        Assert.NotNull(inbox.Stations);
        var mirror = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        try
        {
            mirror.GetChunkManager().Headless = true;
            mirror.ClientMirror.ApplyNetworkEntities(inbox.Entities!);
            var mirrored = Assert.Single(mirror.SkeletonKings);
            Assert.True(mirrored.IsBoss);
            Assert.Equal(carried.ItemId, mirrored.Phase);
            mirror.RefreshMobs();
            Assert.NotNull(BossSelection.Nearest(mirror.Mobs, mirrored.Position));

            using var reader = new PacketReader(inbox.Stations!);
            Assert.Equal(NetMessage.StationState, reader.Message);
            mirror.ClientMirror.ApplyNetworkStations(reader);
            var copy = mirror.Towers.RecordAt(record.CenterX, record.CenterZ);
            Assert.NotNull(copy);
            Assert.Equal(TowerAltarState.Awake, copy!.AltarState);
        }
        finally
        {
            mirror.SaveAndDispose();
        }
    }

    [Fact]
    public void ASealedAltarRaisesNothingForAClient()
    {
        using var client = Connect("Hasty");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        var inbox = InboxOf(client);
        var record = AltarTower(arena, cleared: false);

        var seq = SendWorld(client, WorldIntent.Use(arena.Eye, arena.AimAtFront, arena.Front));
        AwaitResult(client, seq);
        // Well past the ritual's length: the seals hold, so nothing began and no king rose
        for (var t = 0f; t < MobConfig.SkeletonKingRitualSeconds + 1f; t += 0.05f)
            Tick(0.05);
        Assert.False(record.Floors[0].DoorOpen);
        Assert.Equal(TowerAltarState.Dormant, record.AltarState);
        Assert.Empty(_world.SkeletonKings);
        Assert.False(inbox.Entities?.Exists(e => e.Kind == NetEntityKind.SkeletonKing) ?? false);
    }

    // Pumps the server until the newest EntityList the inbox holds satisfies the condition, or five seconds pass
    private void WaitForEntities(Inbox inbox, Predicate<List<NetEntity>> ready)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 5 && (inbox.Entities == null || !ready(inbox.Entities)))
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
            DrainAll();
        }
        Assert.True(inbox.Entities != null && ready(inbox.Entities), "the expected EntityList never arrived");
    }

    [Fact]
    public void AnIntentAimedFromWhereThePlayerIsNotIsRefused()
    {
        using var client = Connect("Teleporter");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        SetCell(arena.Front, Dirt);

        // The same break, claimed from an eye far from the player's authoritative position
        var far = arena.Eye + new Vector3(0f, NetworkConfig.NetMaxIntentEyeDriftBlocks * 3f, 0f);
        var seq = SendWorld(client, WorldIntent.BreakComplete(far, Vector3.Normalize(arena.Front + new Vector3(0.5f) - far),
            arena.Front));
        Assert.False(AwaitResult(client, seq));
        Assert.Equal(Dirt, CellAt(arena.Front).Id);

        // A replayed sequence id is refused too, whatever it asks
        _server.AllowCreative = true;
        SendModes(client, NetPlayerFlags.Creative);
        PumpFor(0.05);
        var replay = InboxOf(client).Seq;
        using (var writer = new PacketWriter(NetMessage.WorldIntent))
        {
            WorldIntentCodec.Write(writer, replay, WorldIntent.BreakComplete(arena.Eye, arena.AimAtFront, arena.Front));
            client.Send(writer.ToArray());
        }
        PumpFor(0.2);
        Assert.Equal(Dirt, CellAt(arena.Front).Id);

        // The same creative break under a fresh id goes through
        var fresh = SendWorld(client, WorldIntent.BreakComplete(arena.Eye, arena.AimAtFront, arena.Front));
        Assert.True(AwaitResult(client, fresh));
        Assert.Equal(0, CellAt(arena.Front).Id);
    }

    [Fact]
    public void AJoiningPlayerIsToldItsHealth()
    {
        using var client = Connect("Newcomer");
        var inbox = InboxOf(client);
        var sw = Stopwatch.StartNew();
        while (inbox.Health == null && sw.Elapsed.TotalSeconds < 15)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
            inbox.Drain(client);
        }

        // Without anything having happened to it yet: the server's own pool, a fresh player's full one
        Assert.NotNull(inbox.Health);
        Assert.Equal(PlayerOf(client).Health, inbox.Health);
        Assert.Equal(PlayerHealth.MaxHealth, inbox.Health);
        Assert.False(inbox.Dead);
    }

    [Fact]
    public void AServerAppliedEffectReachesTheClientsStatus()
    {
        using var client = Connect("Kindling");
        var player = PlayerOf(client);
        player.Creative = false;
        player.Flying = false;
        var inbox = InboxOf(client);
        var burning = StatusEffectRegistry.Require("burning");

        Assert.True(StatusEffects.Apply(player, burning.Name));
        var sw = Stopwatch.StartNew();
        while ((inbox.EffectMask & burning.Bit) == 0 && sw.Elapsed.TotalSeconds < 15)
            Tick(0.05);

        // The status the server sent carries the effect the server applied, and only what it holds
        Assert.NotEqual(0, inbox.EffectMask & burning.Bit);
        Assert.Equal(player.Effects.Mask, inbox.EffectMask);
    }

    [Fact]
    public void DeathHoldsThePlayerUntilARespawnIntentReturnsItHome()
    {
        using var client = Connect("Mortal");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        GrantAsSurvival(client, Dirt, 3);
        var player = PlayerOf(client);
        var home = arena.Eye + new Vector3(-2f, 0f, 1f);
        player.RespawnEye = home;
        var inbox = InboxOf(client);

        // A killing blow through the mob-hit sink; the client hears it is dead
        player.TakeHit(PlayerHealth.MaxHealth, Vector3.Zero, 0f, 0f);
        var sw = Stopwatch.StartNew();
        while (!inbox.Dead && sw.Elapsed.TotalSeconds < 15)
            Tick(0.05);
        Assert.True(inbox.Dead, "the client was never told it died");
        var corpse = player.Position;

        // Dead, it acts on nothing: the very placement a living player makes is refused and spends nothing
        var place = SendWorld(client, WorldIntent.Place(arena.Eye, arena.AimAtFloorAhead, arena.FloorAhead, Vector3.UnitY));
        Assert.False(AwaitResult(client, place));
        Assert.Equal(0, CellAt(arena.FloorAhead + Vector3.UnitY).Id);
        Assert.Equal(3, player.Inventory.CountFor(Dirt));

        // ...and its states no longer move it
        SendState(client, corpse + new Vector3(1f, 0f, 0f), NetPlayerFlags.None);
        PumpFor(0.1);
        Assert.Equal(corpse, player.Position);

        // A respawn from an eye that drifted past the intent eye rule (the corpse kept falling on the
        // client) is still accepted, and brings the player back home at full health
        var drifted = corpse - new Vector3(0f, NetworkConfig.NetMaxIntentEyeDriftBlocks + 5f, 0f);
        Assert.False(NetValidation.EyeWithin(corpse, drifted, NetworkConfig.NetMaxIntentEyeDriftBlocks));
        var respawn = SendWorld(client, WorldIntent.Respawn(drifted, -Vector3.UnitY));
        Assert.True(AwaitResult(client, respawn));
        Assert.Contains(inbox.PlayerCorrections, c => c.Eye == home);
        Assert.Equal(home, player.Position);
        Assert.False(inbox.Dead);
        Assert.Equal(PlayerHealth.MaxHealth, inbox.Health);
        Assert.Equal(player.Health, inbox.Health);

        // Alive, there is nothing to respawn from: a second respawn is refused and moves nothing
        var corrections = inbox.PlayerCorrections.Count;
        var again = SendWorld(client, WorldIntent.Respawn(home, -Vector3.UnitY));
        Assert.False(AwaitResult(client, again));
        Assert.Equal(corrections, inbox.PlayerCorrections.Count);
        Assert.Equal(home, player.Position);
    }

    [Fact]
    public void AnIllegalMoveIsCorrectedAndStatesAreIgnoredUntilTheAck()
    {
        using var client = Connect("Walker");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        var manager = _world.GetChunkManager();
        var inbox = InboxOf(client);

        // Settle on the arena floor a hair above its top, where the client's physics rests a body
        var start = new Vector3(arena.Eye.X, arena.Y + Camera.EyeHeight + 0.01f, arena.Eye.Z);
        Assert.False(BodyCollision.BodyCollides(start, manager));
        SendState(client, start, NetPlayerFlags.None);
        var sw = Stopwatch.StartNew();
        while (PlayerOf(client).Position != start && sw.Elapsed.TotalSeconds < 15)
            Tick(0.05);
        Assert.Equal(start, PlayerOf(client).Position);

        // A two-high stone column ahead, and a state reporting the body inside it
        SetCell(arena.Front, Stone);
        SetCell(arena.Front - Vector3.UnitY, Stone);
        var inside = new Vector3(arena.Front.X + 0.5f, start.Y, arena.Front.Z + 0.5f);
        Assert.True(BodyCollision.BodyCollides(inside, manager));
        Tick(0.05);
        SendState(client, inside, NetPlayerFlags.None);
        sw.Restart();
        while (inbox.PlayerCorrections.Count == 0 && sw.Elapsed.TotalSeconds < 15)
            Tick(0.05);

        // Snapped back to where the player stood before the move
        var correction = Assert.Single(inbox.PlayerCorrections);
        Assert.Equal(start, correction.Eye);
        Assert.Equal(start, PlayerOf(client).Position);

        // A legal step the client sent before it applied the correction is ignored...
        var step = start + new Vector3(-0.4f, 0f, 0f);
        SendState(client, step, NetPlayerFlags.None);
        for (var i = 0; i < 6; i++)
            Tick(0.05);
        Assert.Equal(start, PlayerOf(client).Position);

        // ...and the same step acking the correction is taken, with no second correction
        SendState(client, step, NetPlayerFlags.None, correction.Seq);
        sw.Restart();
        while (PlayerOf(client).Position != step && sw.Elapsed.TotalSeconds < 15)
            Tick(0.05);
        Assert.Equal(step, PlayerOf(client).Position);
        Assert.Single(inbox.PlayerCorrections);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void AWearersDoubleJumpIsNeverCorrectedButASecondAirHopIs(int deliveryWaitPumps)
    {
        using var client = Connect("Hopper");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        var manager = _world.GetChunkManager();
        var inbox = InboxOf(client);
        var player = PlayerOf(client);

        // Headroom for two jumps' climb over the arena's cleared rows
        var top = arena.Y + 7;
        bool Loaded()
        {
            for (var y = arena.Y + 5; y <= top; y++)
                if (!manager.TryGetCell(arena.X, y, arena.Z, out _, out _))
                    return false;
            return true;
        }
        var sw = Stopwatch.StartNew();
        while (!Loaded() && sw.Elapsed.TotalSeconds < 60)
            Tick(0.05);
        Assert.True(Loaded(), "the headroom's chunks never loaded");
        for (var x = arena.X - 3; x <= arena.X + 3; x++)
        for (var y = arena.Y + 5; y <= top; y++)
        for (var z = arena.Z - 3; z <= arena.Z + 3; z++)
            SetCell(new Vector3(x, y, z), 0);

        // Settle on the arena floor where the client's physics rests a body
        var resting = arena.Y + 0.01f;
        var start = new Vector3(arena.Eye.X, resting + Camera.EyeHeight, arena.Eye.Z);
        SendState(client, start, NetPlayerFlags.None);
        sw.Restart();
        while (player.Position != start && sw.Elapsed.TotalSeconds < 15)
            Tick(0.05);
        Assert.Equal(start, player.Position);

        // Wear the cloud boots (equipment is the server's own)
        var boots = ContentRegistry.RequireItem("cloud_boots").Id;
        Assert.True(player.Inventory.Add(boots));
        Assert.True(player.Inventory.Equipment.TryEquipAuto(
            player.Inventory.GearEntries().Last(e => e.ItemId == boots).InstanceId));
        Assert.Equal(1, player.AirJumps);

        const float up = PlayerPhysics.JumpSpeed / PlayerPhysics.Gravity;
        var dt = (float)NetworkConfig.NetPlayerStateSeconds;

        // The feet of a body jumping at each launch moment (from wherever it is then), resting on the floor
        float FeetAt(float t, float[] launches)
        {
            var feet = resting;
            var since = 0f;
            var speed = 0f;
            foreach (var at in launches)
            {
                if (at > t) break;
                feet = MathF.Max(resting, feet + speed * (at - since) - 0.5f * PlayerPhysics.Gravity * (at - since) * (at - since));
                since = at;
                speed = PlayerPhysics.JumpSpeed;
            }
            return MathF.Max(resting, feet + speed * (t - since) - 0.5f * PlayerPhysics.Gravity * (t - since) * (t - since));
        }

        // One state at a time, each pumped until the server has judged it
        void Send(float feet)
        {
            var before = player.LastStateAt;
            var eye = new Vector3(start.X, feet + Camera.EyeHeight, start.Z);
            var wait = Stopwatch.StartNew();
            var pumps = 0;
            while (player.LastStateAt == before && wait.Elapsed.TotalSeconds < 5)
            {
                // Exercise both immediate sends and a state delayed behind empty transport pumps.
                if (pumps++ == deliveryWaitPumps)
                    SendState(client, eye, feet > resting ? NetPlayerFlags.Airborne : NetPlayerFlags.None);
                // The trajectory advances one dt per sample. Delivery waits must not stretch
                // that sample into extra simulation time and turn its legal climb into a hover.
                if (pumps == 1)
                    Tick(dt);
                else
                {
                    _server.Pump(0);
                    Thread.Sleep(2);
                    DrainAll();
                }
            }
            Assert.NotEqual(before, player.LastStateAt);
            Assert.Equal((double)dt, player.LastStateAt - before, 6);
        }

        // A ground jump and one air hop at its apex, down to the floor again: never corrected
        for (var t = 0f; t <= 4f * up + 0.2f; t += dt)
            Send(FeetAt(t, [0f, up]));
        Assert.Empty(inbox.PlayerCorrections);
        Assert.Equal(resting, player.Position.Y - Camera.EyeHeight, 3);

        // A second air hop at the next apex: corrected
        for (var t = 0f; t <= 4f * up && inbox.PlayerCorrections.Count == 0; t += dt)
            Send(FeetAt(t, [0f, up, 2f * up]));
        sw.Restart();
        while (inbox.PlayerCorrections.Count == 0 && sw.Elapsed.TotalSeconds < 15)
            Tick(dt);
        Assert.NotEmpty(inbox.PlayerCorrections);
    }

    [Fact]
    public void ALegalWalkAcrossTheArenaIsNeverCorrected()
    {
        using var client = Connect("Walker");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        var inbox = InboxOf(client);

        // Back and forth along the cleared row at walking pace (a state every 50 ms) for 2 s,
        // never leaving the cleared cells
        var stride = PlayerPhysics.BaseSpeed * (float)NetworkConfig.NetPlayerStateSeconds;
        var offset = 0f;
        var direction = 1f;
        var last = arena.Eye;
        for (var t = 0.0; t < 2.0; t += NetworkConfig.NetPlayerStateSeconds)
        {
            if (MathF.Abs(offset + direction * stride) > 2f)
                direction = -direction;
            offset += direction * stride;
            last = arena.Eye + new Vector3(offset, 0f, 0f);
            SendState(client, last, NetPlayerFlags.None);
            Tick(NetworkConfig.NetPlayerStateSeconds);
        }

        var sw = Stopwatch.StartNew();
        while (PlayerOf(client).Position != last && sw.Elapsed.TotalSeconds < 15)
            Tick(0.05);
        Assert.Equal(last, PlayerOf(client).Position);
        Assert.Empty(inbox.PlayerCorrections);
    }

    [Fact]
    public void TheCosmeticFloatingFlagReachesTheOtherClientsUntouched()
    {
        using var floater = Connect("Floater");
        using var watcher = Connect("Watcher");
        var watching = InboxOf(watcher);
        const NetPlayerFlags floating = NetPlayerFlags.Swimming | NetPlayerFlags.Floating;

        // The server records the flags byte as sent and rebroadcasts it in the player list, so a bit
        // newer than another client's reader (the surface float) is neither refused nor stripped
        var sw = Stopwatch.StartNew();
        while ((!watching.ListedFlags.TryGetValue("Floater", out var listed) || listed != floating)
               && sw.Elapsed.TotalSeconds < 15)
        {
            SendModes(floater, floating);
            PumpFor(0.05);
            DrainAll();
        }

        Assert.Equal(floating, watching.ListedFlags["Floater"]);
        Assert.Equal(NetworkStatus.Handshaken, floater.Status);
    }

    // Casts (MG-4)

    /// <summary>The casts the world's effect hook saw, and the caster's mana right after the last spend.</summary>
    private sealed class CastLog
    {
        public readonly List<SpellCast> Fired = [];
        public float ManaAtSpend = float.NaN;
    }

    // The effect fires right after the spend, so the pool read there is exactly what the cast billed,
    // before any regeneration of the following ticks
    private CastLog RecordCasts()
    {
        var log = new CastLog();
        _world.SpellEffect = cast =>
        {
            log.Fired.Add(cast);
            log.ManaAtSpend = cast.Caster.Health!.Mana.Current;
        };
        return log;
    }

    /// <summary>
    /// Puts a new <paramref name="staffName"/> in the player's hand with a valid recipe bound in slot 0,
    /// on the test thread between pumps, and returns what a cast of it compiles to.
    /// </summary>
    private static SpellStats GiveBoundStaff(ServerPlayer player, string staffName, out StaffItem staff)
    {
        StaffFixture.Hold(player.Inventory, staffName, out staff, out var stack);
        return StaffFixture.Bind(staff, stack, 0, out _);
    }

    [Fact]
    public void ACastWithoutAStaffIsRefusedAndSpendsNothing()
    {
        using var client = Connect("Mage");
        var player = PlayerOf(client);
        var log = RecordCasts();

        var seq = SendWorld(client, WorldIntent.Cast(client.Server!.Value.Spawn, -Vector3.UnitZ, 0));
        Assert.False(AwaitResult(client, seq));
        Assert.Empty(log.Fired);
        Assert.Equal(player.Status.Mana.Max, player.Status.Mana.Current);
    }

    [Fact]
    public void ACastOfAnUnboundOrUnselectedSlotIsRefused()
    {
        using var client = Connect("Mage");
        var player = PlayerOf(client);
        GiveBoundStaff(player, "wooden_staff", out var staff);
        Assert.True(staff.Slots >= 2 && staff.Slots < SpellConfig.StaffMaxSlots);
        Assert.Equal(0, HeldStaff.SelectedSlot(player.Inventory));
        var log = RecordCasts();
        var eye = client.Server!.Value.Spawn;

        // A slot that is not the selected one, and one past the staff (but still a legal frame)
        foreach (var slot in new[] { 1, staff.Slots })
        {
            var seq = SendWorld(client, WorldIntent.Cast(eye, -Vector3.UnitZ, slot));
            Assert.False(AwaitResult(client, seq));
        }

        // The selected slot, but nothing bound in it
        Assert.True(HeldStaff.TrySelect(player.Inventory, 1));
        var unbound = SendWorld(client, WorldIntent.Cast(eye, -Vector3.UnitZ, 1));
        Assert.False(AwaitResult(client, unbound));

        Assert.Empty(log.Fired);
        Assert.Equal(player.Status.Mana.Max, player.Status.Mana.Current);
    }

    [Fact]
    public void AnAcceptedCastBillsExactlyTheRecipeCostAndFiresTheEffectOnce()
    {
        using var client = Connect("Mage");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        var player = PlayerOf(client);
        var stats = GiveBoundStaff(player, "wooden_staff", out _);
        var log = RecordCasts();
        var inbox = InboxOf(client);
        var before = player.Status.Mana.Current;
        Assert.Equal(player.Status.Mana.Max, before);

        // Two casts back to back: the second lands well inside the cooldown
        var first = SendWorld(client, WorldIntent.Cast(arena.Eye, arena.AimAtFront, 0));
        var second = SendWorld(client, WorldIntent.Cast(arena.Eye, arena.AimAtFront, 0));
        var simulated = 0.0;
        var sw = Stopwatch.StartNew();
        while (!inbox.Results.ContainsKey(second) && sw.Elapsed.TotalSeconds < 15)
        {
            Tick(0.01);
            simulated += 0.01;
        }

        Assert.True(inbox.Results.ContainsKey(first) && inbox.Results.ContainsKey(second));
        Assert.True(simulated < SpellConfig.CastCooldownSeconds - NetworkConfig.NetCastSlackSeconds,
            "the second cast arrived too late to test the cooldown");
        Assert.True(inbox.Results[first]);
        Assert.False(inbox.Results[second]);

        // Billed once, exactly the compiled cost, and the effect fired once with that recipe
        var cast = Assert.Single(log.Fired);
        Assert.Equal(stats.Cost, cast.Stats.Cost);
        Assert.Equal(0, cast.Slot);
        Assert.Equal(stats.Cost, before - log.ManaAtSpend, 1e-4f);
        var regen = player.Status.Mana.Max * (float)simulated / ManaConfig.RegenFullSeconds;
        Assert.True(player.Status.Mana.Current <= before - stats.Cost + regen + 1e-3f);

        // The accepted cast is rebroadcast as the cast gesture, not a punch
        Assert.Equal(NetPlayerAction.Cast, player.LastAction);

        // A cast edits no cell
        Assert.Equal(0, CellAt(arena.Front).Id);
        Assert.Equal(Dirt, CellAt(arena.FloorAhead).Id);
    }

    [Fact]
    public void ACastTheManaCannotCoverIsRefused()
    {
        using var client = Connect("Mage");
        var player = PlayerOf(client);
        var stats = GiveBoundStaff(player, "wooden_staff", out _);
        Assert.True(stats.Cost > 0f);
        Assert.True(player.Status.Mana.TrySpend(player.Status.Mana.Current, false));
        var log = RecordCasts();

        var seq = SendWorld(client, WorldIntent.Cast(client.Server!.Value.Spawn, -Vector3.UnitZ, 0));
        Assert.False(AwaitResult(client, seq));
        Assert.Empty(log.Fired);
    }

    [Fact]
    public void ACreativeCastLeavesTheManaUntouched()
    {
        using var client = Connect("Mage");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        _server.AllowCreative = true;
        SendModes(client, NetPlayerFlags.Creative);
        var player = PlayerOf(client);
        var sw = Stopwatch.StartNew();
        while (!player.Creative && sw.Elapsed.TotalSeconds < 15)
            PumpFor(0.02);
        Assert.True(player.Creative);
        GiveBoundStaff(player, "wooden_staff", out _);
        var log = RecordCasts();
        var before = player.Status.Mana.Current;

        var seq = SendWorld(client, WorldIntent.Cast(arena.Eye, arena.AimAtFront, 0));
        Assert.True(AwaitResult(client, seq));
        Assert.Single(log.Fired);
        Assert.Equal(before, log.ManaAtSpend);
    }

    [Fact]
    public void StaffSlotSelectionIsServerAuthoritative()
    {
        using var client = Connect("Mage");
        var player = PlayerOf(client);
        GiveBoundStaff(player, "crystal_staff", out var staff);
        Assert.True(staff.Slots >= 2);
        var inbox = InboxOf(client);

        // The snapshot answering the newest intent sent (the join snapshot may still be in flight)
        Inventory Answer()
        {
            var mirror = AwaitInventory(client);
            while (inbox.InventorySeq != inbox.Seq)
                mirror = AwaitInventory(client);
            return mirror;
        }

        // A slot the staff has: the server's staff selects it and the client's snapshot says so
        SendIntent(client, InventoryIntent.SelectStaffSlot(1));
        Assert.Equal(1, HeldStaff.SelectedSlot(Answer()));
        Assert.Equal(1, HeldStaff.SelectedSlot(player.Inventory));

        // One past it: refused, and the snapshot keeps the slot the server has
        SendIntent(client, InventoryIntent.SelectStaffSlot(staff.Slots));
        Assert.Equal(1, HeldStaff.SelectedSlot(Answer()));
        Assert.Equal(1, HeldStaff.SelectedSlot(player.Inventory));
    }

    [Fact]
    public void AWorldSoundReachesThePlayersInEarshotButNotTheOneWhoMadeIt()
    {
        var maker = Connect("Maker");
        var listener = Connect("Listener");
        var eye = PlayerOf(maker).Position;
        DrainAll();

        // A slime hopping beside both, the maker's own mining blow, and a sound far out of everyone's earshot
        _world.Sounds.PlayAt(SfxLibrary.SlimeJumpGroup, eye + new Vector3(3f, 0f, 0f));
        _world.Sounds.PlayAt(SfxLibrary.StoneGroup, eye + new Vector3(1f, 0f, 0f), PlayerOf(maker).Id);
        _world.Sounds.PlayAt(SfxLibrary.SlimeLandGroup, eye + new Vector3(AudioConfig.SfxRangeBlocks * 3f, 0f, 0f));
        PumpFor(0.2);
        DrainAll();

        Assert.Equal([SfxLibrary.SlimeJumpGroup], InboxOf(maker).Sounds.Select(s => s.Group));
        Assert.Equal([SfxLibrary.SlimeJumpGroup, SfxLibrary.StoneGroup], InboxOf(listener).Sounds.Select(s => s.Group));
        Assert.Empty(_world.Sounds.Pending);
    }
}
