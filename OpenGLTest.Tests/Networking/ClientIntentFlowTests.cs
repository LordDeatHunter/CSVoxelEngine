using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using VoxelEngine.Server;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// A real client-mode <see cref="World"/> against a real <see cref="GameServer"/> over loopback,
/// wired the way the game wires them (predictions staged through <see cref="World.BlockEdited"/>,
/// intents sent from <see cref="World.IntentRaised"/>, answers sorted by
/// <see cref="ClientPrediction"/>): the client's own mining and placing loops produce intents the
/// server accepts and replays, the client never rolls loot of its own, and both worlds end on the
/// same cells and the same inventory.
/// </summary>
[Collection("Worldgen seed")]
public sealed class ClientIntentFlowTests : IDisposable
{
    private const double Dt = 1.0 / 30.0;

    private readonly World _server;
    private readonly GameServer _host;
    private World? _client;
    private NetworkClient? _connection;
    private ClientChunkStreamer? _streamer;
    private readonly ClientPrediction _prediction = new();
    private readonly List<NetCellChange> _cells = [];
    private readonly List<NetCellChange> _apply = [];
    private readonly Dictionary<int, bool> _results = new();

    public ClientIntentFlowTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
        _server = new World(null!, null!, null!, null!, null!, null!, null, null, serverMode: true);
        _host = new GameServer(_server, "Test World", WorldSeed.Default, 0) { AllowCreative = true };
        _host.Start();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _host.Dispose();
        _client?.SaveAndDispose();
        _server.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static byte Dirt => ContentRegistry.RequireBlock("dirt").Id;

    private void Connect()
    {
        _connection = new NetworkClient(new ServerAddress("127.0.0.1", _host.BoundPort), "Client", 5.0);
        new Thread(_connection.Connect) { IsBackground = true, Name = "test-connect" }.Start();
        var sw = Stopwatch.StartNew();
        while (_connection.Status == NetworkStatus.Connecting && sw.Elapsed.TotalSeconds < 15)
        {
            _host.Pump(0.01);
            Thread.Sleep(5);
        }
        Assert.Equal(NetworkStatus.Handshaken, _connection.Status);

        _client = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        _client.GetChunkManager().Headless = true;
        _client.BlockEdited = (x, y, z, _, _) => _prediction.Stage(x, y, z);
        _client.IntentRaised = SendIntent;
        _streamer = new ClientChunkStreamer(_client.GetChunkManager(),
            (cx, cy, cz) => Send(NetMessage.ChunkRequest, w => { w.Write(cx); w.Write(cy); w.Write(cz); }),
            (cx, cz) => Send(NetMessage.ColumnRequest, w => { w.Write(cx); w.Write(cz); }));
    }

    private void Send(NetMessage message, Action<PacketWriter> body)
    {
        using var writer = new PacketWriter(message);
        body(writer);
        _connection!.Send(writer.ToArray());
    }

    private void SendIntent(WorldIntent intent) =>
        Send(NetMessage.WorldIntent, w => WorldIntentCodec.Write(w, _prediction.Commit(intent.PredictsInventory), intent));

