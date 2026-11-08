using System;
using System.Numerics;
using VoxelEngine.Config;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// The multiplayer server's drop pickup (<see cref="DropManager.Update(float, System.Collections.Generic.IReadOnlyList{DropCollector}, ChunkManager)"/>):
/// a drop goes to the nearest player whose inventory has room for it, and only out of a loaded chunk.
/// </summary>
public sealed class DropPickupTests
{
    public DropPickupTests() => TestContent.EnsureLoaded();

    private static byte Stone => ContentRegistry.RequireBlock("stone").Id;

    // One loaded, empty chunk at the origin (cells 0..15 on every axis)
    private static ChunkManager OriginChunk()
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        return manager;
    }

    // An eye whose collect target (eye minus the collect height offset) sits `offset` from a point
    private static Vector3 EyeNear(Vector3 point, Vector3 offset) =>
        point + offset + Vector3.UnitY * DropManager.CollectHeightOffset;

    /// <summary>Runs the pickup until the drop is collected (or a few seconds pass) and returns
    /// the collector index it went to, or -1.</summary>
    private static int CollectorOf(DropManager drops, DropCollector[] collectors, ChunkManager chunks)
    {
        for (var frame = 0; frame < 300; frame++)
        {
            drops.Update(1f / 60, collectors, chunks);
            if (drops.Collected.Length > 0)
                return drops.CollectedBy[0];
        }
        return -1;
    }

    [Fact]
    public void ADropGoesToTheNearestPlayer()
    {
        var chunks = OriginChunk();
        var spot = new Vector3(8f, 8f, 8f);

        // Both are within pickup range; the order in the list must not matter
        foreach (var nearFirst in new[] { true, false })
        {
            var drops = new DropManager();
            drops.SpawnRestored(Stone, spot, Vector3.Zero, age: 5f);
            var near = new DropCollector(EyeNear(spot, new Vector3(0.8f, 0f, 0f)), null);
            var far = new DropCollector(EyeNear(spot, new Vector3(-2.2f, 0f, 0f)), null);
            var collectors = nearFirst ? new[] { near, far } : new[] { far, near };

            var winner = CollectorOf(drops, collectors, chunks);
            Assert.Equal(nearFirst ? 0 : 1, winner);
            Assert.Equal(Stone, drops.Collected[0]);
            Assert.Empty(drops.Drops);
        }
    }

    [Fact]
    public void APlayerWithNoRoomIsPassedOver()
    {
        var chunks = OriginChunk();
        var spot = new Vector3(8f, 8f, 8f);
        var drops = new DropManager();
        drops.SpawnRestored(Stone, spot, Vector3.Zero, age: 5f);

        var full = new DropCollector(EyeNear(spot, new Vector3(0.5f, 0f, 0f)), static _ => false);
        var roomy = new DropCollector(EyeNear(spot, new Vector3(-2.2f, 0f, 0f)), static _ => true);

        Assert.Equal(1, CollectorOf(drops, [full, roomy], chunks));
    }

    [Fact]
    public void NobodyCollectsADropOutsideTheLoadedChunks()
    {
        var chunks = OriginChunk();
        var drops = new DropManager();
        // A whole chunk away from the only loaded one
        var spot = new Vector3(40f, 8f, 8f);
        drops.SpawnRestored(Stone, spot, Vector3.Zero, age: 5f);
        var beside = new DropCollector(EyeNear(spot, new Vector3(0.5f, 0f, 0f)), null);

        Assert.Equal(-1, CollectorOf(drops, [beside], chunks));
        Assert.Single(drops.Drops);
        // Not simulated either: it does not fall through the unloaded air
        Assert.Equal(spot, drops.Drops[0].Position);
    }

    [Fact]
    public void ADropNobodyCanTakeLetsGoAndFalls()
    {
        var chunks = OriginChunk();
        var spot = new Vector3(8f, 12f, 8f);
        var drops = new DropManager();
        drops.SpawnRestored(Stone, spot, Vector3.Zero, age: 5f);
        var drop = drops.Drops[0];

        // Latched onto a player some way off...
        var player = new DropCollector(EyeNear(spot, new Vector3(2.5f, 0f, 0f)), null);
        drops.Update(1f / 60, [player], chunks);
        Assert.True(drop.IsAttracted);

        // ...who then leaves: the drop is released and gravity takes it again
        drops.Update(1f / 60, Array.Empty<DropCollector>(), chunks);
        Assert.False(drop.IsAttracted);
        var before = drop.Position.Y;
        for (var frame = 0; frame < 10; frame++)
            drops.Update(1f / 60, Array.Empty<DropCollector>(), chunks);
        Assert.True(drop.Position.Y < before);
        Assert.Single(drops.Drops);
    }

    // The origin chunk with a stone floor under y = 8, so a drop rests at the spot instead of falling away
    private static ChunkManager FlooredOriginChunk()
    {
        var chunks = OriginChunk();
        var chunk = chunks.Chunks[Vector3.Zero];
        for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
                chunk.SetBlockRaw(x, 7, z, Stone, 0);
        return chunks;
    }

    private const float MagnetRange = DropManager.PickupRange * GearConfig.MagnetRangeFactor;

    [Fact]
    public void AMagnetCollectorAttractsADropBeyondThePlainPickupRange()
    {
        var chunks = FlooredOriginChunk();
        var spot = new Vector3(8f, 8f, 8f);
        var offset = new Vector3(DropManager.PickupRange * 1.5f, 0f, 0f);
        Assert.True(offset.X < MagnetRange);

        var plainDrops = new DropManager();
        plainDrops.SpawnRestored(Stone, spot, Vector3.Zero, age: 5f);
        Assert.Equal(-1, CollectorOf(plainDrops, [new DropCollector(EyeNear(spot, offset), null)], chunks));

        var magnetDrops = new DropManager();
        magnetDrops.SpawnRestored(Stone, spot, Vector3.Zero, age: 5f);
        Assert.Equal(0, CollectorOf(magnetDrops, [new DropCollector(EyeNear(spot, offset), null, MagnetRange)], chunks));
    }

    [Fact]
    public void ANearerCollectorOutOfItsOwnRangeNeverBlocksAMagnetCollector()
    {
        var chunks = FlooredOriginChunk();
        var spot = new Vector3(8f, 8f, 8f);
        var drops = new DropManager();
        drops.SpawnRestored(Stone, spot, Vector3.Zero, age: 5f);

        var plain = new DropCollector(EyeNear(spot, new Vector3(DropManager.PickupRange + 0.3f, 0f, 0f)), null);
        var magnet = new DropCollector(EyeNear(spot, new Vector3(-(DropManager.PickupRange + 1.5f), 0f, 0f)), null,
            MagnetRange);

        Assert.Equal(1, CollectorOf(drops, [plain, magnet], chunks));
    }
}
