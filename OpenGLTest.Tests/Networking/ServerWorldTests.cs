using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using VoxelEngine.Saves;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// A server-mode <see cref="World"/> runs headlessly over real terrain: no geometry pools, no LOD
/// threads, its chunks marked meshed by the headless path. This is the check that the dedicated
/// server can host a saved world without a GL context, and that it streams terrain around every
/// connected player rather than around one point.
/// </summary>
[Collection("Worldgen seed")]
public sealed class ServerWorldTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private World? _world;

    public ServerWorldTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        _world?.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void HeadlessServerWorldStreamsAndPlaysRealTerrain()
    {
        WorldSeed.Apply(WorldSeed.Default);
        var store = new ChunkSaveStore(_dir);
        _world = new World(null!, null!, null!, null!, null!, null!, null, store, serverMode: true);

        Assert.True(_world.GetChunkManager().Headless);
        Assert.True(WorldSeed.WorldIsLive);

        var sw = Stopwatch.StartNew();
        var last = 0.0;
        while (!_world.IsLoaded && sw.Elapsed.TotalSeconds < 45)
        {
            var now = sw.Elapsed.TotalSeconds;
            _world.Update(Math.Min(now - last, 0.1), null);
            last = now;
            Thread.Sleep(1);
        }

        Assert.True(_world.IsLoaded, "server world never left Loading over real terrain");
    }

    [Fact]
    public void APlacementNeverBuriesAConnectedPlayerButTheServerCameraIsNobody()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, serverMode: true);
        var manager = _world.GetChunkManager();
        var eye = _world.Camera.Position;
        var fx = (int)MathF.Floor(eye.X);
        var fy = (int)MathF.Floor(eye.Y - Camera.EyeHeight + 1e-3f);
        var fz = (int)MathF.Floor(eye.Z);

        bool Loaded()
        {
            for (var x = fx - 3; x <= fx + 3; x++)
            for (var y = fy - 1; y <= fy + 3; y++)
                if (!manager.TryGetCell(x, y, fz, out _, out _))
                    return false;
            return true;
        }

        var sw = Stopwatch.StartNew();
        while (!Loaded() && sw.Elapsed.TotalSeconds < 45)
        {
            _world.Update(0.05, null);
            Thread.Sleep(1);
        }
        Assert.True(Loaded());

        // A dirt floor under the feet row and air above it along the row
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        for (var x = fx - 3; x <= fx + 3; x++)
        for (var y = fy - 1; y <= fy + 3; y++)
        {
            var chunk = manager.ChunkAt(x, y, fz)!;
            var (lx, ly, lz) = VoxelCoords.LocalOf(x, y, fz);
            chunk.SetBlockRaw(lx, ly, lz, y == fy - 1 ? dirt : (byte)0, 0);
        }

        // The builder stands at the eye, another player two blocks along +X
        var builder = new ServerPlayer(1, "Builder", eye);
        var other = new ServerPlayer(2, "Other", eye + new Vector3(2f, 0f, 0f));
        _world.ServerPlayers = [builder, other];
        builder.Inventory.Add(dirt, 2);
        builder.Aim(eye, Vector3.UnitX);

        // The floor under the other player: the block would bury them, so nothing is placed or spent
        Assert.False(_world.TryPlaceSelectedBlockAt(builder.Context, new Vector3(fx + 2, fy - 1, fz), Vector3.UnitY));
        Assert.Equal(0, manager.GetBlockGlobal(fx + 2, fy, fz));
        Assert.Equal(2, builder.Inventory.CountFor(dirt));

        // The server camera standing in the cell is nobody's body
        _world.SetServerFocus(new Vector3(fx - 1.5f, fy + Camera.EyeHeight, fz + 0.5f));
        Assert.True(_world.TryPlaceSelectedBlockAt(builder.Context, new Vector3(fx - 2, fy - 1, fz), Vector3.UnitY));
        Assert.Equal(dirt, manager.GetBlockGlobal(fx - 2, fy, fz));
        Assert.Equal(1, builder.Inventory.CountFor(dirt));
    }

    [Fact]
    public void HostingWritesTheSavedPlayerBackNotTheServerCamera()
    {
        // A survival player saved standing somewhere: hosting forces the server camera creative,
        // flying and onto the players' centroid, none of which may reach player.json
        Directory.CreateDirectory(_dir);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var owned = Inventory.Empty();
        owned.Add(stone, 4);
        var saved = new PlayerSaveData
        {
            Position = [12.5f, 90f, -3.5f],
            Yaw = 45f,
            Pitch = -10f,
            Creative = false,
            Flying = false,
            Health = 70,
            RespawnEye = [1f, 80f, 2f],
            Inventory = owned.Export(),
        };
        File.WriteAllBytes(Path.Combine(_dir, PlayerSaveCodec.FileName), PlayerSaveCodec.ToBytes(saved));

        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_dir), serverMode: true);
        Assert.True(_world.Camera.IsCreative && _world.Camera.IsFlying);
        _world.SetServerFocus(new Vector3(500f, 120f, 500f));
        // Whatever the world's own inventory does on a server, the owner's file keeps theirs
        _world.Inventory.Add(stone, 50);
        _world.SaveAndDispose();

        var written = PlayerSaveCodec.TryParse(File.ReadAllBytes(Path.Combine(_dir, PlayerSaveCodec.FileName)));
        Assert.NotNull(written);
        Assert.Equal(saved.Position, written!.Position);
        Assert.Equal(saved.Yaw, written.Yaw);
        Assert.Equal(saved.Pitch, written.Pitch);
        Assert.False(written.Creative);
        Assert.False(written.Flying);
        Assert.Equal(saved.Health, written.Health);
        Assert.Equal(saved.RespawnEye, written.RespawnEye);
        Assert.NotNull(written.LightingClock);
        Assert.NotNull(written.Inventory);
        var reread = Inventory.Empty();
        reread.Import(written.Inventory!);
        Assert.Equal(owned.CountFor(stone), reread.CountFor(stone));
    }

    [Fact]
    public void AServerDropIsCollectedByTheNearestPlayerIntoTheirOwnInventory()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_dir), serverMode: true);
        var sw = Stopwatch.StartNew();
        var last = 0.0;
        while (!_world.IsLoaded && sw.Elapsed.TotalSeconds < 45)
        {
            var now = sw.Elapsed.TotalSeconds;
            _world.Update(Math.Min(now - last, 0.1), null);
            last = now;
            Thread.Sleep(1);
        }
        Assert.True(_world.IsLoaded);

        // Two players by the loaded spawn, both within pickup range of what falls at the first
        var eye = _world.Camera.Position;
        var near = new ServerPlayer(1, "Near", eye);
        var far = new ServerPlayer(2, "Far", eye + new Vector3(2.5f, 0f, 0f));
        _world.ServerPlayers = [near, far];

        // Someone else's stone dropped straight down at the near player's feet
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dropper = Inventory.Empty();
        dropper.Add(stone, 1);
        Assert.True(_world.ThrowSelected(false, eye, -Vector3.UnitY, dropper));
        Assert.Equal(0, dropper.CountFor(stone));

        sw.Restart();
        while (_world.Drops.Count > 0 && sw.Elapsed.TotalSeconds < 10)
        {
            _world.Update(1.0 / 60, null);
            Thread.Sleep(1);
        }

        Assert.Empty(_world.Drops);
        Assert.Equal(1, near.Inventory.CountFor(stone));
        Assert.True(near.InventoryChanged);
        Assert.Equal(0, far.Inventory.CountFor(stone));
        Assert.False(far.InventoryChanged);
        // The server camera is nobody: the world's own inventory never collects
        Assert.Equal(0, _world.Inventory.CountFor(stone));
    }

    [Fact]
    public void HostingANewWorldSavesNoPlayerPose()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_dir), serverMode: true);
        _world.SetServerFocus(new Vector3(500f, 120f, 500f));
        _world.SaveAndDispose();

        var written = PlayerSaveCodec.TryParse(File.ReadAllBytes(Path.Combine(_dir, PlayerSaveCodec.FileName)));
        Assert.NotNull(written);
        // Opened later in singleplayer, the world searches the surface for a survival spawn
        Assert.Null(written!.Position);
        Assert.False(written.Creative);
        Assert.False(written.Flying);
        Assert.Null(written.RespawnEye);
    }

    // The six face-neighbor steps, in chunks
    private static readonly (int X, int Y, int Z)[] FaceSteps =
        [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    // The cell a player's feet stand in (the epsilon absorbs the float error of eye - EyeHeight)
    private static (int X, int Y, int Z) FeetCell(ServerPlayer player) =>
        ((int)MathF.Floor(player.Position.X), (int)MathF.Floor(player.Position.Y - Camera.EyeHeight + 1e-3f),
            (int)MathF.Floor(player.Position.Z));

    [Fact]
    public void TwoPlayersFarApartBothStreamTerrainAndEditBesideThemselves()
    {
        // Far enough apart that neither player's chunk lies inside the other's load radius, so
        // each one's terrain can only come from streaming around that player itself
        const int apartBlocks = 500;
        Assert.True(apartBlocks > (WorldGenerationConfig.ChunkRadius + 1) * Chunk.Size,
            "the players must stand outside each other's load radius");

        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_dir), serverMode: true);
        var world = _world;
        var manager = world.GetChunkManager();
        var clock = Stopwatch.StartNew();
        var last = 0.0;

        void Pump()
        {
            var now = clock.Elapsed.TotalSeconds;
            world.Update(Math.Min(now - last, 0.1), null);
            last = now;
            Thread.Sleep(1);
        }

        // A player standing centred on its column's generated ground: the heightmap is pure
        // worldgen, so the far column needs no loaded chunk to be found
        ServerPlayer OnGround(int id, string name, int x, int z)
        {
            world.TryGroundHeight(x, z, out var ground);
            return new ServerPlayer(id, name, new Vector3(x + 0.5f, ground + 1 + Camera.EyeHeight, z + 0.5f));
        }

        Chunk? ChunkUnder(ServerPlayer player)
        {
            var (x, y, z) = FeetCell(player);
            return manager.ChunkAt(x, y, z);
        }

        // The chunk under the feet, its six face neighbors, and every cell of the pocket the edit
        // below clears (which may reach into an edge neighbor)
        bool Loaded(ServerPlayer player)
        {
            var (fx, fy, fz) = FeetCell(player);
            if (manager.ChunkAt(fx, fy, fz) == null)
                return false;
            foreach (var (dx, dy, dz) in FaceSteps)
                if (manager.ChunkAt(fx + dx * Chunk.Size, fy + dy * Chunk.Size, fz + dz * Chunk.Size) == null)
                    return false;
            for (var x = fx; x <= fx + 2; x++)
            for (var y = fy - 1; y <= fy + 1; y++)
                if (manager.ChunkAt(x, y, fz) == null)
                    return false;
            return true;
        }

        while (!world.IsLoaded && clock.Elapsed.TotalSeconds < 45)
            Pump();
        Assert.True(world.IsLoaded, "server world never left Loading over real terrain");

        var cam = world.Camera.Position;
        var nx = (int)MathF.Floor(cam.X);
        var nz = (int)MathF.Floor(cam.Z);
        var near = OnGround(1, "Near", nx, nz);
        var far = OnGround(2, "Far", nx + apartBlocks, nz);

        // The near player stands in what already streamed around the spawn
        var wait = Stopwatch.StartNew();
        while (!Loaded(near) && wait.Elapsed.TotalSeconds < 30)
            Pump();
        Assert.True(Loaded(near), "the near player's terrain never loaded around the spawn");
        var nearChunk = ChunkUnder(near);

        // Both players online: each one's neighbourhood streams in, and the near one's is never
        // dropped meanwhile (a chunk unloads only outside every player's radius)
        world.ServerPlayers = [near, far];
        wait.Restart();
        while (!(Loaded(near) && Loaded(far)) && wait.Elapsed.TotalSeconds < 60)
            Pump();
        Assert.True(Loaded(far), "the far player's terrain never streamed in");
        Assert.True(Loaded(near), "the near player's terrain did not stay loaded while the far one streamed in");
        Assert.Same(nearChunk, ChunkUnder(near));

        // Each player edits beside its own feet through its own context, the way the server applies
        // its intents: the aim meets the floor cell, a placement lands on it, and a break clears it
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        foreach (var player in new[] { near, far })
        {
            var (fx, fy, fz) = FeetCell(player);
            // A clean pocket from the body to two cells along +X over a stone floor, so the aim from
            // the eye meets nothing on its way down to the floor cell beside the feet
            for (var x = fx; x <= fx + 2; x++)
            for (var y = fy - 1; y <= fy + 1; y++)
            {
                var (lx, ly, lz) = VoxelCoords.LocalOf(x, y, fz);
                manager.ChunkAt(x, y, fz)!.SetBlockRaw(lx, ly, lz, y == fy - 1 ? stone : (byte)0, 0);
            }

            player.Inventory.Add(dirt, 1);
            var floor = new Vector3(fx + 2, fy - 1, fz);
            var floorTop = new Vector3(fx + 2.5f, fy, fz + 0.5f);
            player.Aim(player.Position, Vector3.Normalize(floorTop - player.Position));
            Assert.True(world.RaycastFor(player.Context, includeFluids: false, out var hit, out var normal),
                $"{player.Name}'s aim met nothing");
            Assert.Equal(floor, hit);
            Assert.Equal(Vector3.UnitY, normal);

            Assert.True(world.TryPlaceSelectedBlockAt(player.Context, hit, normal),
                $"{player.Name} could not place beside itself");
            var placed = floor + Vector3.UnitY;
            Assert.Equal(dirt, manager.GetBlockGlobal(fx + 2, fy, fz));
            Assert.Equal(0, player.Inventory.CountFor(dirt));

            // The same aim now meets the placed block, and breaking it clears the cell again
            Assert.True(world.RaycastFor(player.Context, includeFluids: false, out var target, out _));
            Assert.Equal(placed, target);
            Assert.True(world.BreakBlockFor(player.Context, placed), $"{player.Name} could not break beside itself");
            Assert.Equal(0, manager.GetBlockGlobal(fx + 2, fy, fz));
        }

        // The far player leaves: its terrain unloads within a few seconds, the near one's stays.
        // The frame cap lets the periodic unload scan run too, in case a generation worker
        // registered a far chunk just after the leave.
        world.ServerPlayers = [near];
        var (farX, farY, farZ) = FeetCell(far);
        for (var frame = 0; frame < 300 && (frame < 60 || manager.ChunkAt(farX, farY, farZ) != null); frame++)
        {
            world.Update(0.05, null);
            Thread.Sleep(1);
        }

        Assert.Null(manager.ChunkAt(farX, farY, farZ));
        Assert.Same(nearChunk, ChunkUnder(near));
    }
}
