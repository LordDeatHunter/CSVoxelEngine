using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

/// <summary>
/// The sticky per-chunk <see cref="Chunk.MayHaveSkyCover"/> flag the light flood's sky-cover rule is gated on: a raw
/// write of a covering block raises it and no edit lowers it, a restore derives it from the snapshot's blocks alone (so a
/// client's streamed copy agrees with the server), and worldgen raises it in a tower chunk but never in a plain one.
/// </summary>
[Collection("Worldgen seed")]
public sealed class ChunkSkyCoverTests
{
    private static readonly Vector3 Somewhere = new(3, 5, -2);

    public ChunkSkyCoverTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    private static byte Brick => ContentRegistry.RequireBlock("tower_bricks").Id;
    private static byte Stone => ContentRegistry.RequireBlock("stone").Id;

    [Fact]
    public void WritingACoveringBlockRaisesTheFlagAndItStaysRaised()
    {
        var chunk = new Chunk(null!, Somewhere, generateBlockData: false);
        Assert.False(chunk.MayHaveSkyCover);

        chunk.SetBlockRaw(4, 4, 4, Stone, 0);
        Assert.False(chunk.MayHaveSkyCover);

        chunk.SetBlockRaw(5, 6, 7, Brick, 0);
        Assert.True(chunk.MayHaveSkyCover);

        // Sticky: taking the brick away again keeps the chunk flagged
        chunk.SetBlockRaw(5, 6, 7, BlockType.Air, 0);
        Assert.True(chunk.MayHaveSkyCover);
    }

    [Fact]
    public void ARestoredChunkReportsCoverFromItsBlocksAlone()
    {
        var withBrick = new Chunk(null!, Somewhere, generateBlockData: false);
        withBrick.SetBlockRaw(1, 2, 3, Stone, 0);
        withBrick.SetBlockRaw(9, 9, 9, Brick, 0);
        var plain = new Chunk(null!, Somewhere, generateBlockData: false);
        plain.SetBlockRaw(1, 2, 3, Stone, 0);
        var column = ColumnCache.Get((int)Somewhere.X, (int)Somewhere.Z);

        foreach (var (source, expected) in new[] { (withBrick, true), (plain, false) })
        {
            var snapshot = source.TakeSnapshot(null);

            var streamed = new Chunk(null!, Somewhere, generateBlockData: false);
            streamed.RestoreFrom(snapshot, streamedColumns: true);
            Assert.Equal(expected, streamed.MayHaveSkyCover);

            var saved = new Chunk(null!, Somewhere, generateBlockData: false);
            saved.RestoreFrom(snapshot, column);
            Assert.Equal(expected, saved.MayHaveSkyCover);
        }
    }

    [Fact]
    public void AGeneratedTowerChunkMayHaveSkyCoverAndAPlainSurfaceChunkDoesNot()
    {
        Assert.True(TowerPlacement.TryFindNearest(0, 0, 4, out var site), "no tower near the origin");
        var wall = new Vector3(
            VoxelCoords.WorldToChunk(site.CenterX + site.Radius - 1),
            VoxelCoords.WorldToChunk(site.GroundY + 3),
            VoxelCoords.WorldToChunk(site.CenterZ));
        var tower = new Chunk(null!, wall, generateBlockData: false);
        ChunkTerrainFill.GenerateBlockData(tower);
        Assert.True(BlockType.ContainsSkyCover(tower.Blocks), "the wall chunk holds no tower brick");
        Assert.True(tower.MayHaveSkyCover);

        var surfaceY = ColumnCache.GetHeights(0, 0)[0];
        var origin = new Chunk(null!, new Vector3(0, VoxelCoords.WorldToChunk(surfaceY), 0), generateBlockData: false);
        ChunkTerrainFill.GenerateBlockData(origin);
        Assert.True(origin.SolidCount > 0, "the origin surface chunk is empty");
        Assert.False(origin.MayHaveSkyCover);
    }
}
