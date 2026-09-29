using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Pins <see cref="WorldLoadGate"/>: a loading world starts playing only once every chunk of the
/// ready sphere is generated and meshed (or the timeout passes), and the progress it reports to the
/// loading screen only ever moves forward.
/// </summary>
public sealed class WorldLoadGateTests
{
    public WorldLoadGateTests() => TestContent.EnsureLoaded();

    // Camera in the middle of chunk (0, 8, 0).
    private static readonly Vector3 CameraPosition = new(8f, 8 * Chunk.Size + 8f, 8f);

    private static readonly Vector3 CameraChunk = new(0f, 8f, 0f);

    /// <summary>The seven chunk positions of the radius-1 sphere: the centre and its six faces.</summary>
    private static List<Vector3> RadiusOneSphere() =>
    [
        CameraChunk,
        CameraChunk + Vector3.UnitX, CameraChunk - Vector3.UnitX,
        CameraChunk + Vector3.UnitY, CameraChunk - Vector3.UnitY,
        CameraChunk + Vector3.UnitZ, CameraChunk - Vector3.UnitZ,
    ];

    private static Chunk Insert(ChunkManager manager, Vector3 pos)
    {
        var chunk = new Chunk(null!, pos, false) { IsBlockDataReady = true };
        manager.Chunks[pos] = chunk;
        return chunk;
    }

    private static void Mesh(Chunk chunk) =>
        chunk.UpdateMesh(new ChunkMeshData([], [], [], [], false), null, null);

    [Fact]
    public void NotReadyUntilEveryChunkInTheSphereIsMeshed()
    {
        var manager = new ChunkManager(null!);
        var gate = new WorldLoadGate(1, 1e9);
        var sphere = RadiusOneSphere();

        Assert.False(gate.Update(manager, CameraPosition, 0.016));
        Assert.Equal(0f, gate.Progress);

        // The first three generated and meshed.
        var chunks = new List<Chunk>();
        for (var i = 0; i < 3; i++)
        {
            var chunk = Insert(manager, sphere[i]);
            Mesh(chunk);
            chunks.Add(chunk);
        }

        Assert.False(gate.Update(manager, CameraPosition, 0.016));
        var p1 = gate.Progress;
        Assert.InRange(p1, float.Epsilon, 1f - float.Epsilon);

        // The rest generated but not meshed yet: block data alone is not ready.
        for (var i = 3; i < sphere.Count; i++)
            chunks.Add(Insert(manager, sphere[i]));

        Assert.False(gate.Update(manager, CameraPosition, 0.016));
        Assert.False(gate.IsComplete);
        var p2 = gate.Progress;
        Assert.True(p2 >= p1);
        Assert.True(p2 < 1f);

        foreach (var chunk in chunks)
            Mesh(chunk);

        Assert.True(gate.Update(manager, CameraPosition, 0.016));
        Assert.True(gate.IsComplete);
        Assert.False(gate.TimedOut);
        Assert.Equal(1f, gate.Progress);
    }

    [Fact]
    public void ProgressNeverDecreases()
    {
        var manager = new ChunkManager(null!);
        var gate = new WorldLoadGate(1, 1e9);
        var sphere = RadiusOneSphere();

        for (var i = 0; i < 4; i++)
            Mesh(Insert(manager, sphere[i]));

        Assert.False(gate.Update(manager, CameraPosition, 0.016));
        var before = gate.Progress;
        Assert.True(before > 0f);

        // A meshed chunk leaves the sphere (unloaded): fewer are ready now, but the bar holds.
        Assert.True(manager.Chunks.TryRemove(sphere[0], out _));

        Assert.False(gate.Update(manager, CameraPosition, 0.016));
        Assert.True(gate.Progress >= before);
    }

    [Fact]
    public void TimeoutCompletesAnUnfinishedLoad()
    {
        var manager = new ChunkManager(null!);
        var gate = new WorldLoadGate(2, 1.0);

        Assert.False(gate.Update(manager, CameraPosition, 0.4));
        Assert.False(gate.TimedOut);

        // A negative delta never winds the clock back.
        var elapsed = gate.Elapsed;
        Assert.False(gate.Update(manager, CameraPosition, -5.0));
        Assert.True(gate.Elapsed >= elapsed);

        Assert.False(gate.Update(manager, CameraPosition, 0.4));
        Assert.False(gate.TimedOut);

        Assert.True(gate.Update(manager, CameraPosition, 0.4));
        Assert.True(gate.TimedOut);
        Assert.True(gate.IsComplete);
        Assert.Equal(1f, gate.Progress);
    }
}
