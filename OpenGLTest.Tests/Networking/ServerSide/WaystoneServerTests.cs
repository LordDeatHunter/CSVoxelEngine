using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Player;
using VoxelEngine.Server;
using VoxelEngine.Simulation.Waystones;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Networking.ServerSide;

/// <summary>
/// Waystones over a loopback server: a client's right-click (its Use intent) discovers a waystone on the server, which
/// tells that client what to show and makes the first player to activate it its only renamer; each client's rows follow
/// the registry; a trip is judged by the server (standing at a waystone, discovered, not lost, mana over half unless
/// creative), drains the pool, holds the traveler until the destination has loaded without ever correcting the states
/// it sends meanwhile, and lands it beside the destination with a correction.
/// </summary>
[Collection("Worldgen seed")]
public sealed class WaystoneServerTests : IDisposable
{
    private const int FloorY = 10;
    private const int Reach = 48;

    private static readonly (int X, int Y, int Z) Home = (0, FloorY + 1, 0);
    private static readonly (int X, int Y, int Z) Far = (400, FloorY + 1, 0);

    private readonly World _world;
    private readonly GameServer _server;
    private readonly Dictionary<NetworkClient, Inbox> _inboxes = new(ReferenceEqualityComparer.Instance);

    public WaystoneServerTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
        var source = new FlatWorldSource(-Reach, Reach, -Reach, Reach, FloorY, ContentRegistry.RequireBlock("stone").Id,
            Biome.Forest, warmth: 0.5f, wetness: 0.5f);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, serverMode: true, spawn: EyeBeside(Home),
            worldSource: source);
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

    /// <summary>What one client received about waystones, its intents and its position.</summary>
    private sealed class Inbox
    {
        public int Seq;
        public readonly List<WaystoneOpened> Opened = [];
        public readonly List<WaystoneNetRow> Rows = [];
        public int RowMessages;
        public readonly Dictionary<int, bool> Results = new();
        public readonly List<(int Seq, Vector3 Eye)> Corrections = [];
        public readonly List<int> ManaSeen = [];

        public void Drain(NetworkClient client)
        {
            while (client.Inbound.TryDequeue(out var payload))
            {
                using var reader = new PacketReader(payload);
                switch (reader.Message)
                {
                    case NetMessage.WaystoneOpened:
                        Opened.Add(WaystoneNetCodec.ReadOpened(reader));
                        break;
                    case NetMessage.WaystoneRows:
                        WaystoneNetCodec.ReadRows(reader, Rows);
                        RowMessages++;
                        break;
                    case NetMessage.IntentResult:
                    {
                        var seq = reader.ReadInt();
                        Results[seq] = reader.ReadBool();
                        break;
                    }
                    case NetMessage.PlayerCorrection:
                    {
                        var seq = reader.ReadInt();
                        Corrections.Add((seq, reader.ReadVector3()));
                        break;
                    }
                    case NetMessage.PlayerStatus:
                        ManaSeen.Add(PlayerStatusCodec.Read(reader).Mana);
                        break;
                }
            }
        }

        public WaystoneNetRow RowAt((int X, int Y, int Z) cell) => Rows.Find(r => (r.X, r.Y, r.Z) == cell);
    }

    private static Vector3 EyeBeside((int X, int Y, int Z) waystone) =>
        new(waystone.X + 0.5f, waystone.Y + Camera.EyeHeight + 0.02f, waystone.Z + 2.5f);

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
        _inboxes[client] = new Inbox();
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

    private void TickUntil(Func<bool> done, string what, double dt = 0.02)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.Elapsed.TotalSeconds < 60) Tick(dt);
        Assert.True(done(), what);
    }

    private bool Loaded((int X, int Y, int Z) c) => _world.GetChunkManager().TryGetCell(c.X, c.Y, c.Z, out _, out _);

    /// <summary>Stands the player beside a waystone cell, waits for it to load and builds the waystone there.</summary>
    private void BuildBeside(NetworkClient client, (int X, int Y, int Z) waystone)
    {
        PlayerOf(client).Teleport(EyeBeside(waystone));
        TickUntil(() => Loaded(waystone), $"the chunk of {waystone} never loaded");
        var chunk = _world.GetChunkManager().ChunkAt(waystone.X, waystone.Y, waystone.Z)!;
        var (lx, ly, lz) = VoxelCoords.LocalOf(waystone.X, waystone.Y, waystone.Z);
        chunk.SetBlockRaw(lx, ly, lz, ContentRegistry.RequireBlock("waystone_surface").Id, 0);
        chunk.SetBlockRaw(lx, ly + 1, lz, ContentRegistry.RequireBlock("waystone_surface_upper").Id, 0);
    }

    /// <summary>Sends one world intent with the client's next sequence id and waits for its result.</summary>
    private bool Intent(NetworkClient client, WorldIntent intent, double dt = 0.02)
    {
        var inbox = _inboxes[client];
        var seq = ++inbox.Seq;
        using (var writer = new PacketWriter(NetMessage.WorldIntent))
        {
            WorldIntentCodec.Write(writer, seq, intent);
            client.Send(writer.ToArray());
        }
        TickUntil(() => inbox.Results.ContainsKey(seq), $"intent #{seq} was never answered", dt);
        return inbox.Results[seq];
    }

    /// <summary>A right-click on the waystone from where the player stands, aimed at the middle of its shaft.</summary>
    private bool Activate(NetworkClient client, (int X, int Y, int Z) waystone)
    {
        var eye = PlayerOf(client).Position;
        var aim = Vector3.Normalize(new Vector3(waystone.X + 0.5f, waystone.Y + 0.6f, waystone.Z + 0.5f) - eye);
        return Intent(client, WorldIntent.Use(eye, aim, new Vector3(waystone.X, waystone.Y, waystone.Z)));
    }

    private bool Trip(NetworkClient client, (int X, int Y, int Z) to, double dt = 0.02) =>
        Intent(client, WorldIntent.Waystone(PlayerOf(client).Position, Vector3.UnitX, to), dt);

    private void Rename(NetworkClient client, (int X, int Y, int Z) cell, string name)
    {
        using var writer = new PacketWriter(NetMessage.WaystoneRename);
        WaystoneNetCodec.WriteRename(writer, cell, name);
        client.Send(writer.ToArray());
    }

    private static void SendState(NetworkClient client, Vector3 eye, int ack)
    {
        using var writer = new PacketWriter(NetMessage.PlayerState);
        writer.Write(eye).Write(0f).Write(0f).Write((byte)0).Write(ack);
        client.Send(writer.ToArray());
    }

    [Fact]
    public void TheFirstPlayerToActivateAWaystoneDiscoversItAndIsItsOnlyRenamer()
    {
        var alice = Connect("Alice");
        var bob = Connect("Bob");
        BuildBeside(alice, Home);
        PlayerOf(bob).Teleport(EyeBeside(Home));

        Assert.True(Activate(alice, Home));
        var found = Assert.Single(_inboxes[alice].Opened);
        Assert.Equal(Home, found.Cell);
        Assert.True(found.NewToPlayer);
        Assert.True(found.MayRename);
        Assert.True(PlayerOf(alice).Inventory.Waystones.Contains(Home.X, Home.Y, Home.Z));
        Assert.Equal(found.Name, _world.Waystones.Peek(Home.X, Home.Y, Home.Z)!.Name);
        TickUntil(() => _inboxes[alice].RowAt(Home).Name == found.Name, "the discoverer's rows never listed it");
        Assert.True(_inboxes[alice].RowAt(Home).MayRename);

        // A second player discovers the same waystone under the same name, and may not rename it
        Assert.True(Activate(bob, Home));
        var bobFound = Assert.Single(_inboxes[bob].Opened);
        Assert.True(bobFound.NewToPlayer);
        Assert.False(bobFound.MayRename);
        Assert.Equal(found.Name, bobFound.Name);
        TickUntil(() => _inboxes[bob].RowAt(Home).Name == found.Name, "the second player's rows never listed it");

        // Activating it again is no longer a discovery: the client opens the list
        Assert.True(Activate(alice, Home));
        Assert.False(_inboxes[alice].Opened[^1].NewToPlayer);

        // The second player's rename is refused, and answered with the rows as they stand
        var bobRows = _inboxes[bob].RowMessages;
        Rename(bob, Home, "Bobland");
        TickUntil(() => _inboxes[bob].RowMessages > bobRows, "the refused rename was never answered");
        Assert.Equal(found.Name, _world.Waystones.Peek(Home.X, Home.Y, Home.Z)!.Name);
        Assert.Equal(found.Name, _inboxes[bob].RowAt(Home).Name);

        // The first discoverer's rename is sanitized, and reaches every player who knows the waystone
        Rename(alice, Home, "  Home   Base ");
        TickUntil(() => _inboxes[bob].RowAt(Home).Name == "Home Base", "the rename never reached the other player");
        Assert.Equal("Home Base", _world.Waystones.Peek(Home.X, Home.Y, Home.Z)!.Name);
        TickUntil(() => _inboxes[alice].RowAt(Home).Name == "Home Base", "the renamer's rows never took the name");
    }

    [Fact]
    public void ATripDrainsTheManaHoldsTheTravelerUntilTheDestinationLoadsAndLandsItBesideIt()
    {
        var alice = Connect("Alice");
        var inbox = _inboxes[alice];
        BuildBeside(alice, Far);
        Assert.True(Activate(alice, Far));
        BuildBeside(alice, Home);
        Assert.True(Activate(alice, Home));
        TickUntil(() => !Loaded(Far), "the far waystone's chunk never unloaded");
        var player = PlayerOf(alice);
        player.Status.Mana.Refill();

        // No time passes while the trip is judged, so the drained pool cannot regenerate before it is read
        Assert.True(Trip(alice, Far, dt: 0));
        Assert.Equal(0f, player.Status.Mana.Current);
        TickUntil(() => inbox.ManaSeen.Contains(0), "the drained pool never reached the client", dt: 0);
        // Held over the destination while it loads, immune: the states the client keeps sending from home move nothing
        // and are never corrected, so the one correction of the whole trip is its landing
        Assert.NotNull(player.WaystoneTrip);
        Assert.True(player.IsImmune);
        var correctionsBefore = inbox.Corrections.Count;
        TickUntil(() =>
        {
            SendState(alice, EyeBeside(Home), ack: 0);
            return inbox.Corrections.Count > correctionsBefore;
        }, "the traveler never landed");
        for (var i = 0; i < 10; i++) Tick(0.02);
        Assert.Equal(correctionsBefore + 1, inbox.Corrections.Count);
        Assert.Null(player.WaystoneTrip);
        var (seq, eye) = inbox.Corrections[^1];
        Assert.Equal(player.Position, eye);
        var feet = eye.Y - Camera.EyeHeight;
        Assert.InRange(feet, Far.Y - 0.01f, Far.Y + 0.1f);
        var away = new Vector2(eye.X - (Far.X + 0.5f), eye.Z - (Far.Z + 0.5f)).Length();
        Assert.InRange(away, 0.5f, WaystoneArrival.SearchRadius * 1.5f);

        // Once the client acks the landing its states count again, and standing there is legal
        SendState(alice, eye, seq);
        for (var i = 0; i < 10; i++) Tick(0.02);
        Assert.Equal(seq, inbox.Corrections[^1].Seq);
        Assert.Equal(eye, player.Position);
    }

    [Fact]
    public void ATripIsRefusedWithoutEnoughManaUnlessCreativeAndOnlyFromAWaystoneThePlayerStandsAt()
    {
        var alice = Connect("Alice");
        var inbox = _inboxes[alice];
        (int X, int Y, int Z) other = (Home.X + 6, Home.Y, Home.Z);
        BuildBeside(alice, other);
        Assert.True(Activate(alice, other));
        BuildBeside(alice, Home);
        Assert.True(Activate(alice, Home));
        var player = PlayerOf(alice);

        // Exactly half the pool is not more than half; no time passes, so it does not regenerate past it
        player.Status.Mana.Restore(player.Status.Mana.Max / 2f);
        var mana = player.Status.Mana.Current;
        Assert.False(Trip(alice, other, dt: 0));
        Assert.Equal(mana, player.Status.Mana.Current);
        Assert.Null(player.WaystoneTrip);

        // A creative traveler goes free
        player.Creative = true;
        var corrections = inbox.Corrections.Count;
        Assert.True(Trip(alice, other, dt: 0));
        Assert.Equal(mana, player.Status.Mana.Current);
        TickUntil(() => inbox.Corrections.Count > corrections, "the creative traveler never landed");
        player.Creative = false;

        // Having landed, the player stands at no waystone it activated: a trip home is refused until it activates one
        player.Status.Mana.Refill();
        Assert.False(Trip(alice, Home));
        Assert.True(player.Status.Mana.MoreThanHalf);

        // Far from the waystone it activated, a trip is refused too
        Assert.True(Activate(alice, other));
        player.Teleport(EyeBeside(other) + new Vector3(0f, 0f, 30f));
        Assert.False(Trip(alice, Home));
        Assert.True(player.Status.Mana.MoreThanHalf);
    }

    [Fact]
    public void ATripToAnUndiscoveredOrLostWaystoneIsRefused()
    {
        var alice = Connect("Alice");
        var bob = Connect("Bob");
        (int X, int Y, int Z) other = (Home.X + 6, Home.Y, Home.Z);
        BuildBeside(alice, other);
        Assert.True(Activate(alice, other));
        BuildBeside(alice, Home);
        Assert.True(Activate(alice, Home));
        PlayerOf(bob).Teleport(EyeBeside(Home));
        Assert.True(Activate(bob, Home));

        // Bob never found the other waystone
        Assert.False(Trip(bob, other));
        Assert.True(PlayerOf(bob).Status.Mana.MoreThanHalf);

        // Broken, it stays in Alice's list, lost, and cannot be traveled to
        var chunk = _world.GetChunkManager().ChunkAt(other.X, other.Y, other.Z)!;
        var (lx, ly, lz) = VoxelCoords.LocalOf(other.X, other.Y, other.Z);
        chunk.SetBlockRaw(lx, ly, lz, 0, 0);
        TickUntil(() => _inboxes[alice].RowAt(other).Lost, "the loss never reached the discoverer's rows");
        Assert.False(_inboxes[alice].RowAt(other).MayRename);
        Assert.False(Trip(alice, other));
        Assert.True(PlayerOf(alice).Status.Mana.MoreThanHalf);
    }
}
