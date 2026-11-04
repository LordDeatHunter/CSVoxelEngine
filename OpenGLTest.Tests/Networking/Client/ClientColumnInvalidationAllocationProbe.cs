using System;
using System.Diagnostics;
using System.Numerics;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Saves;
using Xunit;

namespace VoxelEngine.Tests.Networking.Client;

/// <summary>Opt-in allocation diagnostic; run with VOXEL_COLUMN_PROBE=1 in Release.</summary>
public sealed class ClientColumnInvalidationAllocationProbe
{
    [Fact]
    public void ReportsApplyColumnAllocationsAtSeveralLoadedTableSizes()
    {
        if (Environment.GetEnvironmentVariable("VOXEL_COLUMN_PROBE") != "1") return;

        TestContent.EnsureLoaded();
        foreach (var size in new[] { 1_000, 8_000, 17_077 })
        {
            var manager = new ChunkManager(null!);
            var streamer = new ClientChunkStreamer(manager, (_, _, _) => { }, (_, _) => { });
            for (var i = 0; i < size; i++)
            {
                // Spread chunks over chunk-space while keeping only a small portion near (0,0).
                var x = i % 128 - 64;
                var z = i / 128 - 64;
                var y = i / (128 * 128) - 1;
                var position = new Vector3(x, y, z);
                manager.Chunks.SetPacked(ChunkKey.Pack(position), new Chunk(null!, position, generateBlockData: false));
            }

            var source = new ColumnTerrain(new int[Chunk.Size * Chunk.Size], new int[Chunk.Size * Chunk.Size], 0.5f, 0.5f);
            var payload = ChunkNetCodec.EncodeColumn(source);
            var encodedBytes = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < 200; i++) _ = ChunkNetCodec.EncodeColumn(source);
            stopwatch.Stop();
            var encodeBytes = (GC.GetAllocatedBytesForCurrentThread() - encodedBytes) / 200;

            // Warm the immutable-column decode and changed-column paths. Alternate two payloads
            // so every measured call changes the stored contents and executes the full walk.
            streamer.ApplyColumn(0, 0, payload);
            var changed = new ColumnTerrain((int[])source.Heights.Clone(), (int[])source.WaterLevel.Clone(), 0.6f, 0.5f);
            var changedPayload = ChunkNetCodec.EncodeColumn(changed);
            for (var i = 0; i < 16; i++) streamer.ApplyColumn(0, 0, i % 2 == 0 ? changedPayload : payload);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            stopwatch.Restart();
            const int samples = 200;
            for (var i = 0; i < samples; i++) streamer.ApplyColumn(0, 0, i % 2 == 0 ? payload : changedPayload);
            stopwatch.Stop();
            var totalBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            var applyMs = stopwatch.Elapsed.TotalMilliseconds / samples;
            var decodeBefore = GC.GetAllocatedBytesForCurrentThread();
            stopwatch.Restart();
            for (var i = 0; i < samples; i++) _ = ChunkNetCodec.DecodeColumn(payload);
            stopwatch.Stop();
            var decodeBytes = (GC.GetAllocatedBytesForCurrentThread() - decodeBefore) / samples;
            var decodeMs = stopwatch.Elapsed.TotalMilliseconds / samples;
            Console.WriteLine($"COLUMN_PROBE chunks={size} apply_alloc_bytes={totalBytes / samples} apply_ms_per_call={applyMs:F4} decode_alloc_bytes={decodeBytes} decode_ms_per_call={decodeMs:F4} encode_alloc_bytes={encodeBytes}");
        }
    }
}
