using System;
using System.IO;
using System.Linq;
using System.Numerics;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

/// <summary>
/// The imported map (<see cref="ImportedMapSource"/>, written by <see cref="ImportedMapWriter"/>): a
/// column with a file and its chunk stack come from the folder (a chunk without a file is air),
/// every other column comes from worldgen untouched, an imported copy of a generated region fills
/// exactly like generating it, and a damaged map is refused at construction naming the file while
/// a damaged chunk file degrades to air instead of failing a generation worker.
/// </summary>
[Collection("Worldgen seed")]
public sealed class ImportedMapSourceTests : IDisposable
{
    private const int ColumnArea = Chunk.Size * Chunk.Size;

    private readonly string _map = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ImportedMapSourceTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }

        if (Directory.Exists(_map))
            Directory.Delete(_map, recursive: true);
    }

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    private static Chunk Filled(IWorldSource source, Vector3 pos)
    {
        var chunk = new Chunk(null!, pos, generateBlockData: false);
        source.Fill(chunk);
        Assert.True(chunk.IsBlockDataReady);
        return chunk;
    }

    private static void AssertSameCells(Chunk expected, Chunk actual)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            Assert.True(expected.GetLocalBlock(x, y, z) == actual.GetLocalBlock(x, y, z),
                $"block ({x}, {y}, {z}) of {expected.Position} differs");
            Assert.True(expected.GetLocalState(x, y, z) == actual.GetLocalState(x, y, z),
                $"state ({x}, {y}, {z}) of {expected.Position} differs");
        }
    }

    private static void AssertAllAir(Chunk chunk)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            Assert.True(chunk.GetLocalBlock(x, y, z) == BlockType.Air, $"cell ({x}, {y}, {z}) of {chunk.Position} is not air");
    }

    // A flat column at top, wet over a third of its cells, unlike anything worldgen holds there
    private static ColumnData FlatColumn(int top)
    {
        var heights = Enumerable.Repeat(top, ColumnArea).ToArray();
        var water = new int[ColumnArea];
        for (var i = 0; i < ColumnArea; i++)
            water[i] = i % 3 == 0 ? top + 2 : TerrainGenerator.NoWater;
        var biomes = Enumerable.Repeat(Biome.Desert, ColumnArea).ToArray();
        var dirt = Enumerable.Repeat((byte)WorldGenerationConfig.NominalDirtDepth, ColumnArea).ToArray();
        return new ColumnData(heights, dirt, new float[ColumnArea], new float[ColumnArea], biomes,
            (int[])heights.Clone(), water, cornerWarmth: 0.9f, cornerWetness: 0.1f);
    }

    // A chunk whose cells at or below top are block and the rest air
    private static ChunkSnapshot Slab(int cy, int top, byte block)
    {
        var blocks = new byte[Chunk.BlockCount];
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            if (cy * Chunk.Size + y <= top)
                blocks[Chunk.Idx(x, y, z)] = block;
        return new ChunkSnapshot(blocks, state: null, playerPlaced: null);
    }

    [Fact]
    public void ASuppliedColumnComesFromTheFolderAndAnAbsentChunkIsAir()
    {
        // Mid-chunk, well above worldgen's terrain there, so the slab has air above it in its chunk
        var tallest = ColumnCache.Get(2, 2).MaxHeight + 24;
        var top = (tallest & ~Chunk.SizeMask) + Chunk.Size / 2;
        var cy = top >> Chunk.SizeShift;
        var sandstone = Block("sandstone");
        var column = FlatColumn(top);
        ImportedMapWriter.WriteColumn(_map, 2, 2, column);
        ImportedMapWriter.WriteChunk(_map, 2, cy, 2, Slab(cy, top, sandstone));

        var source = new ImportedMapSource(_map);
        Assert.True(source.Supplies(2, 2));
        Assert.False(source.Supplies(0, 0));
        Assert.False(source.Supplies(2, 3));

        // The written chunk is the slab, restored rather than generated, and not an edit
        var slab = Filled(source, new Vector3(2, cy, 2));
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var expected = slab.LowerBoundY + y <= top ? sandstone : BlockType.Air;
            Assert.True(expected == slab.GetLocalBlock(x, y, z), $"cell ({x}, {y}, {z}) is not the slab");
        }
        Assert.True(slab.IsRestored);
        Assert.False(slab.ModifiedSinceSave);
        Assert.Equal(top, slab.SurfaceMinY);

        // The chunks under it have no file: air, even where worldgen would have filled the chunk
        // with rock (the one below its own lowest surface)
        var underCy = (ColumnCache.Get(2, 2).MinHeight >> Chunk.SizeShift) - 1;
        var rock = new Chunk(null!, new Vector3(2, underCy, 2), generateBlockData: true);
        Assert.True(rock.SolidCount > 0, "worldgen holds nothing under its own surface");
        foreach (var belowCy in new[] { cy - 1, underCy })
        {
            var below = Filled(source, new Vector3(2, belowCy, 2));
            AssertAllAir(below);
            Assert.True(below.IsRestored);
            Assert.Equal(top, below.SurfaceMinY);
        }

        // A column the map does not supply is worldgen, over the cache's own column
        var outside = new Vector3(0, ColumnCache.Get(0, 0).MaxHeight >> Chunk.SizeShift, 0);
        var fallback = Filled(source, outside);
        AssertSameCells(new Chunk(null!, outside, generateBlockData: true), fallback);
        Assert.False(fallback.IsRestored);
        Assert.Same(ColumnCache.Get(0, 0), source.GetColumn(0, 0));

        // The supplied column streams as written, the fallback column as worldgen's
        Assert.True(source.TryGet(2, 2, out var terrain));
        Assert.Equal(column.Heights, terrain.Heights);
        Assert.Equal(column.WaterLevel, terrain.WaterLevel);
        Assert.Equal(BitConverter.SingleToInt32Bits(column.CornerWarmth), BitConverter.SingleToInt32Bits(terrain.Warmth));
        Assert.Equal(BitConverter.SingleToInt32Bits(column.CornerWetness), BitConverter.SingleToInt32Bits(terrain.Wetness));
        Assert.True(WorldgenColumnSource.Instance.TryGet(0, 0, out var generated));
        Assert.True(source.TryGet(0, 0, out var fallbackTerrain));
        Assert.True(generated.ContentEquals(fallbackTerrain));
    }

    [Fact]
    public void AnExportedGeneratedRegionFillsExactlyLikeWorldgen()
    {
        // Every chunk from below the lowest surface (and sea level) to above the highest
        int low = WorldGenerationConfig.SeaLevel, high = WorldGenerationConfig.SeaLevel;
        for (var cx = 0; cx <= 1; cx++)
        for (var cz = 0; cz <= 1; cz++)
        {
            low = Math.Min(low, ColumnCache.Get(cx, cz).MinHeight);
            high = Math.Max(high, ColumnCache.Get(cx, cz).MaxHeight);
        }
        var cyMin = (low >> Chunk.SizeShift) - 1;
        var cyMax = (high >> Chunk.SizeShift) + 1;

        var written = ImportedMapWriter.ExportGenerated(_map, GeneratedWorldSource.Instance, 0, 1, cyMin, cyMax, 0, 1);
        Assert.InRange(written, 1, 4 * (cyMax - cyMin + 1));

        var source = new ImportedMapSource(_map);
        Assert.False(source.Supplies(2, 0));
        Assert.False(source.Supplies(0, -1));
        var stateCells = 0;
        for (var cx = 0; cx <= 1; cx++)
        for (var cz = 0; cz <= 1; cz++)
        {
            Assert.True(source.Supplies(cx, cz));

            var original = ColumnCache.Get(cx, cz);
            var imported = source.GetColumn(cx, cz);
            Assert.NotSame(original, imported);
            Assert.Equal(original.Heights, imported.Heights);
            Assert.Equal(original.Bed, imported.Bed);
            Assert.Equal(original.WaterLevel, imported.WaterLevel);
            Assert.Equal(original.Biomes, imported.Biomes);
            Assert.Equal(original.DirtDepth, imported.DirtDepth);
            Assert.Equal(BitConverter.SingleToInt32Bits(original.CornerWarmth), BitConverter.SingleToInt32Bits(imported.CornerWarmth));
            Assert.Equal(BitConverter.SingleToInt32Bits(original.CornerWetness), BitConverter.SingleToInt32Bits(imported.CornerWetness));

            for (var cy = cyMin; cy <= cyMax; cy++)
            {
                var pos = new Vector3(cx, cy, cz);
                var generated = new Chunk(null!, pos, generateBlockData: true);
                stateCells += CountStateCells(generated);
                var filled = Filled(source, pos);
                AssertSameCells(generated, filled);
                Assert.Equal(generated.SurfaceMinY, filled.SurfaceMinY);
                // The one difference: an imported chunk is restored, never generated
                Assert.True(filled.IsRestored);
                Assert.False(filled.ModifiedSinceSave);
            }
        }

        // The region holds tree wood or leaves, so the state comparison above was not vacuous
        Assert.True(stateCells > 0, "the exported region carries no state byte to compare");
    }

    private static int CountStateCells(Chunk chunk)
    {
        var count = 0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            if (chunk.GetLocalState(x, y, z) != 0)
                count++;
        return count;
    }

    [Fact]
    public void ADamagedChunkFileFillsAsAirInsteadOfFailing()
    {
        var top = ColumnCache.Get(1, 1).MaxHeight + 24;
        var cy = top >> Chunk.SizeShift;
        ImportedMapWriter.WriteColumn(_map, 1, 1, FlatColumn(top));
        ImportedMapWriter.WriteChunk(_map, 1, cy, 1, Slab(cy, top, Block("sandstone")));
        File.WriteAllBytes(Path.Combine(_map, ImportedMapSource.ChunksDirectoryName, WorldStore.ChunkFileName(1, cy, 1)),
            "not a chunk"u8.ToArray());

        var chunk = Filled(new ImportedMapSource(_map), new Vector3(1, cy, 1));
        AssertAllAir(chunk);
        Assert.True(chunk.IsRestored);
    }

    [Fact]
    public void ACorruptColumnFileFailsAtConstructionNamingTheFile()
    {
        ImportedMapWriter.WriteColumn(_map, 0, 0, FlatColumn(ColumnCache.Get(0, 0).MaxHeight));
        var name = ImportedMapWriter.ColumnFileName(1, 1);
        File.WriteAllBytes(Path.Combine(_map, ImportedMapSource.ColumnsDirectoryName, name), "garbage"u8.ToArray());

        var error = Assert.Throws<InvalidDataException>(() => new ImportedMapSource(_map));
        Assert.Contains(name, error.Message);
    }

    [Fact]
    public void AMissingFolderIsRefused()
    {
        Assert.Throws<DirectoryNotFoundException>(() => new ImportedMapSource(_map));

        // A folder without its columns folder is not a map either
        Directory.CreateDirectory(Path.Combine(_map, ImportedMapSource.ChunksDirectoryName));
        Assert.Throws<DirectoryNotFoundException>(() => new ImportedMapSource(_map));
    }
}
