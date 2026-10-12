using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using VoxelEngine.Data;
using VoxelEngine.Lod;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using VoxelEngine.Saves;
using VoxelEngine.Server;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// An imported world end to end over the real server path: a server-mode <see cref="World"/> built
/// over an <see cref="ImportedMapSource"/> plays the map's terrain (its chunks fill through
/// <see cref="ChunkManager.CreateChunk"/> and its spawn settles on the map's surface), streams the
/// map's chunks, columns and far LOD to a client that never generates anything, and saves an edit
/// to an imported chunk so the save beats the map on reload while the map still fills every chunk
/// the save does not hold.
/// </summary>
[Collection("Worldgen seed")]
public sealed class ImportedWorldStreamingTests : IDisposable
{
    private const int ColumnArea = Chunk.Size * Chunk.Size;

    // The map supplies chunk columns [-MapRadius, MapRadius] on both axes, around the spawn and over
    // every column the far LOD's level-0 region (0, 0) reads for its interior.
    private const int MapRadius = 2;

    // The map writes the plateau's top chunk and this many chunks under it; below them a supplied
    // column is void (an absent chunk file is air).
    private const int MapDepthChunks = 2;

    // How far above worldgen's terrain (and anything worldgen's far LOD draws there) the plateau
    // stands, so no generated column could hold the same surface.
    private const int PlateauLift = 24;

    private const float MapWarmth = 0.95f;
    private const float MapWetness = 0.05f;
    private const int LodLevel = 0;

    // The interior of a level-0 region: region-local X and Z from half a chunk into its first chunk
    // column to half a chunk into its second, whose every cell read stays on the map.
    private const float InteriorMin = Chunk.Size / 2f;
    private const float InteriorMax = Chunk.Size + Chunk.Size / 2f;

    // A cell of chunk column (0, 0), away from the spawn column the camera hovers over.
    private const int CellX = 5;
    private const int CellZ = 9;

    private static readonly TimeSpan LoadBudget = TimeSpan.FromSeconds(45);

    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ImportedWorldStreamingTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // The world's save folder and the map folder beside it.
    private string SaveDir => Path.Combine(_root, "save");
    private string MapDir => Path.Combine(_root, "map");

    private static byte Sandstone => ContentRegistry.RequireBlock("sandstone").Id;

    private static bool InInterior(Vertex v) =>
        v.Position.X >= InteriorMin && v.Position.X < InteriorMax
        && v.Position.Z >= InteriorMin && v.Position.Z < InteriorMax;

    /// <summary>The map as written: its plateau top, the column every supplied column carries, and
    /// worldgen's own far LOD of region (0, 0) to compare the server's against.</summary>
    private sealed record Map(int Plateau, ColumnData Column, LodMeshData GeneratedLod);

    /// <summary>
    /// Writes the map with <see cref="ImportedMapWriter"/>: every column of
    /// [-MapRadius, MapRadius]² a flat dry desert at the plateau, and the plateau's top chunk plus
    /// <see cref="MapDepthChunks"/> under it filled with sandstone up to the plateau.
    /// </summary>
    private Map BuildMap()
    {
        var generatedLod = LodRegion.BuildMeshData(LodLevel, 0, 0);
        var tallest = (int)MathF.Ceiling(generatedLod.Vertices.Where(InInterior).Max(v => v.Position.Y));
        for (var cx = -MapRadius; cx <= MapRadius; cx++)
        for (var cz = -MapRadius; cz <= MapRadius; cz++)
            tallest = Math.Max(tallest, ColumnCache.Get(cx, cz).MaxHeight);

        // The top cell and the air over it share a chunk, so one streamed chunk carries both
        var plateau = tallest + PlateauLift;
        if ((plateau & Chunk.SizeMask) == Chunk.SizeMask)
            plateau++;

        var column = FlatColumn(plateau);
        var topCy = plateau >> Chunk.SizeShift;
        for (var cx = -MapRadius; cx <= MapRadius; cx++)
        for (var cz = -MapRadius; cz <= MapRadius; cz++)
        {
            ImportedMapWriter.WriteColumn(MapDir, cx, cz, column);
            for (var cy = topCy - MapDepthChunks; cy <= topCy; cy++)
                ImportedMapWriter.WriteChunk(MapDir, cx, cy, cz, Slab(cy, plateau, Sandstone));
        }

        return new Map(plateau, column, generatedLod);
    }

