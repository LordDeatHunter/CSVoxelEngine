using System.Collections.Concurrent;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

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
    public void RawWritesFireTheJournalInWorldCoordinates()
    {
        var seen = new ConcurrentQueue<(int X, int Y, int Z, byte Id, byte State)>();
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
        Assert.Contains((33, 50, 67, (byte)0, (byte)0), seen);
    }
}
