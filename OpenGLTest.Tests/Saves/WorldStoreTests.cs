using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using VoxelEngine.Networking.Hosting;
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
    public void LegacyListKeepsItsEmptyResultWhenRootEnumerationFails()
    {
        Directory.CreateDirectory(_root);
        var store = new WorldStore(_root)
        {
            EnumerateDirectories = _ => throw new IOException("injected enumeration failure"),
        };

        Assert.Empty(store.List());
    }

    [Fact]
    public void TryListTreatsAnAbsentRootAsAReadableEmptyList()
    {
        var store = new WorldStore(_root);

        Assert.True(store.TryList(out var worlds));
        Assert.Empty(worlds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryListReportsRootEnumerationFailures(bool unauthorized)
    {
        Directory.CreateDirectory(_root);
        var store = new WorldStore(_root)
        {
            EnumerateDirectories = _ =>
            {
                if (unauthorized)
                    throw new UnauthorizedAccessException("injected enumeration failure");
                throw new IOException("injected enumeration failure");
            },
        };

        Assert.False(store.TryList(out var worlds));
        Assert.Empty(worlds);
        Assert.Empty(store.List());
    }

    [Fact]
    public void CreateRefusesWhenWorldNamesCannotBeListed()
    {
        var store = new WorldStore(_root);
        var existing = store.Create("Home", 1);
        var existingMetadataPath = Path.Combine(existing.Directory, WorldStore.MetadataFileName);
        var existingMetadata = File.ReadAllBytes(existingMetadataPath);
        var foldersBefore = Directory.GetDirectories(_root).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var failNext = true;
        store.EnumerateDirectories = path =>
        {
            if (!failNext) return Directory.GetDirectories(path);
            failNext = false;
            throw new IOException("injected enumeration failure");
        };

        var failure = Record.Exception(() => store.Create("Home", 2));
        var foldersAfter = Directory.GetDirectories(_root).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var worlds = store.List();
        var metadataUnchanged = File.ReadAllBytes(existingMetadataPath).SequenceEqual(existingMetadata);
        var namesUnique = worlds.Select(world => world.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() == worlds.Count;

        Assert.True(failure is IOException && foldersBefore.SequenceEqual(foldersAfter, StringComparer.Ordinal)
                    && metadataUnchanged && namesUnique,
            $"failure={failure?.GetType().Name ?? "none"}; folders=[{string.Join(",", foldersAfter)}]; " +
            $"names=[{string.Join(",", worlds.Select(world => world.Name))}]; metadataUnchanged={metadataUnchanged}");
    }

    [Fact]
    public void RenameRefusesWhenWorldNamesCannotBeListed()
    {
        var store = new WorldStore(_root);
        store.Create("A", 1);
        var renamed = store.Create("B", 2);
        var metadataPath = Path.Combine(renamed.Directory, WorldStore.MetadataFileName);
        var metadataBefore = File.ReadAllBytes(metadataPath);
        var failNext = true;
        store.EnumerateDirectories = path =>
        {
            if (!failNext) return Directory.GetDirectories(path);
            failNext = false;
            throw new IOException("injected enumeration failure");
        };

        var failure = Record.Exception(() => store.Rename(renamed, "A"));
        var metadataAfter = File.ReadAllBytes(metadataPath);
        var names = store.List().Select(world => world.Name).ToArray();
        var namesUnique = names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length;

        Assert.True(failure is IOException && renamed.Name == "B" && metadataBefore.SequenceEqual(metadataAfter)
                    && namesUnique,
            $"failure={failure?.GetType().Name ?? "none"}; inMemoryName={renamed.Name}; " +
            $"names=[{string.Join(",", names)}]; metadataUnchanged={metadataBefore.SequenceEqual(metadataAfter)}");
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
    public void NewWorldsPersistDynamicTerrainAndOldFilesStayUnmarkedAcrossRewrites()
    {
        var store = new WorldStore(_root);
        var created = store.Create("New terrain", 7);
        Assert.Equal(2, TerrainProperty(created.Directory)?.GetInt32());
        store.Rename(created, "Renamed terrain");
        store.TouchLastPlayed(created);
        Assert.Equal(2, TerrainProperty(created.Directory)?.GetInt32());

        var old = store.Create("Old terrain", 8);
        var path = Path.Combine(old.Directory, WorldStore.MetadataFileName);
        File.WriteAllText(path, """{"Name":"Old terrain","Seed":8,"Created":1,"LastPlayed":1,"Version":2}""");
        var listed = store.List().Single(w => w.Slug == old.Slug);
        store.Rename(listed, "Old renamed");
        store.TouchLastPlayed(listed);
        Assert.Null(TerrainProperty(old.Directory));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("\"dynamic\"")]
    [InlineData("null")]
    public void UnsupportedTerrainRemainsListedAndCannotBeRewritten(string value)
    {
        var store = new WorldStore(_root);
        var created = store.Create("Unrecognized", 9);
        var path = Path.Combine(created.Directory, WorldStore.MetadataFileName);
        var original = "{\"Name\":\"Unrecognized\",\"Seed\":9,\"Version\":2,\"terrain\":" + value + "}";
        File.WriteAllText(path, original);

        var listed = Assert.Single(store.List());
        Assert.False(ServerLaunch.IsHostable(listed));
        Assert.Throws<InvalidDataException>(() => store.Rename(listed, "Lost"));
        Assert.Throws<InvalidDataException>(() => store.TouchLastPlayed(listed));
        Assert.Equal(original, File.ReadAllText(path));
    }

    private static JsonElement? TerrainProperty(string worldDirectory)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(worldDirectory, WorldStore.MetadataFileName)));
        foreach (var property in json.RootElement.EnumerateObject())
            if (property.Name.Equals("terrain", StringComparison.OrdinalIgnoreCase))
                return property.Value.Clone();
        return null;
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
