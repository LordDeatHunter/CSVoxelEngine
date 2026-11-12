using System.Collections.Concurrent;
using System.Numerics;
using System.Collections.Generic;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Networking;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

/// <summary>
/// The raw-write journal a server hooks to broadcast every cell change, including simulation steps
/// (fluid, decay) that no explicit edit path reports: each write fires with world coordinates and
/// the new id and state.
/// </summary>
[Collection("Worldgen seed")]
public sealed class ChunkCellJournalTests
{
    public ChunkCellJournalTests() => TestContent.EnsureLoaded();

    [Fact]
    public void SlowLavaAndWaterReplicateTheirActualAuthoritativeWrites()
    {
        var manager = new ChunkManager(null!);
        var source = new Chunk(null!, Vector3.Zero, false) { IsBlockDataReady = true };
        manager.Chunks[Vector3.Zero] = source;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var lava = ContentRegistry.RequireBlock("lava").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++) source.SetBlockRaw(x, 0, z, stone, 0);
        source.SetBlockRaw(4, 1, 8, lava, FluidState.Source);
        source.SetBlockRaw(12, 1, 8, water, FluidState.Source);
        var mirror = new Chunk(null!, Vector3.Zero, false);
        mirror.RestoreFrom(source.CaptureForNetwork());
        var changes = new List<NetCellChange>();
        var fluids = new FluidManager();
        fluids.ScheduleAround(4, 1, 8);
        fluids.ScheduleAround(12, 1, 8);
        Chunk.CellWritten = (x, y, z, id, state) => changes.Add(new NetCellChange(x, y, z, id, state));
        try
        {
            for (var tick = 1; tick <= 4; tick++)
            {
                fluids.Update(SimulationConfig.FluidTickSeconds, manager, new DropManager());
                if (tick < 4) Assert.DoesNotContain(changes, c => c.Id == lava);
            }
        }
        finally { Chunk.CellWritten = null; }
        Assert.Contains(changes, c => c.Id == water);
        Assert.Contains(changes, c => c.Id == lava && !FluidState.IsSource(c.State));
        foreach (var change in changes)
        {
            using var writer = new PacketWriter(NetMessage.BlockChanges);
            NetCellChangeCodec.Write(writer, change);
            using var reader = new PacketReader(writer.ToArray());
            Assert.Equal(NetMessage.BlockChanges, reader.Message);
            var decoded = NetCellChangeCodec.Read(reader);
            Assert.Equal(change, decoded);
            mirror.SetBlockRaw(decoded.X, decoded.Y, decoded.Z, decoded.Id, decoded.State);
        }
        Assert.Equal(source.Storage.ToDense(), mirror.Storage.ToDense());
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            Assert.Equal(source.GetLocalState(x, y, z), mirror.GetLocalState(x, y, z));
    }

    [Fact]
    public void RawWritesFireTheJournalInWorldCoordinates()
    {
        var seen = new ConcurrentQueue<(int X, int Y, int Z, ushort Id, byte State)>();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var chunk = new Chunk(null!, new Vector3(2f, 3f, 4f), generateBlockData: false);

        Chunk.CellWritten = (x, y, z, id, state) => seen.Enqueue((x, y, z, id, state));
        try
        {
            chunk.SetBlockRaw(1, 2, 3, stone, 4);
            chunk.ClearBlockAt(new Vector3(33, 50, 67));
        }
        finally
        {
            Chunk.CellWritten = null;
        }

        // Chunk (2,3,4) starts at world (32,48,64).
        Assert.Contains((33, 50, 67, stone, (byte)4), seen);
        Assert.Contains((33, 50, 67, BlockType.Air, (byte)0), seen);
    }
}
