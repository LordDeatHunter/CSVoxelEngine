using System;
using System.IO;
using System.Linq;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Saves;

/// <summary>
/// The dedicated server's choice of world source (<see cref="WorldSources.Create"/>): no name or
/// <c>generated</c> is worldgen, <c>imported</c> is the world folder's own imported map and fails
/// when the world has none, and an unknown name is refused naming every valid one.
/// </summary>
public sealed class WorldSourcesTests : IDisposable
{
    private const int ColumnArea = Chunk.Size * Chunk.Size;

    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void CreateResolvesKnownNamesAndRefusesOthers()
    {
        var withMap = Path.Combine(_root, "with-map");
        var withoutMap = Path.Combine(_root, "without-map");
        Directory.CreateDirectory(withoutMap);
        // One supplied column, so the source built is provably this world's own map
        var heights = Enumerable.Repeat(64, ColumnArea).ToArray();
        ImportedMapWriter.WriteColumn(Path.Combine(withMap, ImportedMapSource.DirectoryName), 3, -4,
            new ColumnData(heights, new byte[ColumnArea], new float[ColumnArea], new float[ColumnArea]));

        // No name, a blank one and the generated name in any case are worldgen, whatever the folder
        // holds: a generated world never reads its folder's map
        foreach (var name in new[] { null, "", "  ", WorldSources.Generated, "Generated", " GENERATED " })
        {
            Assert.Same(GeneratedWorldSource.Instance, WorldSources.Create(name, withMap));
            Assert.Same(GeneratedWorldSource.Instance, WorldSources.Create(name, withoutMap));
        }

        foreach (var name in new[] { WorldSources.Imported, "Imported", " IMPORTED " })
        {
            var imported = Assert.IsType<ImportedMapSource>(WorldSources.Create(name, withMap));
            Assert.Equal(Path.GetFullPath(Path.Combine(withMap, ImportedMapSource.DirectoryName)), imported.MapDirectory);
            Assert.True(imported.Supplies(3, -4));
            Assert.False(imported.Supplies(0, 0));
        }

        Assert.Throws<DirectoryNotFoundException>(() => WorldSources.Create(WorldSources.Imported, withoutMap));
        // A map folder without its columns folder is no map either
        Directory.CreateDirectory(Path.Combine(withoutMap, ImportedMapSource.DirectoryName));
        Assert.Throws<DirectoryNotFoundException>(() => WorldSources.Create(WorldSources.Imported, withoutMap));

        var refused = Assert.Throws<ArgumentException>(() => WorldSources.Create("x", withMap));
        Assert.All(WorldSources.Names, valid => Assert.Contains(valid, refused.Message));
        Assert.Contains(WorldSources.Generated, WorldSources.Names);
        Assert.Contains(WorldSources.Imported, WorldSources.Names);
    }

    [Fact]
    public void AWorldInfoWithoutASourceReadsAsGenerated()
    {
        Assert.Equal(WorldSources.Generated, new WorldInfo("w", "W", 0, 0, 0, WorldStore.CurrentVersion, _root).Source);

        var blank = new WorldInfo("w", "W", 0, 0, 0, WorldStore.CurrentVersion, _root, "   ");
        Assert.Null(blank.SourceName);
        Assert.Equal(WorldSources.Generated, blank.Source);

        var named = new WorldInfo("w", "W", 0, 0, 0, WorldStore.CurrentVersion, _root, " imported ");
        Assert.Equal(WorldSources.Imported, named.SourceName);
        Assert.Equal(named.SourceName, named.Source);
    }
}