    /// <summary>What Program.HandleNetworkMessage does with the messages this flow needs.</summary>
    private void HandleInbound()
    {
        var world = _client!;
        while (_connection!.Inbound.TryDequeue(out var payload))
        {
            using var reader = new PacketReader(payload);
            switch (reader.Message)
            {
                case NetMessage.ChunkData:
                {
                    var cx = reader.ReadInt();
                    var cy = reader.ReadInt();
                    var cz = reader.ReadInt();
                    _streamer!.ApplyChunk(cx, cy, cz, reader.ReadBytes());
                    break;
                }
                case NetMessage.ColumnTerrain:
                {
                    var cx = reader.ReadInt();
                    var cz = reader.ReadInt();
                    _streamer!.ApplyColumn(cx, cz, reader.ReadBytes());
                    break;
                }
                case NetMessage.BlockCorrection:
                {
                    var seq = reader.ReadInt();
                    ReadCells(reader);
                    _apply.Clear();
                    foreach (var cell in _cells)
                        if (_prediction.Offer(cell, seq))
                            _apply.Add(cell);
                    world.ApplyNetworkBlockChanges(_apply);
                    break;
                }
                case NetMessage.BlockChanges:
                    ReadCells(reader);
                    _apply.Clear();
                    foreach (var cell in _cells)
                        if (_prediction.Offer(cell, null))
                            _apply.Add(cell);
                    world.ApplyNetworkBlockChanges(_apply);
                    break;
                case NetMessage.IntentResult:
                {
                    var seq = reader.ReadInt();
                    _results[seq] = reader.ReadBool();
                    _apply.Clear();
                    _prediction.Resolve(seq, _apply);
                    world.ApplyNetworkBlockChanges(_apply);
                    break;
                }
                case NetMessage.InventoryState:
                {
                    var seq = reader.ReadInt();
                    var data = reader.ReadBytes();
                    if (_prediction.AcceptsInventory(seq))
                        world.Inventory.Import(InventoryNetCodec.Decode(data));
                    break;
                }
            }
        }
    }

    private void ReadCells(PacketReader reader)
    {
        var count = reader.ReadInt();
        _cells.Clear();
        for (var i = 0; i < count; i++)
            _cells.Add(new NetCellChange(reader.ReadInt(), reader.ReadInt(), reader.ReadInt(), reader.ReadByte(),
                reader.ReadByte()));
    }

    /// <summary>
    /// One frame of both sides, the server first, as each runs its own loop. The frame ends once
    /// the server has answered every intent sent so far (pumped without advancing its clock), so
    /// loopback scheduling never shifts one intent against another in server time.
    /// </summary>
    private void Frame(bool mining = false)
    {
        _server.Update(Dt, null);
        _host.Pump(Dt);
        Thread.Sleep(1);
        HandleInbound();
        _streamer!.Update(_client!.Camera.Position, Dt);
        _client.Update(Dt, null);
        _client.UpdateBlockBreaking(Dt, mining);

        var sw = Stopwatch.StartNew();
        while (_prediction.LastSeq > 0 && !_results.ContainsKey(_prediction.LastSeq) && sw.Elapsed.TotalSeconds < 5)
        {
            _host.Pump(0);
            Thread.Sleep(1);
            HandleInbound();
        }
    }

    private (byte Id, byte State) ServerCell(Vector3 cell)
    {
        Assert.True(_server.GetChunkManager().TryGetCell((int)cell.X, (int)cell.Y, (int)cell.Z, out var id, out var state));
        return (id, state);
    }

    private byte ClientCell(Vector3 cell) =>
        _client!.GetChunkManager().TryGetCell((int)cell.X, (int)cell.Y, (int)cell.Z, out var id, out _) ? id : (byte)255;

    private void SetServerCell(int x, int y, int z, byte id)
    {
        var chunk = _server.GetChunkManager().ChunkAt(x, y, z)!;
        var (lx, ly, lz) = VoxelCoords.LocalOf(x, y, z);
        chunk.SetBlockRaw(lx, ly, lz, id, 0);
    }

