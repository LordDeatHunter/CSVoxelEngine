using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.Chunks.Streaming;
using VoxelEngine.Data;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.Interaction;

/// <summary>An S4a-only real server/player fixture whose constructor race can fill only flat chunks.</summary>
internal sealed class GroundCoverPlayerWorld : IDisposable
{
    private const int PlateauTop = 71;
    private readonly ITestOutputHelper _output;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly CountingSource _source;
    private readonly ThrowingSource _fallback = new();
    private readonly HashSet<(int X, int Y, int Z)> _requested = [];
    private int _queueCalls;

    public GroundCoverPlayerWorld(ITestOutputHelper output)
    {
        _output = output;
        Stone = ContentRegistry.RequireBlock("stone").Id;
        _source = new CountingSource(new FlatWorldSource(int.MinValue, int.MaxValue,
            int.MinValue, int.MaxValue, PlateauTop, Stone, Biome.Forest,
            warmth: 0.5f, wetness: 0.5f, fallback: _fallback));
        var eye = new Vector3(8.5f, PlateauTop + 1 + Camera.EyeHeight, 8.5f);
        World? world = null;
        try
        {
            world = new World(null!, null!, null!, null!, null!, null!, null, null,
                serverMode: true, spawn: eye, worldSource: _source);
            // The constructor started workers with the camera focus. No test calls World.Update,
            // so an empty focus leaves only the finite, counted constructor race to finish.
            world.Focus.SetEyes(ReadOnlySpan<Vector3>.Empty);
            World = world;
            Manager = world.GetChunkManager();
            Player = new ServerPlayer(1, "Builder", eye);
            world.ServerPlayers = [Player];
        }
        catch (Exception setupFailure)
        {
            // The World constructor owns its own failure. Once it returned, this fixture owns
            // worker shutdown, even if later player or focus setup throws.
            if (world != null)
            {
                try
                {
                    if (!world.SaveAndDispose())
                        throw new InvalidOperationException("S4a fixture world did not shut down after setup failure");
                }
                catch (Exception teardownFailure)
                {
                    throw new AggregateException(setupFailure, teardownFailure);
                }
            }
            throw;
        }
    }

    public World World { get; }
    public ChunkManager Manager { get; }
    public ServerPlayer Player { get; }
    public int Top => PlateauTop;
    public ushort Stone { get; }

    /// <summary>Synchronously requests the edited chunk and its six face neighbors.</summary>
    public void Load(int x, int y, int z)
    {
        var center = VoxelCoords.ChunkPosOf(x, y, z);
        Queue(center);
        foreach (var offset in Vector3Directions.CardinalDirections)
            Queue(center + offset);
        Assert.Equal(7, _requested.Count);

        // QueueChunk is synchronous except when a constructor worker already owns the key.
        var wait = Stopwatch.StartNew();
        while (!AllLoaded() && wait.Elapsed < TimeSpan.FromSeconds(20))
            Thread.Sleep(1);
        Assert.True(AllLoaded(), $"seven flat chunks around ({x}, {y}, {z}) did not load");

        bool AllLoaded()
        {
            foreach (var (cx, cy, cz) in _requested)
                if (Manager.ChunkAt(cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size) == null)
                    return false;
            return true;
        }
    }

    private void Queue(Vector3 pos)
    {
        _queueCalls++;
        _requested.Add(((int)pos.X, (int)pos.Y, (int)pos.Z));
        Manager.QueueChunk(pos);
    }

    /// <summary>Raw setup only; actions after setup run through the authoritative World.</summary>
    public void Set(int x, int y, int z, ushort id, byte state = 0)
    {
        var chunk = Manager.ChunkAt(x, y, z) ?? throw new InvalidOperationException($"({x}, {y}, {z}) is not loaded");
        var (lx, ly, lz) = VoxelCoords.LocalOf(x, y, z);
        chunk.SetBlockRaw(lx, ly, lz, id, state);
    }

    public ushort IdAt(int x, int y, int z) => Manager.GetBlockGlobal(x, y, z);

    /// <summary>Places with a genuine survival ServerPlayer inventory and context.</summary>
    public bool Place(ushort id, Vector3 hitCell, Vector3 normal)
    {
        Assert.True(Player.Inventory.Add(id));
        Assert.True(Player.Inventory.PickBlock(id));
        return World.TryPlaceSelectedBlockAt(Player.Context, hitCell, normal);
    }

