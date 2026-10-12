using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// A multiplayer client never meshes a chunk before the server's terrain for the nine chunk
/// columns around it has arrived (<see cref="ChunkManager.ColumnSource"/>): a build would light
/// it dark and lose its water surfaces and tint. The waiting chunk is dropped from the dirty set
/// rather than retried every frame, and the column's arrival is what wakes it. An all-air chunk
/// reads no column and meshes at once.
/// </summary>
public sealed class ClientMeshGateTests
{
    private static readonly Vector3 Centre = Vector3.Zero;

    // The foci array ChunkManager.Update streams around (chunk positions); one reference for the
    // whole test, as a real caller passes while the focus is unchanged.
    private readonly Vector3[] _foci = [Centre];

    private readonly byte _stone;

    public ClientMeshGateTests()
    {
        TestContent.EnsureLoaded();
        _stone = ContentRegistry.RequireBlock("stone").Id;
    }

    /// <summary>
    /// A headless client manager with its streamer, the centre chunk (one stone block, or all air)
    /// and its six empty face neighbors streamed in, and no column yet.
    /// </summary>
    private (ChunkManager Manager, ClientChunkStreamer Streamer, Chunk Centre) StreamedNeighbourhood(bool centreHasStone)
    {
        var manager = new ChunkManager(null!) { Headless = true };
        var streamer = new ClientChunkStreamer(manager, (_, _, _) => { }, (_, _) => { });

        var blocks = new byte[Chunk.BlockCount];
        if (centreHasStone) blocks[Chunk.Idx(8, 8, 8)] = _stone;
        manager.ApplyStreamedChunk(Centre, new ChunkSnapshot(blocks, null, null));
        foreach (var offset in Vector3Directions.CardinalDirections)
            manager.ApplyStreamedChunk(Centre + offset, new ChunkSnapshot(new byte[Chunk.BlockCount], null, null));

        return (manager, streamer, manager.Chunks[Centre]);
    }

    // Far below the chunks, so the whole light volume is open sky; no water.
    private static byte[] ColumnPayload()
    {
        var heights = new int[Chunk.Size * Chunk.Size];
        Array.Fill(heights, -100);
        var water = new int[Chunk.Size * Chunk.Size];
        Array.Fill(water, TerrainGenerator.NoWater);
        return ChunkNetCodec.EncodeColumn(new ColumnTerrain(heights, water, 0.5f, 0.5f));
    }

    private static void ApplyColumns(ClientChunkStreamer streamer, Func<int, int, bool> which)
    {
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
            if (which(dx, dz))
                streamer.ApplyColumn((int)Centre.X + dx, (int)Centre.Z + dz, ColumnPayload());
    }

    /// <summary>One frame that schedules, then the drain, then the frame that applies the result.</summary>
    private void Frames(ChunkManager manager)
    {
        manager.Update(_foci);
        Assert.True(WaitUntil(() => Quiescent(manager)), "mesh dispatcher did not drain");
        manager.Update(_foci);
    }

    private static bool WaitUntil(Func<bool> done, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (done()) return true;
            Thread.Sleep(1);
        }
        return done();
    }

    private static bool Quiescent(ChunkManager m) =>
        m.GeneratingMeshCount == 0 && m.PendingMeshWorkCount == 0 && m.ActiveMeshRunnerCount == 0;

    [Fact]
    public void AChunkIsNotMeshedUntilItsNineColumnsArrived()
    {
        var (manager, streamer, centre) = StreamedNeighbourhood(centreHasStone: true);

        Frames(manager);
        Assert.False(centre.IsMeshed, "meshed before any column arrived");
        // Dropped from the dirty set, not retried every frame
        Assert.Equal(0, manager.DirtyCount);

        ApplyColumns(streamer, (_, _) => true);
        Frames(manager);
        Assert.True(centre.IsMeshed, "the column arrivals never woke the chunk");
    }

    [Fact]
    public void ADiagonalColumnIsRequiredToo()
    {
        var (manager, streamer, centre) = StreamedNeighbourhood(centreHasStone: true);

        // Every column but one corner of the 3x3
        ApplyColumns(streamer, (dx, dz) => !(dx == 1 && dz == 1));
        Frames(manager);
        Assert.False(centre.IsMeshed, "meshed without the diagonal column");
        Assert.Equal(0, manager.DirtyCount);

        ApplyColumns(streamer, (dx, dz) => dx == 1 && dz == 1);
        Frames(manager);
        Assert.True(centre.IsMeshed, "the last column's arrival never woke the chunk");
    }

    [Fact]
    public void AnAllAirChunkMeshesBeforeItsColumnsArrive()
    {
        var (manager, _, centre) = StreamedNeighbourhood(centreHasStone: false);

        Frames(manager);
        Assert.True(centre.IsMeshed, "an all-air chunk waited for columns its build never reads");
    }

    [Fact]
    public void AnEditBeforeTheColumnsArriveWaitsForThemInsteadOfMeshing()
    {
        var (manager, streamer, centre) = StreamedNeighbourhood(centreHasStone: true);
        Frames(manager);

        // The synchronous edit remesh does not bake the chunk without its columns; it leaves the
        // chunk dirty once, and the next frame drops it rather than spinning on it.
        manager.ImmediateRemesh(Centre);
        Assert.False(centre.IsMeshed, "the edit remesh built without the columns");
        Assert.Equal(1, manager.DirtyCount);
        Frames(manager);
        Assert.False(centre.IsMeshed);
        Assert.Equal(0, manager.DirtyCount);

        ApplyColumns(streamer, (_, _) => true);
        Frames(manager);
        Assert.True(centre.IsMeshed, "the column arrivals never woke the edited chunk");
    }
}
