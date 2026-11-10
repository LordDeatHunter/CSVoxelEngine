using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using Xunit;
using static VoxelEngine.Chunks.ChunkBlocks;

namespace VoxelEngine.Tests.Chunks;

/// <summary>
/// A chunk's block storage (<see cref="ChunkBlocks"/>) seen through <see cref="Chunk"/>: every raw writer
/// agrees with a plain dense oracle whatever mode the storage passes through, a generated chunk holds the
/// smallest storage for its ids, and the mesh build's enclosed-chunk skip keeps its exact meaning on top of
/// the palette fast paths. Negative controls: a writer that skipped publishing an upgraded storage
/// (<c>Chunk.WriteId</c>) fails the oracle fact on the first second id; a fill that handed the chunk its
/// dense array without <see cref="ChunkBlocks.FromDense"/> fails the smallest-storage fact; an enclosed
/// check that trusted a stale palette air entry fails the edit-built skip, and one that trusted an
/// occluding palette alone fails the open-face facts.
/// </summary>
public sealed class ChunkStorageTests
{
    public ChunkStorageTests() => TestContent.EnsureLoaded();

    private static ushort Stone => ContentRegistry.RequireBlock("stone").Id;

    private static int CountNonAir(ushort[] cells) => cells.Count(id => id != BlockType.Air);