    [Fact]
    public void TheClientsOwnMiningAndPlacingAreReplayedByTheServer()
    {
        Connect();
        var eye = _connection!.Server!.Value.Spawn;
        var fx = (int)MathF.Floor(eye.X);
        var fy = (int)MathF.Floor(eye.Y - Camera.EyeHeight + 1e-3f);
        var fz = (int)MathF.Floor(eye.Z);

        // The same cleared pocket the server tests use: a dirt floor under the feet, air above,
        // and one dirt block at eye height two blocks along +X
        var manager = _server.GetChunkManager();
        var sw = Stopwatch.StartNew();
        bool ServerLoaded()
        {
            for (var x = fx - 3; x <= fx + 3; x++)
            for (var y = fy - 1; y <= fy + 4; y++)
            for (var z = fz - 3; z <= fz + 3; z++)
                if (!manager.TryGetCell(x, y, z, out _, out _))
                    return false;
            return true;
        }
        while (!ServerLoaded() && sw.Elapsed.TotalSeconds < 60)
        {
            _server.Update(0.05, null);
            _host.Pump(0.05);
        }
        Assert.True(ServerLoaded());
        for (var x = fx - 3; x <= fx + 3; x++)
        for (var y = fy - 1; y <= fy + 4; y++)
        for (var z = fz - 3; z <= fz + 3; z++)
            SetServerCell(x, y, z, y == fy - 1 ? Dirt : (byte)0);
        var front = new Vector3(fx + 2, fy + 1, fz);
        SetServerCell(fx + 2, fy + 1, fz, Dirt);

        // The client stands on the pocket's floor, aimed at the middle of the front block
        var standing = new Vector3(fx + 0.5f, fy + Camera.EyeHeight + 0.01f, fz + 0.5f);
        var toFront = front + new Vector3(0.5f) - standing;
        var pitch = MathF.Atan2(toFront.Y, new Vector2(toFront.X, toFront.Z).Length()) * 180f / MathF.PI;
        _client!.Camera.TestTeleport(standing, 0f, pitch);
        sw.Restart();
        while (ClientCell(front) != Dirt && sw.Elapsed.TotalSeconds < 30)
            Frame();
        Assert.Equal(Dirt, ClientCell(front));

        // Hold the button: the client mines at its own pace and the server agrees
        sw.Restart();
        while (ClientCell(front) != 0 && sw.Elapsed.TotalSeconds < 30)
            Frame(mining: true);
        Assert.Equal(0, ClientCell(front));
        // A client never rolls loot of its own
        Assert.Empty(_client.Drops);
        sw.Restart();
        while ((ServerCell(front).Id != 0 || _prediction.PendingCellCount > 0) && sw.Elapsed.TotalSeconds < 15)
            Frame();
        Assert.Equal(0, ServerCell(front).Id);
        Assert.DoesNotContain(false, _results.Values);

        // The loot reaches this client's inventory from the server, not from its own break
        sw.Restart();
        while (_client.Inventory.CountFor(Dirt) == 0 && sw.Elapsed.TotalSeconds < 15)
            Frame();
        Assert.True(_client.Inventory.CountFor(Dirt) > 0, "the mined dirt never reached the client's inventory");
        Assert.True(_host.TryGetPlayer(_connection.Server!.Value.PlayerId, out var player));
        Assert.Equal(player.Inventory.CountFor(Dirt), _client.Inventory.CountFor(Dirt));

        // Placing it back: the client's own right-click goes out as a placement the server makes
        var owned = player.Inventory.CountFor(Dirt);
        Assert.Equal(Dirt, _client.Inventory.SelectedBlockId);
        var floorAhead = new Vector3(fx + 2, fy - 1, fz);
        var toFloor = new Vector3(fx + 2.5f, fy - 0.001f, fz + 0.5f) - _client.Camera.Position;
        var down = MathF.Atan2(toFloor.Y, new Vector2(toFloor.X, toFloor.Z).Length()) * 180f / MathF.PI;
        _client.Camera.TestTeleport(_client.Camera.Position, 0f, down);
        Frame();
        Assert.True(_client.TryUseSelected(World.InteractionReach));
        var placed = floorAhead + Vector3.UnitY;
        Assert.Equal(Dirt, ClientCell(placed));
        sw.Restart();
        while ((ServerCell(placed).Id != Dirt || _prediction.PendingCellCount > 0) && sw.Elapsed.TotalSeconds < 15)
            Frame();
        Assert.Equal(Dirt, ServerCell(placed).Id);
        Assert.Equal(owned - 1, player.Inventory.CountFor(Dirt));
        Assert.Equal(player.Inventory.CountFor(Dirt), _client.Inventory.CountFor(Dirt));
        Assert.DoesNotContain(false, _results.Values);
    }
}
