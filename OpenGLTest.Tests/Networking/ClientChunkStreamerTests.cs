using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Networking;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// The client's streamed-chunk path: an applied snapshot becomes readable blocks, the streamer
/// asks for the chunks around the camera and the 3x3 columns around each without duplicating a
/// request, re-asks an unanswered column like a chunk, and a streamed column (heights, water levels
/// and climate) is served as the client's column source.
/// </summary>
public sealed class ClientChunkStreamerTests
{
    public ClientChunkStreamerTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ApplyingAStreamedChunkMakesItsBlocksReadable()
    {
        var manager = new ChunkManager(null!);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var blocks = new byte[Chunk.BlockCount];
        blocks[Chunk.Idx(0, 0, 0)] = stone;

        manager.ApplyStreamedChunk(Vector3.Zero, new ChunkSnapshot(blocks, null, null));

        Assert.Equal(stone, manager.GetBlockGlobal(0, 0, 0));
    }

    [Fact]
    public void UpdateRequestsTheNearestChunksAndColumnsWithinABudget()
    {
        var manager = new ChunkManager(null!);
        var chunks = new List<(int X, int Y, int Z)>();
        var columns = new List<(int X, int Z)>();
        var streamer = new ClientChunkStreamer(manager, (x, y, z) => chunks.Add((x, y, z)), (x, z) => columns.Add((x, z)));

        streamer.Update(Vector3.Zero, 0.016);

        Assert.NotEmpty(chunks);
        Assert.NotEmpty(columns);
        Assert.True(chunks.Count <= 16, $"asked for {chunks.Count} chunks in one update");
        Assert.Equal((0, 0, 0), chunks[0]);
    }

    [Fact]
    public void RepeatedUpdatesNeverAskForTheSameChunkTwiceQuickly()
    {
        var manager = new ChunkManager(null!);
        var asked = new List<(int, int, int)>();
        var streamer = new ClientChunkStreamer(manager, (x, y, z) => asked.Add((x, y, z)), (_, _) => { });

        streamer.Update(Vector3.Zero, 0.016);
        streamer.Update(Vector3.Zero, 0.016);

        Assert.Equal(asked.Count, asked.Distinct().Count());
    }

    [Fact]
    public void UpdateAsksForTheColumnsAroundEveryRequestedChunk()
    {
        // A build needs the 3x3 columns around its chunk, and a chunk at the load sphere's rim has
        // diagonal columns no chunk inside the sphere asks for on its own.
        var manager = new ChunkManager(null!);
        var chunks = new List<(int X, int Y, int Z)>();
        var columns = new List<(int X, int Z)>();
        var streamer = new ClientChunkStreamer(manager, (x, y, z) => chunks.Add((x, y, z)), (x, z) => columns.Add((x, z)));

        streamer.Update(Vector3.Zero, 0.016);

        Assert.NotEmpty(chunks);
        foreach (var (cx, _, cz) in chunks)
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
            Assert.Contains((cx + dx, cz + dz), columns);
        // A column shared by several requested chunks is asked for once
        Assert.Equal(columns.Count, columns.Distinct().Count());
    }

    [Fact]
    public void AnUnansweredColumnIsAskedForAgainAfterTheResendDelay()
    {
        var manager = new ChunkManager(null!);
        var columns = new List<(int X, int Z)>();
        var streamer = new ClientChunkStreamer(manager, (_, _, _) => { }, (x, z) => columns.Add((x, z)));

        streamer.Update(Vector3.Zero, 0.016);
        streamer.Update(Vector3.Zero, ClientChunkStreamer.ResendSeconds * 0.5);
        Assert.Equal(1, columns.Count(c => c == (0, 0)));

        streamer.Update(Vector3.Zero, ClientChunkStreamer.ResendSeconds);
        Assert.Equal(2, columns.Count(c => c == (0, 0)));

        // Once it is answered it is never asked again, however long its chunks stay missing
        streamer.ApplyColumn(0, 0, ChunkNetCodec.EncodeColumn(Terrain(0)));
        columns.Clear();
        for (var i = 0; i < 5; i++)
            streamer.Update(Vector3.Zero, ClientChunkStreamer.ResendSeconds * 2);
        Assert.DoesNotContain((0, 0), columns);
    }

