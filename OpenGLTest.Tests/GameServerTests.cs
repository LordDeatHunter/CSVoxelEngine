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
/// A real <see cref="GameServer"/> over loopback: a malformed frame drops only its sender, every
/// joiner spawns at the world spawn rather than at the players' centroid, far-LOD requests
/// are built a bounded number per Pump with a repeated request served once, and every player's
/// inventory is its own: intents are replayed against the sender's inventory only, refused ones
/// are corrected by the snapshot the sender gets back, and creative grants need the server's
/// creative flag.
/// </summary>
[Collection("Worldgen seed")]
public sealed class GameServerTests : IDisposable
{
    private readonly World _world;
    private readonly GameServer _server;

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

    private static void SendIntent(NetworkClient client, InventoryIntent intent)
    {
        using var writer = new PacketWriter(NetMessage.InventoryRequest);
        InventoryIntentCodec.Write(writer, intent);
        client.Send(writer.ToArray());
    }

    // A PlayerState standing at the join spawn (so no move is clamped), with the given mode request
    private static void SendModes(NetworkClient client, NetPlayerFlags flags)
    {
        using var writer = new PacketWriter(NetMessage.PlayerState);
        writer.Write(client.Server!.Value.Spawn);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write((byte)flags);
        client.Send(writer.ToArray());
    }

    /// <summary>
    /// Pumps until <paramref name="client"/> has received at least one inventory snapshot (each
    /// intent is answered by exactly one) and returns the newest as the mirror the client imports.
    /// </summary>
    private Inventory AwaitInventory(NetworkClient client)
    {
        PlayerSaveData.InventoryData? latest = null;
        var sw = Stopwatch.StartNew();
        while (latest == null && sw.Elapsed.TotalSeconds < 15)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
            while (client.Inbound.TryDequeue(out var payload))
            {
                using var reader = new PacketReader(payload);
                if (reader.Message == NetMessage.InventoryState)
                    latest = InventoryNetCodec.Decode(reader.ReadBytes());
            }
        }

        Assert.NotNull(latest);
        var mirror = Inventory.Empty();
        mirror.Import(latest!);
        return mirror;
    }

    private ServerPlayer PlayerOf(NetworkClient client)
    {
        Assert.True(_server.TryGetPlayer(client.Server!.Value.PlayerId, out var player));
        return player;
    }

    private static byte Stone => ContentRegistry.RequireBlock("stone").Id;

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

        // A client asking for creative on a server that does not allow it stays survival
        _server.AllowCreative = false;
        SendModes(client, NetPlayerFlags.Creative | NetPlayerFlags.Flying);
        SendIntent(client, InventoryIntent.CreativeGrant(Stone, 5));
        Assert.Equal(0, AwaitInventory(client).CountFor(Stone));
        Assert.False(PlayerOf(client).Creative);

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
    public void ALaterJoinerSpawnsAtTheWorldSpawnNotTheCentroid()
    {
        using var first = Connect("First");
        var spawn = first.Server!.Value.Spawn;

        // The first player walks off; the server's chunk focus follows them
        PumpFor(0.1);
        var moved = spawn + new Vector3(1.5f, 0f, 0f);
        using (var state = new PacketWriter(NetMessage.PlayerState))
        {
            state.Write(moved);
            state.Write(0f);
            state.Write(0f);
            state.Write((byte)0);
            first.Send(state.ToArray());
        }

        var sw = Stopwatch.StartNew();
        while (Vector3.Distance(_world.Camera.Position, moved) > 0.01f && sw.Elapsed.TotalSeconds < 15)
        {
            _server.Pump(0.01);
            Thread.Sleep(5);
        }
        Assert.True(Vector3.Distance(_world.Camera.Position, moved) <= 0.01f, "the focus never followed the player");

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
}
