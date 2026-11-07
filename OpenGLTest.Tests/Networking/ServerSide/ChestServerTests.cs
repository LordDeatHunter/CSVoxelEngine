using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Server;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Networking.ServerSide;

/// <summary>
/// Chests over a loopback server: two players share one chest and each sees every move, a player who never opened a chest
/// or walked out of reach moves nothing, and a broken chest closes for its viewers and spills its contents.
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

        public void Drain(NetworkClient client)
        {
            while (client.Inbound.TryDequeue(out var payload))
            {
                using var reader = new PacketReader(payload);
                if (reader.Message != NetMessage.ChestContents) continue;
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

    private static byte Flint => ContentRegistry.RequireItem("flint").Id;

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
}
