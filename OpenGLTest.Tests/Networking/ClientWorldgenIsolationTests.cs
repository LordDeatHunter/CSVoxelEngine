using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Lod;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using VoxelEngine.WorldGen;
using Xunit;
using Target = VoxelEngine.Tests.Networking.StreamedTerrainMeshTests.Target;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// A multiplayer client derives nothing from worldgen: a client-mode <see cref="World"/> whose
/// process seed is not the server's meshes the streamed chunks exactly as the server would, makes
/// no worldgen read on the way (<see cref="WorldgenReads"/>, noted on every column computed and
/// every climate evaluated), and its own edits never touch the far-LOD tables (the surface
/// overrides, the column caps and the felled-canopy markers), whose far LOD is the server's.
/// </summary>
/// <remarks>
/// The client's seed is poisoned (<see cref="WorldSeed.Apply"/> with a seed the server never
/// used), which also empties the column cache, so any worldgen read on the client path would both
/// be counted and yield different terrain. The counter and the LOD edit versions are
/// process-global, which is one reason this class runs in the serialised Worldgen seed collection.
/// </remarks>
[Collection("Worldgen seed")]
public sealed class ClientWorldgenIsolationTests
{
    private const int PoisonSeed = WorldSeed.Default ^ 0x5bd1e995;
    private const double Dt = 1.0 / 30.0;

    public ClientWorldgenIsolationTests() => TestContent.EnsureLoaded();

    /// <summary>What a server under the default seed streams for a target, and its own mesh of it.</summary>
    private sealed record ServerCapture(
        Vector3 Centre,
        Vector3 Top,
        List<(Vector3 Pos, byte[] Payload)> Chunks,
        List<(int X, int Z, byte[] Payload)> Columns,
        ChunkMeshData Mesh);

    private static ServerCapture CaptureServer(Target target)
    {
        // The seed-hashed emitters (pebbles, branch piles, untouched snow) are the one place a mesh
        // still reads the world seed, so the mesh comparison needs a chunk without them.
        var (centre, top) = StreamedTerrainMeshTests.FindTarget(target, withoutSeedHashedCells: true);
        var worldgen = StreamedTerrainMeshTests.GenerateNeighbourhood(centre);
        var mesh = worldgen.Chunks[centre].GenerateMeshData(worldgen, greedy: true);
        Assert.NotNull(mesh);
        StreamedTerrainMeshTests.AssertExercises(target, centre, mesh);
        return new ServerCapture(centre, top, StreamedTerrainMeshTests.CaptureChunks(worldgen, centre),
            StreamedTerrainMeshTests.CaptureColumns(centre), mesh);
    }

    /// <summary>
    /// A headless client world under the poisoned seed with its streamer installed, the camera in
    /// creative flight ten blocks over <paramref name="top"/> (so the streamed chunks around it stay
    /// in the load sphere and no placement near the top meets the body), settled for a frame.
    /// </summary>
    private static (World World, ChunkManager Manager, ClientChunkStreamer Streamer) NewClientWorld(Vector3 top)
    {
        var world = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        var manager = world.GetChunkManager();
        manager.Headless = true;
        var streamer = new ClientChunkStreamer(manager, (_, _, _) => { }, (_, _) => { });
        world.Camera.RestoreState(top + new Vector3(0.5f, 10f, 0.5f), -90f, -30f, creative: true, flying: true);
        Frames(world, manager);
        return (world, manager, streamer);
    }

    /// <summary>One frame that schedules, the drain of the mesh work, then the frame that applies it.</summary>
    private static void Frames(World world, ChunkManager manager)
    {
        world.Update(Dt, null);
        Assert.True(StreamedTerrainMeshTests.WaitUntil(() => StreamedTerrainMeshTests.Quiescent(manager)),
            "mesh dispatcher did not drain");
        world.Update(Dt, null);
    }