    public bool Break(int x, int y, int z) =>
        World.BreakBlockFor(Player.Context, new Vector3(x, y, z));

    public void Dispose()
    {
        var quiet = World.SaveAndDispose();
        _elapsed.Stop();
        var filled = _source.FilledPositions;
        var requested = _requested.OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToArray();
        var outsideExplicit = filled.Except(_requested).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToArray();
        _output.WriteLine($"S4a flat fixture quiet={quiet} elapsedMs={_elapsed.Elapsed.TotalMilliseconds:F1} " +
            $"queueCalls={_queueCalls} requested={_requested.Count} fillCalls={_source.FillCount} distinctFills={filled.Count} " +
            $"getColumn={_source.GetColumnCount} isReady={_source.IsReadyCount} " +
            $"tryGet={_source.TryGetCount} supplies={_source.SuppliesCount} " +
            $"requestedSet=[{string.Join(",", requested)}] " +
            $"outsideExplicitSet=[{string.Join(",", outsideExplicit)}] fallback={_fallback.Calls} " +
            $"offsetBound={ChunkGenerationWorker.SortedOffsets.Length}");
        Assert.True(quiet, "server world teardown timed out; seed stays locked");
        Assert.Equal(0, _fallback.Calls);
        Assert.True(_source.FillCount <= ChunkGenerationWorker.SortedOffsets.Length + _requested.Count,
            "flat fixture exceeded the no-unload focus scan plus explicit requests");
        Assert.False(WorldSeed.WorldIsLive);
        WorldSeed.Apply(WorldSeed.Default);
    }

    private sealed class CountingSource(IWorldSource inner) : IWorldSource
    {
        // Counts only calls forwarded through this wrapper. FlatWorldSource.Fill reads its own
        // GetColumn internally, so these are not totals for all column reads inside a fill.
        private readonly ConcurrentDictionary<(int X, int Y, int Z), byte> _filled = new();
        private int _fillCount, _getColumnCount, _isReadyCount, _tryGetCount, _suppliesCount;

        public int FillCount => Volatile.Read(ref _fillCount);
        public int GetColumnCount => Volatile.Read(ref _getColumnCount);
        public int IsReadyCount => Volatile.Read(ref _isReadyCount);
        public int TryGetCount => Volatile.Read(ref _tryGetCount);
        public int SuppliesCount => Volatile.Read(ref _suppliesCount);
        public HashSet<(int X, int Y, int Z)> FilledPositions => _filled.Keys.ToHashSet();

        public bool Supplies(int chunkX, int chunkZ)
        {
            Interlocked.Increment(ref _suppliesCount);
            return inner.Supplies(chunkX, chunkZ);
        }

        public ColumnData GetColumn(int chunkX, int chunkZ)
        {
            Interlocked.Increment(ref _getColumnCount);
            return inner.GetColumn(chunkX, chunkZ);
        }

        public bool IsReady(int chunkX, int chunkZ)
        {
            Interlocked.Increment(ref _isReadyCount);
            return inner.IsReady(chunkX, chunkZ);
        }

        public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain)
        {
            Interlocked.Increment(ref _tryGetCount);
            return inner.TryGet(chunkX, chunkZ, out terrain);
        }

        public void Fill(Chunk chunk)
        {
            Interlocked.Increment(ref _fillCount);
            var pos = chunk.Position;
            _filled.TryAdd(((int)pos.X, (int)pos.Y, (int)pos.Z), 0);
            inner.Fill(chunk);
        }
    }

    private sealed class ThrowingSource : IWorldSource
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        private Exception Unexpected()
        {
            Interlocked.Increment(ref _calls);
            return new InvalidOperationException("S4a all-flat fixture reached its forbidden fallback");
        }

        public bool Supplies(int chunkX, int chunkZ) => throw Unexpected();
        public ColumnData GetColumn(int chunkX, int chunkZ) => throw Unexpected();
        public void Fill(Chunk chunk) => throw Unexpected();
        public bool IsReady(int chunkX, int chunkZ) => throw Unexpected();
        public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain) => throw Unexpected();
    }
}
