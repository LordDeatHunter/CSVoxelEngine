using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Net.Sockets;
using System.Threading;
using VoxelEngine.Audio;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Server;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.Simulation.Lanterns;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Networking.ServerSide;

/// <summary>
/// Chests over a loopback server: two players share one chest and each sees every move, a player who never opened a chest
/// or walked out of reach moves nothing, a broken chest closes for its viewers and spills its contents, and a chest's lid
/// stands open for everyone while any player looks into it.
/// </summary>
[Collection("Worldgen seed")]
public sealed class ChestServerTests : IDisposable
{
    private readonly World _world;
    private readonly GameServer _server;
    private readonly Dictionary<NetworkClient, Received> _inboxes = new(ReferenceEqualityComparer.Instance);

    public ChestServerTests()
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

    /// <summary>What one client received about chests: the newest answer (null contents = the closed answer).</summary>
    private sealed class Received
    {
        public bool Answered;
        public ChestState? Contents;
        public int Answers;
        public LanternState? Lantern;
        public int LanternAnswers;
        public readonly List<SoundEvent> Sounds = [];
        public Dictionary<string, (ushort Id, bool Lit)> Held = new();
        public PlayerSaveData.InventoryData? Inventory;

        /// <summary>The newest station snapshot, whole (it ends with the chests that stand open).</summary>
        public byte[]? Stations;

        public void Drain(NetworkClient client)
        {
            while (client.Inbound.TryDequeue(out var payload))
            {
                using var reader = new PacketReader(payload);
                var message = reader.Message;
                if (message == NetMessage.StationState) Stations = payload;
                if (message == NetMessage.PlayerList)
                {
                    var count = reader.ReadInt();
                    for (var i = 0; i < count; i++)
                    {
                        reader.ReadInt(); var name = reader.ReadString(); reader.ReadVector3();
                        reader.ReadFloat(); reader.ReadFloat(); reader.ReadByte(); reader.ReadByte(); reader.ReadByte();
                        for (var slot = 0; slot < GearConfig.ArmorSlotCount; slot++) reader.ReadId();
                        Held[name] = (reader.ReadId(), reader.ReadBool());
                    }
                }
                if (message == NetMessage.SoundEvents)
                {
                    var sounds = new List<SoundEvent>();
                    SoundEventCodec.Read(reader, sounds);
                    Sounds.AddRange(sounds);
                }
                if (message == NetMessage.LanternContents)
                {
                    Lantern = LanternNetCodec.ReadContents(reader).State;
                    LanternAnswers++;
                }
                if (message == NetMessage.InventoryState)
                {
                    reader.ReadInt();
                    Inventory = InventoryNetCodec.Decode(reader.ReadBytes());
                }
                if (message != NetMessage.ChestContents) continue;
                var read = ChestNetCodec.ReadContents(reader);
                Contents = read?.Contents;
                Answered = true;
                Answers++;
            }
        }
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
        _inboxes[client] = new Received();
        return client;
    }

    private ServerPlayer PlayerOf(NetworkClient client)
    {
        Assert.True(_server.TryGetPlayer(client.Server!.Value.PlayerId, out var player));
        return player;
    }

    private void Tick(double dt)
    {
        _world.Update(dt, null);
        _server.Pump(dt);
        Thread.Sleep(2);
        foreach (var (client, inbox) in _inboxes) inbox.Drain(client);
    }

