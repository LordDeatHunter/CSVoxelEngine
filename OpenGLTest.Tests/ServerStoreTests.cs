using System;
using System.IO;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class ServerStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
    private readonly ServerStore _store;
    private long _now = 100;

    public ServerStoreTests()
    {
        _store = new ServerStore(_path) { Clock = () => _now++ };
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public void AbsentFileListsEmpty()
    {
        Assert.Empty(new ServerStore(_path).List());
    }

    [Fact]
    public void AddPersistsAndReloads()
    {
        var added = _store.Add("Home", "10.0.0.1", 25566);

        var reloaded = new ServerStore(_path).List();
        var only = Assert.Single(reloaded);
        Assert.Equal(added.Id, only.Id);
        Assert.Equal("Home", only.Name);
        Assert.Equal("10.0.0.1", only.Host);
        Assert.Equal(25566, only.Port);
        Assert.Equal(added.Address, only.Address);
    }

    [Fact]
    public void DuplicateNamesGetTheSmallestFreeSuffix()
    {
        _store.Add("Server", "10.0.0.1", 25566);
        _store.Add("Server", "10.0.0.2", 25566);
        _store.Add("Server", "10.0.0.3", 25566);

        var names = _store.List().Select(s => s.Name).OrderBy(n => n).ToArray();
        Assert.Equal(3, names.Length);
        Assert.Contains("Server", names);
        Assert.Contains("Server 2", names);
        Assert.Contains("Server 3", names);
    }

    [Fact]
    public void NamesAreUniqueCaseInsensitively()
    {
        _store.Add("Server", "10.0.0.1", 25566);
        _store.Add("server", "10.0.0.2", 25566);

        var names = _store.List().Select(s => s.Name).ToArray();
        Assert.Equal(2, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void BlankNameFallsBackToTheDefault()
    {
        var added = _store.Add("   ", "10.0.0.1", 25566);
        Assert.Equal(WorldNames.DefaultName, added.Name);
    }

    [Fact]
    public void DeleteRemovesOnlyThatServer()
    {
        var a = _store.Add("A", "10.0.0.1", 25566);
        _store.Add("B", "10.0.0.2", 25566);

        _store.Delete(a);

        var only = Assert.Single(_store.List());
        Assert.Equal("B", only.Name);
    }

    [Fact]
    public void MostRecentlyPlayedComesFirst()
    {
        var a = _store.Add("A", "10.0.0.1", 25566);
        var b = _store.Add("B", "10.0.0.2", 25566);

        _store.TouchLastPlayed(a);

        Assert.Equal(a.Id, _store.List()[0].Id);
        Assert.Equal(b.Id, _store.List()[1].Id);
    }

    [Fact]
    public void BrokenFileListsEmptyWithAWarning()
    {
        File.WriteAllText(_path, "{ not json");
        Assert.Empty(_store.List());
    }

    [Fact]
    public void EntriesWithBadPortsAreSkipped()
    {
        File.WriteAllText(_path,
            "[{\"Name\":\"Good\",\"Host\":\"10.0.0.1\",\"Port\":25566}," +
            "{\"Name\":\"Bad\",\"Host\":\"10.0.0.2\",\"Port\":70000}," +
            "{\"Name\":\"NoHost\",\"Host\":\"\",\"Port\":25566}]");

        var only = Assert.Single(_store.List());
        Assert.Equal("Good", only.Name);
    }
}