    [Fact]
    public void AColumnPayloadIsServedThroughTheManagersColumnSource()
    {
        var manager = new ChunkManager(null!);
        var streamer = new ClientChunkStreamer(manager, (_, _, _) => { }, (_, _) => { });
        var terrain = Terrain(42, water: 50, warmth: 0.25f, wetness: 0.75f);

        // Before anything arrived, the manager's source (which every mesh build, the fluid-surface
        // query and the mining pop read) knows no column: nothing falls back to worldgen
        var source = manager.ColumnSource;
        Assert.False(source.IsReady(2, -3));
        Assert.False(source.TryGet(2, -3, out _));

        streamer.ApplyColumn(2, -3, ChunkNetCodec.EncodeColumn(terrain));

        // The whole column - heights, water levels and climate - is served as the server sent it
        Assert.True(source.IsReady(2, -3));
        Assert.True(source.TryGet(2, -3, out var served));
        Assert.Equal(terrain.Heights, served.Heights);
        Assert.Equal(terrain.WaterLevel, served.WaterLevel);
        Assert.True(served.ContentEquals(terrain));
        // A column that never arrived is still unknown
        Assert.False(source.IsReady(0, 0));
        Assert.False(source.TryGet(0, 0, out _));
    }

    [Fact]
    public void AReceivedColumnIsNeverAskedForAgain()
    {
        var manager = new ChunkManager(null!);
        var columns = new List<(int X, int Z)>();
        var streamer = new ClientChunkStreamer(manager, (_, _, _) => { }, (x, z) => columns.Add((x, z)));
        streamer.Update(Vector3.Zero, 0.016);
        Assert.Contains((0, 0), columns);

        streamer.ApplyColumn(0, 0, ChunkNetCodec.EncodeColumn(Terrain(0)));
        columns.Clear();
        // Long past every resend interval, with the column's chunks still missing
        for (var i = 0; i < 20; i++)
            streamer.Update(Vector3.Zero, 1.0);

        Assert.DoesNotContain((0, 0), columns);
    }

    [Fact]
    public void OnlyAChangedColumnCountsAsNew()
    {
        var stored = Terrain(12, water: 20, warmth: 0.3f, wetness: 0.6f);

        var raised = Terrain(12, water: 20, warmth: 0.3f, wetness: 0.6f);
        raised.Heights[5] += 3;
        var flooded = Terrain(12, water: 20, warmth: 0.3f, wetness: 0.6f);
        flooded.WaterLevel[7] = 21;
        var drained = Terrain(12, water: 20, warmth: 0.3f, wetness: 0.6f);
        drained.WaterLevel[7] = TerrainGenerator.NoWater;

        var answers = new (string Name, ColumnTerrain Answer, bool Changed)[]
        {
            // A duplicate answer changes nothing and re-dirties nothing
            ("duplicate", Terrain(12, water: 20, warmth: 0.3f, wetness: 0.6f), false),
            ("heights", raised, true),
            ("water level", flooded, true),
            ("water drained", drained, true),
            ("warmth", Terrain(12, water: 20, warmth: 0.31f, wetness: 0.6f), true),
            ("wetness", Terrain(12, water: 20, warmth: 0.3f, wetness: 0.59f), true),
        };

        foreach (var (name, answer, changed) in answers)
        {
            var manager = new ChunkManager(null!);
            var streamer = new ClientChunkStreamer(manager, (_, _, _) => { }, (_, _) => { });
            Assert.True(streamer.ApplyColumn(1, 1, ChunkNetCodec.EncodeColumn(stored)));
            // A loaded chunk over the column, not dirty yet
            var pos = new Vector3(1, 0, 1);
            manager.Chunks.SetPacked(ChunkKey.Pack(pos), new Chunk(null!, pos, generateBlockData: false));
            Assert.Equal(0, manager.DirtyCount);

            Assert.True(changed == streamer.ApplyColumn(1, 1, ChunkNetCodec.EncodeColumn(answer)), name);
            Assert.True(changed == (manager.DirtyCount > 0), $"{name}: {manager.DirtyCount} chunks re-dirtied");
            Assert.True(streamer.TryGet(1, 1, out var now));
            Assert.True(now.ContentEquals(answer), name);
        }
    }

    // A column of one height and one water level everywhere, with the given corner climate
    private static ColumnTerrain Terrain(int height, int water = TerrainGenerator.NoWater, float warmth = 0.5f, float wetness = 0.5f)
    {
        var heights = new int[Chunk.Size * Chunk.Size];
        Array.Fill(heights, height);
        var levels = new int[Chunk.Size * Chunk.Size];
        Array.Fill(levels, water);
        return new ColumnTerrain(heights, levels, warmth, wetness);
    }

    [Fact]
    public void AStreamedChunkIsNotQueuedForTheWorldgenWaterWake()
    {
        // The wake queue is only drained by FluidManager.WakeLoadedChunks, which a client never
        // runs; a streamed chunk left in it would stay referenced for the whole session.
        var manager = new ChunkManager(null!);
        manager.ApplyStreamedChunk(Vector3.Zero, new ChunkSnapshot(new byte[Chunk.BlockCount], null, null));

        Assert.False(manager.TryDequeueLoaded(out _));
    }
}