    private void TickUntil(Func<bool> done, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.Elapsed.TotalSeconds < 15) Tick(0.02);
        Assert.True(done(), what);
    }

    private static void Send(NetworkClient client, NetMessage message, Action<PacketWriter>? body = null)
    {
        using var writer = new PacketWriter(message);
        body?.Invoke(writer);
        client.Send(writer.ToArray());
    }

    private void Open(NetworkClient client, (int X, int Y, int Z) cell)
    {
        var inbox = _inboxes[client];
        inbox.Answered = false;
        Send(client, NetMessage.ChestOpen, w => ChestNetCodec.WriteOpen(w, cell.X, cell.Y, cell.Z));
        TickUntil(() => inbox.Answered, "the open was never answered");
    }

    private void Move(NetworkClient client, ChestMove move) =>
        Send(client, NetMessage.ChestMove, w => ChestNetCodec.WriteMove(w, move));

    // A chest cell two blocks along +X from the join spawn's feet, in a loaded chunk with air around it
    private (int X, int Y, int Z) PlaceChest(NetworkClient near)
    {
        var eye = near.Server!.Value.Spawn;
        var manager = _world.GetChunkManager();
        var cell = ((int)MathF.Floor(eye.X) + 2, (int)MathF.Floor(eye.Y) - 1, (int)MathF.Floor(eye.Z));
        TickUntil(() => manager.TryGetCell(cell.Item1, cell.Item2, cell.Item3, out _, out _), "the chest's chunk never loaded");
        var chunk = manager.ChunkAt(cell.Item1, cell.Item2, cell.Item3)!;
        var (lx, ly, lz) = VoxelCoords.LocalOf(cell.Item1, cell.Item2, cell.Item3);
        chunk.SetBlockRaw(lx, ly, lz, ContentRegistry.RequireBlock("wooden_chest").Id, 0);
        return cell;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroundLanternPickupSendsItsEquipmentAndFuelToItsOwner(bool placed)
    {
        using var client = Connect("Collector");
        var cell = PlaceChest(client); // Also waits for the player's chunk to be loaded.
        var player = PlayerOf(client);
        var inventory = player.Inventory;
        inventory.Import(new PlayerSaveData.InventoryData());
        var lantern = ContentRegistry.RequireBlock("lantern").Id;
        var coal = ContentRegistry.RequireItem("coal").Id;
        inventory.Add(lantern);
        var stack = inventory.Tools.Last();
        var fuel = LanternState.Of(stack)!;
        inventory.Add(coal, 3, autoEquip: false);
        fuel.Move(inventory, ChestMove.DepositStack(coal, 3));
        fuel.Advance(LanternState.Duration(coal) * .4f);
        if (placed)
        {
            var chunk = _world.GetChunkManager().ChunkAt(cell.Item1, cell.Item2, cell.Item3)!;
            var (x, y, z) = VoxelCoords.LocalOf(cell.Item1, cell.Item2, cell.Item3);
            chunk.SetBlockRaw(x, y, z, lantern, 0);
            _world.Lanterns.Place(inventory, new Vector3(cell.Item1, cell.Item2, cell.Item3));
        }
        else
            Assert.True(_world.ThrowSelected(false, player.Position, Vector3.UnitY, inventory));
        inventory.Select(7);
        _inboxes[client].Inventory = null;
        if (placed)
        {
            var target = new Vector3(cell.Item1, cell.Item2, cell.Item3);
            var aim = Vector3.Normalize(target + new Vector3(.5f) - player.Position);
            player.Flags = (byte)NetPlayerFlags.Sneaking;
            Send(client, NetMessage.WorldIntent, w => WorldIntentCodec.Write(w, 1,
                WorldIntent.Use(player.Position, aim, target)));
        }
        TickUntil(() => _inboxes[client].Inventory is { } data && data.Tools?.Any(t => t.Lantern != null) == true,
            "pickup never sent its authoritative inventory to the client");
        var mirror = Inventory.Empty();
        mirror.Import(_inboxes[client].Inventory!);
        Assert.Equal(7, mirror.SelectedIndex);
        Assert.Equal(lantern, mirror.SelectedBlockId);
        Assert.Equal(inventory.SelectedToolInstance, mirror.SelectedToolInstance);
        Assert.Equal(2, LanternState.Held(mirror)!.Storage.CountOf(coal));
        Assert.InRange(LanternState.Held(mirror)!.Remaining, 0.01f, fuel.Capacity);
        Assert.Single(inventory.Tools);
        Assert.Empty(_world.Drops);
    }

    [Theory]
    [InlineData(42)] // Both published 42 layouts lack part of the combined contract.
    [InlineData(43)] // Upstream skeleton layout and historical bridge.
    [InlineData(44)] // Both published upstream audio and lantern/hotbar layouts.
    [InlineData(45)] // Historical bridge integration.
    [InlineData(47)] // Planned separate bridge integration.
    public void IncompatibleLayoutsAreRejectedBeforeJoining(int version)
    {
        using var raw = new TcpClient();
        raw.Connect("127.0.0.1", _server.BoundPort);
        var stream = raw.GetStream();
        using var hello = new PacketWriter(NetMessage.ClientHello);
        hello.Write(version).Write("Incompatible");
        NetFraming.WriteFrame(stream, hello.ToArray());
        var sw = Stopwatch.StartNew();
        while (!raw.Client.Poll(0, SelectMode.SelectRead) && sw.Elapsed.TotalSeconds < 15)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
        }
        stream.ReadTimeout = 2000;
        int received;
        try { received = stream.ReadByte(); }
        catch (System.IO.IOException e) when (e.InnerException is SocketException { SocketErrorCode: not SocketError.TimedOut })
        {
            received = -1; // A reset also closes the rejected connection.
        }
        Assert.Equal(-1, received); // No ServerHello or inventory may reach an incompatible peer.
        Assert.Equal(0, _server.ClientCount);
        using var current = Connect("Compatible");
        Assert.Equal(1, _server.ClientCount);
    }

    [Fact]
    public void LanternMovesAreAuthoritativeAndSelectionInvalidatesTheSession()
    {
        var a = Connect("Alice");
        var b = Connect("Bob");
        var player = PlayerOf(a);
        var coal = ContentRegistry.RequireItem("coal").Id;
        var lantern = ContentRegistry.RequireBlock("lantern").Id;
        player.Inventory.Add(lantern);
        player.Inventory.PickBlock(lantern);
        var instance = player.Inventory.SelectedToolInstance;
        player.Aim(player.Position, Vector3.UnitY);
        player.Inventory.Add(coal, 3);
        PlayerOf(b).Inventory.Add(coal, 5);
        Send(b, NetMessage.LanternOpen, w => LanternNetCodec.WriteTarget(w, new(instance, default)));
        TickUntil(() => _inboxes[b].Answered, "forged held target was not refused");
        Assert.Null(PlayerOf(b).OpenLantern);
        Send(b, NetMessage.LanternMove, w => ChestNetCodec.WriteMove(w, ChestMove.DepositStack(coal, 2)));
        Send(a, NetMessage.LanternOpen, w => LanternNetCodec.WriteTarget(w, new(instance, default)));
        TickUntil(() => _inboxes[a].LanternAnswers > 0, "owned held lantern never opened");
        player.Inventory.Add(Flint, 2);
        Send(a, NetMessage.LanternMove, w => ChestNetCodec.WriteMove(w, ChestMove.DepositStack(Flint, 1)));
        Send(a, NetMessage.LanternMove, w => ChestNetCodec.WriteMove(w, ChestMove.DepositStack(coal, 2)));
        TickUntil(() => _inboxes[a].Lantern?.Remaining > 0f, "server did not burn stored fuel");
        var fuel = LanternState.Held(player.Inventory)!;
        Assert.Equal(1, fuel.Storage.CountOf(coal));
        Assert.Equal(1, player.Inventory.CountFor(coal));
        Assert.Equal(5, PlayerOf(b).Inventory.CountFor(coal));
        Assert.Equal(2, player.Inventory.CountFor(Flint));
        Assert.True(LanternManager.Emits(player.Inventory));
        // The same current-version connection carries fuel answers and upstream positional world sounds.
        var soundAt = player.Position + Vector3.UnitX;
        _world.Sounds.PlayAt(SfxLibrary.StoneGroup, soundAt, exceptPlayerId: player.Id);
        TickUntil(() => _inboxes[b].Sounds.Count > 0, "world sound was lost beside the lantern answers");
        Assert.Equal(new SoundEvent(SfxLibrary.StoneGroup, soundAt), Assert.Single(_inboxes[b].Sounds));
        Assert.Empty(_inboxes[a].Sounds); // The maker already predicted it locally.
        TickUntil(() => _inboxes[b].Held.TryGetValue("Alice", out var held) && held.Id == lantern && held.Lit,
            "authoritative held light never reached the other client");
        var snapshot = Inventory.Empty(); snapshot.Import(_inboxes[a].Inventory!);
        Assert.True(LanternState.Held(snapshot)!.Remaining > 0);
        Assert.Equal(1, LanternState.Held(snapshot)!.Storage.CountOf(coal));

        // A hotbar drag is replayed on the server before a queued fuel transfer. It moves the instance,
        // closes the now-unselected session and keeps both its fuel and the replaced item's pool intact.
        var source = player.Inventory.SelectedIndex;
        var destination = (source + 1) % Inventory.SlotCount;
        player.Inventory.EquipStackable(destination, Flint);
        Send(a, NetMessage.InventoryRequest, w =>
        {
            w.Write(1);
            InventoryIntentCodec.Write(w, InventoryIntent.MoveSlot(source, destination));
        });
        TickUntil(() => player.Inventory.ToolInstanceAt(destination) == instance, "hotbar drag never moved the lantern");
        var remaining = fuel.Remaining;
        Send(a, NetMessage.LanternMove, w => ChestNetCodec.WriteMove(w, ChestMove.Withdraw(0, 0)));
        TickUntil(() => player.OpenLantern == null, "changed selection kept the lantern session open");
        Assert.Equal(remaining, fuel.Remaining);
        Assert.Equal(1, fuel.Storage.CountOf(coal));
        Assert.False(LanternManager.Emits(player.Inventory));
        TickUntil(() => _inboxes[b].Held.TryGetValue("Alice", out var held) && held.Id == 0 && !held.Lit,
            "switching away left a remote held light behind");
        Assert.Equal(0, player.Inventory.IdAt(source));
        Assert.Equal(2, player.Inventory.CountFor(Flint));
        TickUntil(() => _inboxes[a].Inventory?.Hotbar?[destination].ToolInstance == instance,
            "moved lantern never reached the inventory mirror");
        snapshot.Import(_inboxes[a].Inventory!);
        Assert.Equal(remaining, LanternState.Of(snapshot.GetToolStack(instance))!.Remaining);
        Assert.Equal(1, LanternState.Of(snapshot.GetToolStack(instance))!.Storage.CountOf(coal));

        Send(a, NetMessage.InventoryRequest, w =>
        {
            w.Write(2);
            InventoryIntentCodec.Write(w, InventoryIntent.ClearSlot(destination));
        });
        TickUntil(() => player.Inventory.IdAt(destination) == 0, "hotbar clear never reached the server");
        Assert.Equal(remaining, LanternState.Of(player.Inventory.GetToolStack(instance))!.Remaining);
        Assert.Equal(1, LanternState.Of(player.Inventory.GetToolStack(instance))!.Storage.CountOf(coal));
    }

    [Fact]
    public void SharedPlacedLanternRejectsUnopenedMovesAndClosesWhenRemoved()
    {
        var a = Connect("Alice"); var b = Connect("Bob");
        var cell = PlaceChest(a);
        var chunks = _world.GetChunkManager();
        var (lx, ly, lz) = VoxelCoords.LocalOf(cell.Item1, cell.Item2, cell.Item3);
        chunks.ChunkAt(cell.Item1, cell.Item2, cell.Item3)!.SetBlockRaw(lx, ly, lz,
            ContentRegistry.RequireBlock("lantern").Id, 1);
        var coal = ContentRegistry.RequireItem("coal").Id;
        foreach (var client in new[] { a, b })
        {
            var player = PlayerOf(client);
            player.Inventory.Add(coal, 2);
            var center = new Vector3(cell.Item1 + .5f, cell.Item2 + .5f, cell.Item3 + .5f);
            player.Aim(player.Position, Vector3.Normalize(center - player.Position));
            Send(client, NetMessage.LanternOpen, w => LanternNetCodec.WriteTarget(w, new(0, cell)));
            TickUntil(() => _inboxes[client].LanternAnswers > 0, "placed lantern did not open");
        }
        Send(a, NetMessage.LanternMove, w => ChestNetCodec.WriteMove(w, ChestMove.DepositStack(coal, 2)));
        TickUntil(() => _inboxes[b].Lantern?.Remaining > 0f, "other viewer never saw the fuel");
        Send(b, NetMessage.LanternMove, w => ChestNetCodec.WriteMove(w, ChestMove.Withdraw(0, 0)));
        TickUntil(() => PlayerOf(b).Inventory.CountFor(coal) == 3, "shared withdrawal never arrived");
        Assert.Equal(0, PlayerOf(a).Inventory.CountFor(coal));
        var state = LanternState.Of(_world.Lanterns.Peek(cell))!;
        Assert.True(state.Remaining > 0);
        Assert.Equal(0, state.Storage.CountOf(coal));
        chunks.ChunkAt(cell.Item1, cell.Item2, cell.Item3)!.SetBlockRaw(lx, ly, lz, 0, 0);
        TickUntil(() => PlayerOf(a).OpenLantern == null && PlayerOf(b).OpenLantern == null, "removed lantern stayed open");
        var dropped = _world.Drops.Where(d => d.Instance != null && LanternState.Of(d.Instance) != null).ToArray();
        Assert.Single(dropped);
        Assert.True(LanternState.Of(dropped[0].Instance)!.Remaining > 0);
    }

    private static ushort Flint => ContentRegistry.RequireItem("flint").Id;

    [Fact]
    public void TwoPlayersShareOneChestAndEachSeesEveryMove()
    {
        var a = Connect("Alice");
        var b = Connect("Bob");
        var cell = PlaceChest(a);
        PlayerOf(a).Inventory.Add(Flint, 5);
        Open(a, cell);
        Open(b, cell);
        Assert.True(_inboxes[a].Contents!.IsEmpty);
        Assert.True(_inboxes[b].Contents!.IsEmpty);

        var bAnswers = _inboxes[b].Answers;
        Move(a, ChestMove.DepositStack(Flint, 0));
        TickUntil(() => _inboxes[b].Answers > bAnswers, "the other viewer never saw the deposit");
        Assert.Equal(5, _inboxes[a].Contents!.CountOf(Flint));
        Assert.Equal(5, _inboxes[b].Contents!.CountOf(Flint));
        Assert.Equal(0, PlayerOf(a).Inventory.CountFor(Flint));

        var aAnswers = _inboxes[a].Answers;
        Move(b, ChestMove.Withdraw(0, 0));
        TickUntil(() => _inboxes[a].Answers > aAnswers, "the first viewer never saw the withdraw");
        Assert.True(_inboxes[a].Contents!.IsEmpty);
        Assert.Equal(5, PlayerOf(b).Inventory.CountFor(Flint));
        Assert.Equal(0, PlayerOf(a).Inventory.CountFor(Flint));
    }

    [Fact]
    public void AMoveFromAPlayerWithNoOpenChestChangesNothingAndIsNotAnswered()
    {
        var a = Connect("Alice");
        var b = Connect("Bob");
        var cell = PlaceChest(a);
        PlayerOf(a).Inventory.Add(Flint, 3);
        PlayerOf(b).Inventory.Add(Flint, 4);
        Open(a, cell);

        Move(b, ChestMove.DepositStack(Flint, 0));
        for (var i = 0; i < 25; i++) Tick(0.02);

        Assert.Equal(4, PlayerOf(b).Inventory.CountFor(Flint));
        Assert.Equal(0, _world.Chests.Peek(cell.Item1, cell.Item2, cell.Item3)!.CountOf(Flint));
        Assert.False(_inboxes[b].Answered);
    }

    [Fact]
    public void OpeningWhatIsNotAChestIsAnsweredClosed()
    {
        var a = Connect("Alice");
        PlaceChest(a);
        var eye = a.Server!.Value.Spawn;
        var elsewhere = ((int)MathF.Floor(eye.X) + 2, (int)MathF.Floor(eye.Y) - 1, (int)MathF.Floor(eye.Z) + 1);

        Open(a, elsewhere);

        Assert.Null(_inboxes[a].Contents);
        Assert.Null(PlayerOf(a).OpenChest);
    }

    [Fact]
    public void ADepositNamingAnIdPastTheLoadedContentIsRefusedWithoutThrowing()
    {
        // The first id this build's content does not assign: a client with other content could name it
        var outside = new ChestMove(ChestMoveKind.DepositStack, Id: BlockTables.IdCount, Count: 1);
        var inventory = Inventory.Empty();
        inventory.Add(Flint, 1);
        Assert.Equal(0, ChestAccess.Apply(inventory, new ChestState(1), outside));

        var a = Connect("Alice");
        var cell = PlaceChest(a);
        PlayerOf(a).Inventory.Add(Flint, 2);
        Open(a, cell);
        Move(a, outside);
        // The session survives the refused move: a valid deposit sent after it still lands, and only it
        Move(a, ChestMove.DepositStack(Flint, 0));
        TickUntil(() => _inboxes[a].Contents?.CountOf(Flint) == 2, "the deposit after the refused one never arrived");

        var chest = _world.Chests.Peek(cell.Item1, cell.Item2, cell.Item3)!;
        Assert.Equal(2, Enumerable.Range(0, chest.SlotCount).Sum(i => chest[i].Count));
        Assert.Equal(0, PlayerOf(a).Inventory.CountFor(Flint));
    }

    [Fact]
    public void WalkingOutOfReachClosesTheChest()
    {
        var a = Connect("Alice");
        var cell = PlaceChest(a);
        Open(a, cell);
        Assert.NotNull(PlayerOf(a).OpenChest);

        PlayerOf(a).Teleport(PlayerOf(a).Position + new Vector3(0, 0, 60));
        var answers = _inboxes[a].Answers;
        TickUntil(() => _inboxes[a].Answers > answers, "the closed answer never came");

        Assert.Null(_inboxes[a].Contents);
        Assert.Null(PlayerOf(a).OpenChest);
    }

    [Fact]
    public void ABrokenChestClosesForItsViewersAndSpillsWhatItHeld()
    {
        var a = Connect("Alice");
        var cell = PlaceChest(a);
        PlayerOf(a).Inventory.Add(Flint, 6);
        Open(a, cell);
        Move(a, ChestMove.DepositStack(Flint, 0));
        TickUntil(() => _inboxes[a].Contents!.CountOf(Flint) == 6, "the deposit never arrived");

        var chunk = _world.GetChunkManager().ChunkAt(cell.Item1, cell.Item2, cell.Item3)!;
        var (lx, ly, lz) = VoxelCoords.LocalOf(cell.Item1, cell.Item2, cell.Item3);
        chunk.SetBlockRaw(lx, ly, lz, 0, 0);
        TickUntil(() => _inboxes[a].Contents == null, "the viewer was never told the chest is gone");

        Assert.Equal(6, _world.Drops.Count(d => d.BlockId == Flint));
        Assert.Null(PlayerOf(a).OpenChest);
    }

    // Whether a client that applies this station snapshot shows the chest at the cell open
    private static bool AClientShowsItOpen(byte[] stations, (int X, int Y, int Z) cell)
    {
        var mirror = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        try
        {
            mirror.GetChunkManager().Headless = true;
            // ClientSession hands ApplyNetworkStations a reader already past the message byte
            using var reader = new PacketReader(stations);
            Assert.Equal(NetMessage.StationState, reader.Message);
            mirror.ClientMirror.ApplyNetworkStations(reader);
            mirror.ChestLids.Update(0.01f);
            return mirror.ChestLids.IsOpen(cell.X, cell.Y, cell.Z);
        }
        finally
        {
            mirror.SaveAndDispose();
        }
    }

    [Fact]
    public void AChestStandsOpenForEveryoneWhileAnyPlayerLooksIntoItAndShutsWhenTheLastLeaves()
    {
        var a = Connect("Alice");
        var b = Connect("Bob");
        var watcher = Connect("Carol");
        var cell = PlaceChest(a);
        var lids = _world.ChestLids;
        Assert.False(lids.IsOpen(cell.X, cell.Y, cell.Z));

        // The first viewer opens the lid, and a client that never touched the chest is told
        Open(a, cell);
        TickUntil(() => lids.IsOpen(cell.X, cell.Y, cell.Z), "the first viewer never opened the lid");
        _inboxes[watcher].Stations = null;
        TickUntil(() => _inboxes[watcher].Stations != null, "no station snapshot followed the open");
        Assert.True(AClientShowsItOpen(_inboxes[watcher].Stations!, cell));

        // One of two viewers leaving shuts nothing
        Open(b, cell);
        Send(a, NetMessage.ChestClose);
        TickUntil(() => PlayerOf(a).OpenChest == null, "the close never reached the server");
        for (var i = 0; i < 10; i++) Tick(0.02);
        Assert.True(lids.IsOpen(cell.X, cell.Y, cell.Z));

        // The last one leaving shuts it, here and (with the snapshot that follows) on every client
        Send(b, NetMessage.ChestClose);
        TickUntil(() => !lids.IsOpen(cell.X, cell.Y, cell.Z), "the lid never shut after its last viewer left");
        for (var i = 0; i < 25; i++) Tick(0.02);
        Assert.False(AClientShowsItOpen(_inboxes[watcher].Stations!, cell));
    }
}
