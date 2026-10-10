using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using Xunit;

namespace silkgltriangle.Tests;

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
        public int? Health;
        public bool Dead;
        public readonly List<(int Seq, Vector3 Eye)> PlayerCorrections = [];

        /// <summary>The newest <see cref="NetMessage.EntityList"/> received, decoded.</summary>
        public List<NetEntity>? Entities;

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
                        Health = status.Health;
                        Dead = status.Dead;
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
                        // The record shape the client's own reader decodes (Program's EntityList case)
                        var count = reader.ReadInt();
                        var entities = new List<NetEntity>(count);
                        for (var i = 0; i < count; i++)
                        {
                            var kind = (NetEntityKind)reader.ReadByte();
                            var id = reader.ReadInt();
                            var position = reader.ReadVector3();
                            var yaw = reader.ReadFloat();
                            var health = reader.ReadInt();
                            var ammo = reader.ReadInt();
                            var itemId = reader.ReadByte();
                            var velocity = reader.ReadVector3();
                            entities.Add(new NetEntity(kind, id, position, yaw, health, ammo, itemId, velocity));
                        }
                        Entities = entities;
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
                cells.Add(new NetCellChange(reader.ReadInt(), reader.ReadInt(), reader.ReadInt(),
                    reader.ReadByte(), reader.ReadByte()));
            return cells;
        }

        public Inventory Mirror()
        {
            Assert.NotNull(Inventory);
            var mirror = silkgltriangle.Inventory.Empty();
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

    private static byte Stone => ContentRegistry.RequireBlock("stone").Id;
    private static byte Dirt => ContentRegistry.RequireBlock("dirt").Id;

    /// <summary>
    /// A cleared pocket around the spawn eye: a dirt floor one row under the feet and air for four
    /// rows above it, seven blocks across, so every aim below is a clean line of sight. Ticks the
    /// world until the pocket's chunks have streamed in.
    /// </summary>
    private sealed record Arena(Vector3 Eye, int X, int Y, int Z)
    {
        /// <summary>The cell at eye height two blocks along +X, and the aim at its centre.</summary>
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
            SetCell(new Vector3(x, y, z), y == fy - 1 ? Dirt : (byte)0);
        return new Arena(eye, fx, fy, fz);
    }

    private void SetCell(Vector3 cell, byte id, byte state = 0)
    {
        var chunk = _world.GetChunkManager().ChunkAt((int)cell.X, (int)cell.Y, (int)cell.Z);
        Assert.NotNull(chunk);
        var (lx, ly, lz) = VoxelCoords.LocalOf((int)cell.X, (int)cell.Y, (int)cell.Z);
        chunk!.SetBlockRaw(lx, ly, lz, id, state);
    }

    private (byte Id, byte State) CellAt(Vector3 cell)
    {
        Assert.True(_world.GetChunkManager().TryGetCell((int)cell.X, (int)cell.Y, (int)cell.Z, out var id, out var state));
        return (id, state);
    }

    /// <summary>Grants <paramref name="id"/> through a creative request, then drops back to survival.</summary>
    private void GrantAsSurvival(NetworkClient client, byte id, int count)
    {
        _server.AllowCreative = true;
        SendModes(client, NetPlayerFlags.Creative);
        SendIntent(client, InventoryIntent.CreativeGrant(id, count));
        AwaitInventory(client);
        SendModes(client, NetPlayerFlags.None);
        PumpFor(0.05);
        Assert.False(PlayerOf(client).Creative);
    }

    /// <summary>Seconds of server time the bare hand needs to mine <paramref name="block"/>.</summary>
    private static double BareHandSeconds(byte block) =>
        BreakProgress.RequiredSeconds(Mining.HardnessOf(block, 0), Mining.BlockDamage(null), Mining.HitGapSeconds(null));

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
        var wrong = (byte[])members.Clone();
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
    private static (CraftingRecipe Recipe, byte[] Members, List<(byte Id, int Count)> Grants) FirstGrantableRecipe()
    {
        foreach (var recipe in ContentRegistry.CraftingRecipes)
        {
            var members = new byte[recipe.Ingredients.Length];
            var totals = new Dictionary<byte, int>();
            for (var i = 0; i < members.Length; i++)
            {
                var ingredient = recipe.Ingredients[i];
                members[i] = ingredient.AcceptedIds[0];
                totals[members[i]] = totals.GetValueOrDefault(members[i]) + ingredient.Count;
            }
            if (totals.ContainsKey(recipe.ResultId))
                continue;

            var dryRun = Inventory.Empty();
            var grants = new List<(byte, int)>();
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

        var distinct = WorldGenerationConfig.NetLodBuildsPerPump + 2;
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
        Assert.True(received.Count <= WorldGenerationConfig.NetLodBuildsPerPump,
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
        Assert.True(required > WorldGenerationConfig.NetBreakSlackSeconds, "dirt must take several hits");

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
    public void AnIntentAimedFromWhereThePlayerIsNotIsRefused()
    {
        using var client = Connect("Teleporter");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        SetCell(arena.Front, Dirt);

        // The same break, claimed from an eye far from the player's authoritative position
        var far = arena.Eye + new Vector3(0f, WorldGenerationConfig.NetMaxIntentEyeDriftBlocks * 3f, 0f);
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
        var drifted = corpse - new Vector3(0f, WorldGenerationConfig.NetMaxIntentEyeDriftBlocks + 5f, 0f);
        Assert.False(NetValidation.EyeWithin(corpse, drifted, WorldGenerationConfig.NetMaxIntentEyeDriftBlocks));
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
        Assert.False(PlayerPhysics.BodyCollides(start, manager));
        SendState(client, start, NetPlayerFlags.None);
        var sw = Stopwatch.StartNew();
        while (PlayerOf(client).Position != start && sw.Elapsed.TotalSeconds < 15)
            Tick(0.05);
        Assert.Equal(start, PlayerOf(client).Position);

        // A two-high stone column ahead, and a state reporting the body inside it
        SetCell(arena.Front, Stone);
        SetCell(arena.Front - Vector3.UnitY, Stone);
        var inside = new Vector3(arena.Front.X + 0.5f, start.Y, arena.Front.Z + 0.5f);
        Assert.True(PlayerPhysics.BodyCollides(inside, manager));
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

    [Fact]
    public void ALegalWalkAcrossTheArenaIsNeverCorrected()
    {
        using var client = Connect("Walker");
        var arena = PrepareArena(client.Server!.Value.Spawn);
        var inbox = InboxOf(client);

        // Back and forth along the cleared row at walking pace (a state every 50 ms) for 2 s,
        // never leaving the cleared cells
        var stride = PlayerPhysics.BaseSpeed * (float)WorldGenerationConfig.NetPlayerStateSeconds;
        var offset = 0f;
        var direction = 1f;
        var last = arena.Eye;
        for (var t = 0.0; t < 2.0; t += WorldGenerationConfig.NetPlayerStateSeconds)
        {
            if (MathF.Abs(offset + direction * stride) > 2f)
                direction = -direction;
            offset += direction * stride;
            last = arena.Eye + new Vector3(offset, 0f, 0f);
            SendState(client, last, NetPlayerFlags.None);
            Tick(WorldGenerationConfig.NetPlayerStateSeconds);
        }

        var sw = Stopwatch.StartNew();
        while (PlayerOf(client).Position != last && sw.Elapsed.TotalSeconds < 15)
            Tick(0.05);
        Assert.Equal(last, PlayerOf(client).Position);
        Assert.Empty(inbox.PlayerCorrections);
    }
}