    [Theory]
    [InlineData(Target.Surface)]
    [InlineData(Target.Submerged)]
    public void AClientWorldMeshesStreamedChunksWithoutTouchingWorldgen(Target target)
    {
        var server = CaptureServer(target);
        World? world = null;
        try
        {
            WorldSeed.Apply(PoisonSeed);
            var (client, manager, streamer) = NewClientWorld(server.Top);
            world = client;

            WorldgenReads.Enabled = true;
            var before = WorldgenReads.Count;

            // The chunks first, then their columns, as the wire may deliver them; the columns'
            // arrival is what lets the centre mesh
            StreamedTerrainMeshTests.StreamChunks(streamer, server.Chunks);
            Frames(client, manager);
            StreamedTerrainMeshTests.StreamColumns(streamer, server.Columns);
            var centre = manager.Chunks[server.Centre];
            for (var i = 0; i < 20 && !centre.IsMeshed; i++)
                Frames(client, manager);
            Assert.True(centre.IsMeshed, "the streamed centre chunk never meshed");

            var mesh = centre.GenerateMeshData(manager, greedy: true);
            Assert.Equal(before, WorldgenReads.Count);
            Assert.NotNull(mesh);
            StreamedTerrainMeshTests.AssertSameMesh(server.Mesh, mesh);

            // The poison is real: the client's own worldgen would have made other terrain of this
            // column, so the equal meshes above cannot come from reading it
            WorldgenReads.Enabled = false;
            Assert.True(WorldgenColumnSource.Instance.TryGet((int)server.Centre.X, (int)server.Centre.Z, out var poisoned));
            Assert.True(streamer.TryGet((int)server.Centre.X, (int)server.Centre.Z, out var streamed));
            Assert.False(poisoned.ContentEquals(streamed), "the poisoned seed generates the server's column");
        }
        finally
        {
            WorldgenReads.Enabled = false;
            world?.SaveAndDispose();
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void AClientEditNeverMovesTheFarLodTables()
    {
        var server = CaptureServer(Target.Surface);
        World? world = null;
        try
        {
            WorldSeed.Apply(PoisonSeed);
            var (client, manager, streamer) = NewClientWorld(server.Top);
            world = client;
            StreamedTerrainMeshTests.StreamChunks(streamer, server.Chunks);
            StreamedTerrainMeshTests.StreamColumns(streamer, server.Columns);
            var centre = manager.Chunks[server.Centre];
            for (var i = 0; i < 20 && !centre.IsMeshed; i++)
                Frames(client, manager);
            Assert.True(centre.IsMeshed, "the streamed centre chunk never meshed");

            var player = new PlayerContext(client.Inventory) { Camera = client.Camera, IsCreative = true };
            var dirt = ContentRegistry.RequireBlock("dirt").Id;
            var leaves = ContentRegistry.RequireBlock("oak_leaves").Id;
            var top = server.Top;
            var above = top + Vector3.UnitY;

            WorldgenReads.Enabled = true;
            var reads = WorldgenReads.Count;
            var surfaces = LodSurface.EditVersion;
            var caps = LodRegion.CapEditVersion;
            var canopy = TreeGenerator.CanopyEditVersion;

            // Mining the surface top, which on a server lowers the column's far-LOD surface
            Assert.True(client.BreakBlockFor(player, top));
            Assert.Equal(BlockType.Air, CellAt(manager, top));

            // Placing a full cube back on it, which on a server raises it again
            Assert.True(client.Inventory.Add(dirt));
            Assert.True(client.Inventory.PickBlock(dirt));
            Assert.True(client.TryPlaceSelectedBlockAt(player, top - Vector3.UnitY, Vector3.UnitY));
            Assert.Equal(dirt, CellAt(manager, top));

            // A leaf placed on it and mined, which on a server disturbs the column's canopy
            Assert.True(client.Inventory.Add(leaves));
            Assert.True(client.Inventory.PickBlock(leaves));
            Assert.True(client.TryPlaceSelectedBlockAt(player, top, Vector3.UnitY));
            Assert.Equal(leaves, CellAt(manager, above));
            Assert.True(client.BreakBlockFor(player, above));
            Assert.Equal(BlockType.Air, CellAt(manager, above));

            Assert.Equal(surfaces, LodSurface.EditVersion);
            Assert.Equal(caps, LodRegion.CapEditVersion);
            Assert.Equal(canopy, TreeGenerator.CanopyEditVersion);
            Assert.Equal(reads, WorldgenReads.Count);
        }
        finally
        {
            WorldgenReads.Enabled = false;
            world?.SaveAndDispose();
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    private static byte CellAt(ChunkManager manager, Vector3 cell)
    {
        Assert.True(manager.TryGetCell((int)cell.X, (int)cell.Y, (int)cell.Z, out var id, out _), $"{cell} is not loaded");
        return id;
    }
}