    private static void AssertMatches(Chunk chunk, ushort[] oracle)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var i = Chunk.Idx(x, y, z);
            Assert.True(chunk.GetLocalBlock(x, y, z) == oracle[i], $"cell {x},{y},{z} reads {chunk.GetLocalBlock(x, y, z)}, not {oracle[i]}");
        }

        Assert.Equal(oracle, chunk.Storage.ToDense());
        Assert.Equal(CountNonAir(oracle), chunk.SolidCount);
    }

    [Fact]
    public void EditsThroughEveryWriterAgreeWithADenseOracle()
    {
        // Every assigned id plus air, so a big enough registry also drives the storage past the palette
        var ids = Enumerable.Range(0, BlockTables.IdCount)
            .Where(id => id == 0 || BlockType.ById[id] != null)
            .Select(id => (ushort)id)
            .ToArray();
        Assert.True(ids.Length > 2, "the test content holds too few blocks to mix");

        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        var oracle = new ushort[Chunk.BlockCount];
        var random = new Random(1234);
        for (var batch = 0; batch < 8; batch++)
        {
            // Early batches draw from a few ids (in-mode Indexed writes), later ones from all of them
            var pool = Math.Min(ids.Length, 3 + batch * batch * 8);
            for (var n = 0; n < 1500; n++)
            {
                var (x, y, z) = (random.Next(Chunk.Size), random.Next(Chunk.Size), random.Next(Chunk.Size));
                var i = Chunk.Idx(x, y, z);
                var id = ids[random.Next(pool)];
                switch (random.Next(3))
                {
                    case 0:
                        Assert.Equal(oracle[i], chunk.SetBlockRaw(x, y, z, id, 0));
                        oracle[i] = id;
                        break;
                    case 1:
                        var removable = oracle[i] != BlockType.Air && !BlockType.IsUnbreakable(oracle[i]);
                        Assert.Equal(removable ? oracle[i] : BlockType.Air, chunk.ClearBlockAt(new Vector3(x, y, z)));
                        if (removable) oracle[i] = BlockType.Air;
                        break;
                    default:
                        Assert.Equal(oracle[i], chunk.CollapseWriteAt(new Vector3(x, y, z), id, 0));
                        oracle[i] = id;
                        break;
                }
            }

            AssertMatches(chunk, oracle);
        }
    }

    [Fact]
    public void AFreshChunkIsAirAndAcceptsWrites()
    {
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        AssertMatches(chunk, new ushort[Chunk.BlockCount]);

        Assert.Equal(BlockType.Air, chunk.SetBlockRaw(3, 4, 5, Stone, 0));
        Assert.Equal(Stone, chunk.GetLocalBlock(3, 4, 5));
        Assert.Equal(1, chunk.SolidCount);
        // The write went to this chunk alone: a second fresh chunk still reads air there
        Assert.Equal(BlockType.Air, new Chunk(null!, Vector3.Zero, generateBlockData: false).GetLocalBlock(3, 4, 5));
    }

    [Fact]
    public void AGeneratedChunkHoldsTheSmallestStorageForItsIds()
    {
        var surface = new Vector3(0, ColumnCache.Get(0, 0).MaxHeight >> Chunk.SizeShift, 0);
        Vector3[] positions =
        [
            new(0, 64, 0),
            surface,
            surface with { Y = surface.Y - 3 },
            new(0, (WorldGenerationConfig.HellTopY >> Chunk.SizeShift) - 2, 0),
        ];

        foreach (var pos in positions)
        {
            var chunk = new Chunk(null!, pos, generateBlockData: false);
            ChunkTerrainFill.GenerateBlockData(chunk);
            var cells = chunk.Storage.ToDense();
            var distinct = cells.Distinct().Count();
            var expected = distinct == 1 ? StorageMode.Uniform
                : distinct <= PaletteCapacity ? StorageMode.Indexed
                : StorageMode.Direct;

            Assert.True(expected == chunk.Storage.Mode, $"{pos} holds {distinct} ids in a {chunk.Storage.Mode} storage");
            Assert.True(chunk.Storage.IsCompact, $"{pos} storage is not compact");
            Assert.Equal(CountNonAir(cells), chunk.SolidCount);
        }
    }

    /// <summary>The walled test world with every chunk restored from one dense all-stone snapshot.</summary>
    private static (ChunkManager Manager, Chunk Center) RestoredStoneWorld()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        var stone = Enumerable.Repeat(Stone, Chunk.BlockCount).ToArray();
        foreach (var chunk in manager.Chunks.Values)
            chunk.RestoreFrom(new ChunkSnapshot(stone, state: null, playerPlaced: null), streamedColumns: true);
        return (manager, center);
    }

    [Fact]
    public void ASealedUniformChunkTakesTheEnclosedSkip()
    {
        var (manager, center) = RestoredStoneWorld();
        Assert.Equal(StorageMode.Uniform, center.Storage.Mode);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.TranslucentVertices);
        // Only the enclosed skip returns without a light volume; a full build always bakes one
        Assert.Null(mesh.Light);
    }

    [Fact]
    public void AChunkFilledByEditsStillTakesTheEnclosedSkip()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        MeshTestWorld.FillWalledArea(manager, Stone);
        // Filled from air through the edit path, so the palette keeps the air every cell left
        Assert.True(center.Storage.IdSpan.Contains(BlockType.Air), "the edit-built palette dropped its stale air entry");
        Assert.Equal(Chunk.BlockCount, center.SolidCount);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Empty(mesh.Vertices);
        Assert.Null(mesh.Light);
    }

    [Fact]
    public void OneOpenCellOnASharedFaceEndsTheEnclosedSkip()
    {
        var (manager, center) = RestoredStoneWorld();
        manager.Chunks[new Vector3(1, 0, 0)].SetBlockRaw(0, 8, 8, BlockType.Air, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh.Vertices);
        Assert.NotNull(mesh.Light);

        // A full center holding one block that does not occlude meshes normally too
        var open = Enumerable.Range(1, BlockTables.IdCount - 1).Select(id => (ushort)id)
            .First(id => BlockType.ById[id] != null && !BlockType.OccludesById[id] && !BlockType.IsFluid(id));
        (manager, center) = RestoredStoneWorld();
        center.SetBlockRaw(8, 8, 8, open, 0);
        Assert.Equal(Chunk.BlockCount, center.SolidCount);

        mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh.Vertices);
        Assert.NotNull(mesh.Light);
    }
}
