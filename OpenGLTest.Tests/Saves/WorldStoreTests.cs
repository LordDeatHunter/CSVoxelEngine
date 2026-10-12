using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using VoxelEngine.Saves;
using Xunit;

namespace VoxelEngine.Tests.Saves;

public sealed class WorldStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void CreateThenListRoundTripsMetadata()
    {
        var store = new WorldStore(_root);
        store.Create("Home", 99);

        var world = Assert.Single(store.List());
        Assert.Equal("Home", world.Name);
        Assert.Equal(99, world.Seed);
        Assert.True(Directory.Exists(world.Directory));
        Assert.True(Directory.Exists(Path.Combine(world.Directory, WorldStore.ChunksDirectoryName)));
        Assert.Equal(world.CreatedUnixMs, world.LastPlayedUnixMs);
    }

    [Fact]
    public void CreatingDuplicateNamesKeepsNamesAndFoldersUnique()
    {
        var store = new WorldStore(_root);
        var worlds = Enumerable.Range(0, 3).Select(_ => store.Create("Same", 1)).ToArray();

        Assert.Equal(3, worlds.Select(w => w.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(3, worlds.Select(w => w.Slug).Distinct().Count());
        foreach (var world in worlds)
            Assert.True(Directory.Exists(Path.Combine(_root, world.Slug)));
    }

    [Fact]
    public void RenameKeepsTheFolderAndReUniquifies()
    {
        var store = new WorldStore(_root);
        var a = store.Create("A", 1);
        var b = store.Create("B", 2);
        var slug = b.Slug;
        var directory = b.Directory;

        store.Rename(b, "a");

        Assert.False(string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(slug, b.Slug);
        Assert.Equal(directory, b.Directory);
        var listed = store.List();
        Assert.Equal(2, listed.Count);
        Assert.Equal(b.Name, listed.Single(w => w.Slug == slug).Name);
        Assert.Equal(a.Name, listed.Single(w => w.Slug == a.Slug).Name);
    }

    [Fact]
    public void DeleteRemovesTheFolderAndRefusesEscapes()
    {
        var store = new WorldStore(_root);
        var keep = store.Create("Keep", 1);
        var doomed = store.Create("Doomed", 2);

        store.Delete(doomed);

        Assert.False(Directory.Exists(doomed.Directory));
        Assert.DoesNotContain(store.List(), w => w.Slug == doomed.Slug);

        var outside = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(outside);
        try
        {
            var forged = new WorldInfo("x", "x", 0, 0, 0, 0, outside);
            Assert.Throws<ArgumentException>(() => store.Delete(forged));
            Assert.True(Directory.Exists(outside));

            var theRoot = new WorldInfo("x", "x", 0, 0, 0, 0, _root);
            Assert.Throws<ArgumentException>(() => store.Delete(theRoot));

            var escaping = new WorldInfo("x", "x", 0, 0, 0, 0, Path.Combine(_root, "..", Path.GetFileName(outside)));
            Assert.Throws<ArgumentException>(() => store.Delete(escaping));
            Assert.True(Directory.Exists(outside));
            Assert.True(Directory.Exists(keep.Directory));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void CorruptWorldJsonIsSkippedNotFatal()
    {
        var store = new WorldStore(_root);
        var good = store.Create("Good", 1);
        var bad = store.Create("Bad", 2);
        File.WriteAllText(Path.Combine(bad.Directory, WorldStore.MetadataFileName), "{ not json ]");
        Directory.CreateDirectory(Path.Combine(_root, "no-metadata"));

        var listed = store.List();

        var only = Assert.Single(listed);
        Assert.Equal(good.Slug, only.Slug);
    }

    [Fact]
    public void TouchLastPlayedReordersTheList()
    {
        long now = 1_000;
        var store = new WorldStore(_root) { Clock = () => now };
        var a = store.Create("A", 1);
        now += 10;
        var b = store.Create("B", 2);
        Assert.Equal(b.Slug, store.List()[0].Slug);

        now += 10;
        store.TouchLastPlayed(a);

        Assert.Equal(a.Slug, store.List()[0].Slug);
        Assert.True(store.List()[0].LastPlayedUnixMs > store.List()[1].LastPlayedUnixMs);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-3, 7, -12)]
    [InlineData(int.MinValue, -1, int.MaxValue)]
    public void ChunkFileNameRoundTrips(int cx, int cy, int cz)
    {
        Assert.True(WorldStore.TryParseChunkFileName(WorldStore.ChunkFileName(cx, cy, cz), out var x, out var y, out var z));
        Assert.Equal((cx, cy, cz), (x, y, z));
    }

    [Theory]
    [InlineData("notachunk.bin")]
    [InlineData("1_2.bin")]
    [InlineData("1_2_3_4.bin")]
    [InlineData("1_2_3.bin.tmp")]
    [InlineData("1_2_3.txt")]
    public void NonChunkFileNamesAreRejected(string fileName)
    {
        Assert.False(WorldStore.TryParseChunkFileName(fileName, out _, out _, out _));
    }

    [Fact]
    public void ASeedlessWorldFileFallsBackToTheDefaultSeedNotTheLastApplied()
    {
        var store = new WorldStore(_root);
        var created = store.Create("Old", 7);
        File.WriteAllText(Path.Combine(created.Directory, WorldStore.MetadataFileName),
            "{ \"Name\": \"Old\", \"Created\": 1, \"LastPlayed\": 1, \"Version\": " + WorldStore.CurrentVersion + " }");

        var listed = Assert.Single(store.List());
        Assert.Equal(WorldSeed.Default, listed.Seed);
    }

    [Fact]
    public void AWorldsSourceSurvivesARenameAndATouchAndAnUnsetOneIsNeverWritten()
    {
        long now = 1_000;
        var store = new WorldStore(_root) { Clock = () => now };
        var imported = store.Create("Imported", 7);
        var plain = store.Create("Plain", 8);
        File.WriteAllText(Path.Combine(imported.Directory, WorldStore.MetadataFileName),
            "{ \"Name\": \"Imported\", \"Seed\": 7, \"Created\": 1, \"LastPlayed\": 1, \"Version\": "
            + WorldStore.CurrentVersion + ", \"source\": \"imported\" }");

        var listed = store.List().Single(w => w.Slug == imported.Slug);
        Assert.Equal("imported", listed.SourceName);
        Assert.Equal(WorldSources.Imported, listed.Source);

        // Both rewrites of world.json keep the source the file named
        now += 10;
        store.Rename(listed, "Renamed");
        now += 10;
        store.TouchLastPlayed(listed);
        var reread = store.List().Single(w => w.Slug == imported.Slug);
        Assert.Equal("Renamed", reread.Name);
        Assert.Equal(listed.SourceName, reread.SourceName);
        Assert.Equal(WorldSources.Imported, reread.Source);
        Assert.Equal(listed.SourceName, SourceProperty(reread.Directory)?.GetString());

        // A world the game created names no source, reads as generated, and gains no source
        // property however often its file is rewritten
        var unset = store.List().Single(w => w.Slug == plain.Slug);
        Assert.Null(unset.SourceName);
        Assert.Equal(WorldSources.Generated, unset.Source);
        Assert.Null(SourceProperty(unset.Directory));
        now += 10;
        store.Rename(unset, "Still Plain");
        now += 10;
        store.TouchLastPlayed(unset);
        Assert.Null(SourceProperty(unset.Directory));
        Assert.Null(store.List().Single(w => w.Slug == plain.Slug).SourceName);
    }

    // The world.json "source" property, matched case-insensitively like the reader does, or null
    // when the file has no such property at all (a property written as JSON null still counts)
    private static JsonElement? SourceProperty(string worldDirectory)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(worldDirectory, WorldStore.MetadataFileName)));
        foreach (var property in json.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("source", StringComparison.OrdinalIgnoreCase))
                return property.Value.Clone();
        }
        return null;
    }
}