    private static ColumnData FlatColumn(int top)
    {
        var heights = Enumerable.Repeat(top, ColumnArea).ToArray();
        var water = Enumerable.Repeat(TerrainGenerator.NoWater, ColumnArea).ToArray();
        var biomes = Enumerable.Repeat(Biome.Desert, ColumnArea).ToArray();
        var dirt = Enumerable.Repeat((byte)WorldGenerationConfig.NominalDirtDepth, ColumnArea).ToArray();
        return new ColumnData(heights, dirt, new float[ColumnArea], new float[ColumnArea], biomes,
            (int[])heights.Clone(), water, MapWarmth, MapWetness);
    }

    // A chunk whose cells at or below top are block and the rest air
    private static ChunkSnapshot Slab(int cy, int top, byte block)
    {
        var blocks = new byte[Chunk.BlockCount];
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            if (cy * Chunk.Size + y <= top)
                blocks[Chunk.Idx(x, y, z)] = block;
        return new ChunkSnapshot(blocks, state: null, playerPlaced: null);
    }

    /// <summary>A server-mode world over the save folder whose terrain comes from the map, exactly
    /// the World the dedicated server builds for a world whose source is <c>imported</c>.</summary>
    private World OpenServerWorld()
    {
        var source = new ImportedMapSource(MapDir);
        return new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(SaveDir),
            serverMode: true, worldSource: source);
    }

    private static void PumpUntilLoaded(World world)
    {
        var sw = Stopwatch.StartNew();
        while (!world.IsLoaded && sw.Elapsed < LoadBudget)
        {
            world.Update(0.05, null);
            Thread.Sleep(1);
        }

        Assert.True(world.IsLoaded, "the imported world never finished loading");
    }

    /// <summary>Pumps until the spawn column is loaded from the plateau up past the reach the
    /// spawn settle scans from, and the eye has then stopped moving for a while.</summary>
    private static void PumpUntilSettled(World world, int plateau)
    {
        var manager = world.GetChunkManager();
        var sx = (int)MathF.Floor(world.Camera.Position.X);
        var sz = (int)MathF.Floor(world.Camera.Position.Z);

        bool ScanLoaded() =>
            manager.TryGetCell(sx, plateau, sz, out _, out _)
            && manager.TryGetCell(sx, plateau + TreeGenerator.ReachUp + 3, sz, out _, out _);

        var sw = Stopwatch.StartNew();
        var previousY = float.NaN;
        var stableFrames = 0;
        while (stableFrames < 10 && sw.Elapsed < LoadBudget)
        {
            world.Update(0.05, null);
            var y = world.Camera.Position.Y;
            stableFrames = ScanLoaded() && y == previousY ? stableFrames + 1 : 0;
            previousY = y;
            Thread.Sleep(1);
        }

        Assert.True(stableFrames >= 10, "the spawn never settled over the map");
    }

    /// <summary>Pumps until every given cell is in a loaded chunk.</summary>
    private static void PumpUntilCellsLoaded(World world, params Vector3[] cells)
    {
        var manager = world.GetChunkManager();
        bool AllLoaded() => cells.All(c => manager.TryGetCell((int)c.X, (int)c.Y, (int)c.Z, out _, out _));

        var sw = Stopwatch.StartNew();
        while (!AllLoaded() && sw.Elapsed < LoadBudget)
        {
            world.Update(0.05, null);
            Thread.Sleep(1);
        }

        Assert.True(AllLoaded(), "the cells under test never loaded");
    }

    private static byte CellOf(World world, Vector3 cell)
    {
        Assert.True(world.GetChunkManager().TryGetCell((int)cell.X, (int)cell.Y, (int)cell.Z, out var id, out _),
            $"cell {cell} is not loaded");
        return id;
    }

    private static void AssertSameCells(Chunk expected, Chunk actual)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            Assert.True(expected.GetLocalBlock(x, y, z) == actual.GetLocalBlock(x, y, z),
                $"block ({x}, {y}, {z}) of {expected.Position} differs");
            Assert.True(expected.GetLocalState(x, y, z) == actual.GetLocalState(x, y, z),
                $"state ({x}, {y}, {z}) of {expected.Position} differs");
        }
    }

    private static NetworkClient Connect(GameServer server)
    {
        var client = new NetworkClient(new ServerAddress("127.0.0.1", server.BoundPort), "Tester", 5);
        var connect = new Thread(client.Connect) { IsBackground = true, Name = "import-test-connect" };
        connect.Start();

        var sw = Stopwatch.StartNew();
        while (client.Status == NetworkStatus.Connecting && sw.Elapsed.TotalSeconds < 5)
        {
            server.Pump(0.01);
            Thread.Sleep(5);
        }

        Assert.Equal(NetworkStatus.Handshaken, client.Status);
        return client;
    }

    [Fact]
    public void TheServerPlaysTheImportedTerrainAndStreamsItToAClient()
    {
        var map = BuildMap();
        var plateau = map.Plateau;
        var sandstone = Sandstone;
        var topCy = plateau >> Chunk.SizeShift;
        var top = new Vector3(CellX, plateau, CellZ);
        var above = top + Vector3.UnitY;

        var world = OpenServerWorld();
        try
        {
            PumpUntilLoaded(world);
            PumpUntilSettled(world, plateau);
            var manager = world.GetChunkManager();
            Assert.IsType<ImportedMapSource>(manager.WorldSource);

            // (1) The map's blocks are the live world: its sandstone top with its air over it, far
            // above worldgen's own surface in that column
            Assert.True(ColumnCache.GetHeights(0, 0)[VoxelCoords.ColumnIndex(CellX, CellZ)] < plateau);
            Assert.Equal(sandstone, CellOf(world, top));
            Assert.Equal(BlockType.Air, CellOf(world, above));

            // The ground and the spawn follow the map's surface, not worldgen's
            Assert.True(world.TryGroundHeight(CellX, CellZ, out var ground));
            Assert.Equal(plateau, ground);
            Assert.True(world.TryGroundHeight(0, 0, out var spawnGround));
            Assert.Equal(plateau, spawnGround);
            Assert.True(world.Camera.Position.Y > plateau, "the eye did not settle above the map's surface");

            // (2) A client that only ever streams: a bare chunk manager fed by a ClientChunkStreamer,
            // never a world, so nothing on its side generates
            using var server = new GameServer(world, "Test", WorldSeed.Default, 0);
            server.Start();
            using var client = Connect(server);

            using (var request = new PacketWriter(NetMessage.ChunkRequest))
            {
                request.Write(0);
                request.Write(topCy);
                request.Write(0);
                client.Send(request.ToArray());
            }

            using (var request = new PacketWriter(NetMessage.ColumnRequest))
            {
                request.Write(0);
                request.Write(0);
                client.Send(request.ToArray());
            }

            using (var request = new PacketWriter(NetMessage.LodRegionRequest))
            {
                request.Write(LodLevel);
                request.Write(0);
                request.Write(0);
                client.Send(request.ToArray());
            }

            var clientChunks = new ChunkManager(null!);
            var streamer = new ClientChunkStreamer(clientChunks, (_, _, _) => { }, (_, _) => { });
            var gotChunk = false;
            var gotColumn = false;
            LodMeshData? region = null;
            var sw = Stopwatch.StartNew();
            while ((!gotChunk || !gotColumn || region == null) && sw.Elapsed.TotalSeconds < 15)
            {
                server.Pump(0.01);
                while (client.Inbound.TryDequeue(out var payload))
                {
                    using var reader = new PacketReader(payload);
                    switch (reader.Message)
                    {
                        case NetMessage.ChunkData:
                        {
                            var rx = reader.ReadInt();
                            var ry = reader.ReadInt();
                            var rz = reader.ReadInt();
                            streamer.ApplyChunk(rx, ry, rz, reader.ReadBytes());
                            gotChunk = true;
                            break;
                        }
                        case NetMessage.ColumnTerrain:
                        {
                            var rx = reader.ReadInt();
                            var rz = reader.ReadInt();
                            streamer.ApplyColumn(rx, rz, reader.ReadBytes());
                            gotColumn = true;
                            break;
                        }
                        case NetMessage.LodRegionData:
                            reader.ReadInt();
                            reader.ReadInt();
                            reader.ReadInt();
                            region = LodNetCodec.Decode(reader.ReadBytes());
                            break;
                    }
                }

                Thread.Sleep(5);
            }

            Assert.True(gotChunk, "no chunk data came back from the server");
            Assert.True(gotColumn, "no column terrain came back from the server");
            Assert.NotNull(region);

            // The streamed chunk is the server's, cell for cell: the map's plateau top and the air
            // over it
            var chunkPos = new Vector3(0, topCy, 0);
            AssertSameCells(manager.Chunks[chunkPos], clientChunks.Chunks[chunkPos]);
            Assert.Equal(sandstone, clientChunks.GetBlockGlobal(CellX, plateau, CellZ));
            Assert.Equal(BlockType.Air, clientChunks.GetBlockGlobal(CellX, plateau + 1, CellZ));

            // The streamed column is the map's, which worldgen's column there is not: the client got
            // the source's terrain, and derived none of it itself
            Assert.True(clientChunks.ColumnSource.TryGet(0, 0, out var streamed));
            Assert.Equal(map.Column.Heights, streamed.Heights);
            Assert.Equal(map.Column.WaterLevel, streamed.WaterLevel);
            Assert.Equal(BitConverter.SingleToInt32Bits(map.Column.CornerWarmth), BitConverter.SingleToInt32Bits(streamed.Warmth));
            Assert.Equal(BitConverter.SingleToInt32Bits(map.Column.CornerWetness), BitConverter.SingleToInt32Bits(streamed.Wetness));
            Assert.True(WorldgenColumnSource.Instance.TryGet(0, 0, out var worldgen));
            Assert.False(streamed.ContentEquals(worldgen), "the client received worldgen's column, not the map's");

            // (3) The far LOD the server built rose to the map's terrain, above anything worldgen's
            // far LOD draws over the same interior
            Assert.Equal(LodLevel, region!.Level);
            var interior = region.Vertices.Where(InInterior).ToList();
            Assert.NotEmpty(interior);
            var generatedMaxY = map.GeneratedLod.Vertices.Where(InInterior).Max(v => v.Position.Y);
            Assert.True(interior.Max(v => v.Position.Y) > generatedMaxY,
                "the server's far LOD did not rise to the map's terrain");
        }
        finally
        {
            world.SaveAndDispose();
        }
    }

    [Fact]
    public void AnEditedImportedChunkIsSavedAndWinsOnReload()
    {
        var map = BuildMap();
        var plateau = map.Plateau;
        var sandstone = Sandstone;
        var topCy = plateau >> Chunk.SizeShift;

        // The mined cell, a cell of the same chunk left alone, and a cell of the chunk beside it on X
        var mined = new Vector3(CellX, plateau, CellZ);
        var sameChunk = mined + Vector3.UnitX;
        var nextChunk = mined + new Vector3(Chunk.Size, 0, 0);

        var world = OpenServerWorld();
        var disposed = false;
        try
        {
            PumpUntilLoaded(world);
            PumpUntilCellsLoaded(world, mined, nextChunk);
            Assert.Equal(sandstone, CellOf(world, mined));
            Assert.Equal(sandstone, CellOf(world, nextChunk));

            var player = new PlayerContext(Inventory.Empty()) { IsCreative = true, Eye = world.Camera.Position };
            Assert.True(world.BreakBlockFor(player, mined));
            Assert.Equal(BlockType.Air, CellOf(world, mined));

            disposed = true;
            Assert.True(world.SaveAndDispose(), "the imported world did not save cleanly");
        }
        finally
        {
            if (!disposed)
                world.SaveAndDispose();
        }

        // The save holds the edited chunk of the map and not its untouched neighbor
        string SavedChunk(int cx) => Path.Combine(SaveDir, WorldStore.ChunksDirectoryName, WorldStore.ChunkFileName(cx, topCy, 0));
        Assert.True(File.Exists(SavedChunk(0)), "the edited imported chunk was not saved");
        Assert.False(File.Exists(SavedChunk(1)), "an imported chunk nobody edited was saved");

        // Reopened over the same save and a fresh read of the map, as the server would
        WorldSeed.Apply(WorldSeed.Default);
        var reopened = OpenServerWorld();
        try
        {
            PumpUntilLoaded(reopened);
            PumpUntilCellsLoaded(reopened, mined, sameChunk, nextChunk);

            // The save beat the map in the chunk it holds, and still carries the rest of the map there
            Assert.Equal(BlockType.Air, CellOf(reopened, mined));
            Assert.Equal(sandstone, CellOf(reopened, sameChunk));

            // The map still fills the chunk the save does not hold
            Assert.Equal(sandstone, CellOf(reopened, nextChunk));
        }
        finally
        {
            reopened.SaveAndDispose();
        }
    }
}
