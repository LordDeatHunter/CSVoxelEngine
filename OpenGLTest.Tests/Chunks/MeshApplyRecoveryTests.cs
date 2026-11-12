using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks.Meshing;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

/// <summary>Completed results keep one owner across an interrupted main-thread drain.</summary>
public sealed class MeshApplyRecoveryTests
{
    public MeshApplyRecoveryTests() => TestContent.EnsureLoaded();

    private static (ChunkManager Manager, Chunk First, Chunk Tail) CreateWorld()
    {
        var (manager, first) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var tailPosition = first.Position + new Vector3(3, 0, 0);
        var tail = new Chunk(null!, tailPosition, generateBlockData: false) { IsBlockDataReady = true };
        foreach (var offset in new[] { Vector3.Zero, Vector3.UnitX, -Vector3.UnitX,
                     Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
            manager.Chunks[tailPosition + offset] = offset == Vector3.Zero ? tail :
                new Chunk(null!, tailPosition + offset, generateBlockData: false) { IsBlockDataReady = true };
        foreach (var chunk in new[] { first, tail })
        {
            chunk.SetBlockRaw(4, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);
            chunk.SetBlockRaw(10, 8, 8, ContentRegistry.RequireBlock("water").Id, 0);
        }
        manager.Headless = true;
        return (manager, first, tail);
    }

    private static ChunkMeshData Produce(ChunkManager manager, Chunk chunk)
    {
        ChunkMeshData result = null!;
        var scheduler = manager.MeshSchedulerForTests;
        scheduler.MeshGeneratedForTests = (_, data) => result = data;
        manager.MarkDirty(chunk.Position);
        scheduler.ProcessDirtyChunks([chunk.Position]);
        Assert.True(manager.DrainMeshWork(TimeSpan.FromSeconds(10)), "worker did not drain");
        scheduler.MeshGeneratedForTests = null;
        Assert.NotNull(result);
        Assert.NotEmpty(result.Vertices);
        Assert.NotEmpty(result.TranslucentVertices);
        return result;
    }

    // Renting all four buffers back proves both return and absence of duplicate pool entries.
    private static void AssertReturnedOnce(ChunkManager manager, params ChunkMeshData[] results)
    {
        Assert.Equal(results.Length, manager.MeshBuffers.PooledVertexLists);
        Assert.Equal(results.Length, manager.MeshBuffers.PooledTranslucentVertexLists);
        var vertices = results.Select(_ => manager.MeshBuffers.RentVertices()).ToArray();
        var indices = results.Select(_ => manager.MeshBuffers.RentIndices()).ToArray();
        var translucent = results.Select(_ => manager.MeshBuffers.RentTranslucentVertices()).ToArray();
        var translucentIndices = results.Select(_ => manager.MeshBuffers.RentTranslucentIndices()).ToArray();
        foreach (var result in results)
        {
            Assert.Single(vertices, list => ReferenceEquals(list, result.Vertices));
            Assert.Single(indices, list => ReferenceEquals(list, result.Indices));
            Assert.Single(translucent, list => ReferenceEquals(list, result.TranslucentVertices));
            Assert.Single(translucentIndices, list => ReferenceEquals(list, result.TranslucentIndices));
            Assert.Empty(result.Vertices);
            Assert.Empty(result.Indices);
            Assert.Empty(result.TranslucentVertices);
            Assert.Empty(result.TranslucentIndices);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FailedApplyReturnsCurrentAndPreservesUntouchedResults(int failAt)
    {
        var (manager, first, tail) = CreateWorld();
        var scheduler = manager.MeshSchedulerForTests;
        var firstData = Produce(manager, first);
        var oldTail = Produce(manager, tail);
        tail.SetBlockRaw(7, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);
        var newTail = Produce(manager, tail);
        var failure = new InvalidOperationException("apply regression sentinel");
        var calls = 0;
        scheduler.MeshBeforeApplyForTests = (_, _) =>
        {
            if (calls++ == failAt) throw failure;
        };

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            scheduler.ApplyCompletedMeshes([first.Position])));
        Assert.Equal(2 - failAt, scheduler.CompletedMeshCount);
        Assert.Equal(failAt + 1, manager.MeshBuffers.PooledVertexLists);
        Assert.Equal(failAt + 1, manager.MeshBuffers.PooledTranslucentVertexLists);
        var failedChunk = failAt == 0 ? first : tail;
        Assert.True(scheduler.IsDirty(ChunkKey.Pack(failedChunk.Position)));
        Assert.False(tail.IsMeshed);
        Assert.NotEmpty(newTail.Vertices);

        var applied = new List<ChunkMeshData>();
        scheduler.MeshBeforeApplyForTests = (_, data) => applied.Add(data);
        scheduler.MeshUploadBudget = 0;
        scheduler.ApplyCompletedMeshes([first.Position]);
        Assert.Empty(applied);
        Assert.Equal(2 - failAt, scheduler.CompletedMeshCount);
        scheduler.MeshUploadBudget = 1;
        while (scheduler.CompletedMeshCount > 0)
            scheduler.ApplyCompletedMeshes([first.Position]);
        Assert.Equal(failAt == 0 ? new[] { oldTail, newTail } : new[] { newTail }, applied);
        Assert.True(tail.IsMeshed);
        Assert.Equal(failAt == 1, scheduler.IsDirty(ChunkKey.Pack(tail.Position)));
        AssertReturnedOnce(manager, firstData, oldTail, newTail);

        // The failed current snapshot is terminal; the ordinary dirty path rebuilds it.
        scheduler.ProcessDirtyChunks([first.Position]);
        Assert.True(manager.DrainMeshWork(TimeSpan.FromSeconds(10)));
        scheduler.ApplyCompletedMeshes([first.Position]);
        Assert.True(failedChunk.IsMeshed);
        Assert.False(scheduler.IsDirty(ChunkKey.Pack(failedChunk.Position)));
        Assert.Equal(0, scheduler.CompletedMeshCount);
        Assert.Equal(0, scheduler.GeneratingMeshCount);
        Assert.Equal(0, scheduler.PendingMeshWorkCount);
        Assert.Equal(0, scheduler.ActiveMeshRunnerCount);
        manager.ReleaseAll();
    }

    [Theory]
    [InlineData("unload")]
    [InlineData("replace")]
    [InlineData("revision")]
    [InlineData("range")]
    [InlineData("release")]
    public void RecoveredTailStillHonorsLifecycleGates(string change)
    {
        var (manager, first, tail) = CreateWorld();
        var scheduler = manager.MeshSchedulerForTests;
        var firstData = Produce(manager, first);
        var tailData = Produce(manager, tail);
        scheduler.MeshBeforeApplyForTests = (_, _) => throw new InvalidOperationException("stop");
        Assert.Throws<InvalidOperationException>(() => scheduler.ApplyCompletedMeshes([first.Position]));
        Assert.Equal(1, scheduler.CompletedMeshCount);
        scheduler.MeshBeforeApplyForTests = (_, _) => Assert.Fail("obsolete tail was applied");
        switch (change)
        {
            case "unload": manager.Chunks.TryRemove(tail.Position, out _); break;
            case "replace": manager.Chunks[tail.Position] = new Chunk(null!, tail.Position, false); break;
            case "revision": tail.AdvanceMeshRevision(); break;
        }
        if (change == "release") manager.ReleaseAll();
        else scheduler.ApplyCompletedMeshes([change == "range" ? new Vector3(1000) : first.Position]);
        Assert.False(tail.IsMeshed);
        Assert.Equal(0, scheduler.CompletedMeshCount);
        AssertReturnedOnce(manager, firstData, tailData);
        manager.ReleaseAll();
    }

    [Fact]
    public void BookkeepingFailureAfterPoolReturnDoesNotReturnCurrentTwice()
    {
        var (manager, first, tail) = CreateWorld();
        var scheduler = manager.MeshSchedulerForTests;
        var firstData = Produce(manager, first);
        var tailData = Produce(manager, tail);
        // Force the real render-list bookkeeping to throw after ApplyMesh returns its buffers.
        // This deliberately inconsistent index avoids adding a production fault-injection hook.
        first.ChestRenderIndex = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.ApplyCompletedMeshes([first.Position]));
        first.ChestRenderIndex = -1;
        Assert.True(first.IsMeshed);
        Assert.True(scheduler.IsDirty(ChunkKey.Pack(first.Position)));
        Assert.Equal(1, scheduler.CompletedMeshCount);
        Assert.NotEmpty(tailData.Vertices);
        scheduler.ApplyCompletedMeshes([first.Position]);
        Assert.True(tail.IsMeshed);
        AssertReturnedOnce(manager, firstData, tailData);
        manager.ReleaseAll();
    }

    [Fact]
    public void RealUploadFailureReturnsCurrentAndLeavesTailForNextFrame()
    {
        var (manager, first, tail) = CreateWorld();
        var scheduler = manager.MeshSchedulerForTests;
        var firstData = Produce(manager, first);
        var tailData = Produce(manager, tail);
        manager.Headless = false; // Real UpdateMesh rejects a nonempty mesh without a geometry pool.
        var failure = Assert.Throws<InvalidOperationException>(() => scheduler.ApplyCompletedMeshes([first.Position]));
        Assert.Contains("geometry pool", failure.Message);
        Assert.Equal(1, scheduler.CompletedMeshCount);
        Assert.True(scheduler.IsDirty(ChunkKey.Pack(first.Position)));
        Assert.NotEmpty(tailData.Vertices);
        manager.Headless = true;
        scheduler.ApplyCompletedMeshes([first.Position]);
        Assert.True(tail.IsMeshed);
        AssertReturnedOnce(manager, firstData, tailData);
        manager.ReleaseAll();
    }
}
