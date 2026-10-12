using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The server's network capture must not clear a chunk's save-dirty flag (unlike the save
/// snapshot) and must carry the current blocks, which is what lets a server stream chunks to
/// clients without pretending it saved them.
/// </summary>
public sealed class ChunkNetworkCaptureTests
{
    public ChunkNetworkCaptureTests() => TestContent.EnsureLoaded();

    [Fact]
    public void CaptureForNetworkKeepsTheSaveDirtyFlagAndCarriesTheBlocks()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.SetBlockRaw(1, 1, 1, stone, 0);
        Assert.True(chunk.ModifiedSinceSave);

        var snapshot = chunk.CaptureForNetwork();

        Assert.Equal(stone, snapshot.Blocks[Chunk.Idx(1, 1, 1)]);
        // A network read must not make the server think the chunk was saved.
        Assert.True(chunk.ModifiedSinceSave);
    }

    [Fact]
    public void AnAbsentChunkIsNotCaptured()
    {
        var manager = new ChunkManager(null!);
        Assert.False(manager.TryCaptureChunk(0, 0, 0, out _));
    }
}
