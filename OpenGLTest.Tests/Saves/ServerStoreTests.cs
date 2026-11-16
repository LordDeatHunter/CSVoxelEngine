using System;
using System.IO;
using System.Linq;
using VoxelEngine.Saves;
using Xunit;

namespace VoxelEngine.Tests.Saves;

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
        if (File.Exists(_path + ".tmp"))
            File.Delete(_path + ".tmp");
        if (File.Exists(_path))
            File.Delete(_path);
        if (Directory.Exists(_path + ".bak"))
            Directory.Delete(_path + ".bak", recursive: true);
        else if (File.Exists(_path + ".bak"))
            File.Delete(_path + ".bak");
    }

    [Fact]
    public void AbsentFileListsEmpty()
    {
        Assert.Empty(new ServerStore(_path).List());
    }

    [Fact]
    public void MissingFileIsASuccessfulEmptyRead()
    {
        Assert.True(_store.TryList(out var servers));
        Assert.Empty(servers);
    }

    [Fact]
    public void ListKeepsItsBestEffortEmptyResultOnReadFailure()
    {
        _store.ReadAllText = _ => throw new IOException("injected server-list read failure");

        Assert.False(_store.TryList(out var statusServers));
        Assert.Empty(statusServers);
        Assert.Empty(_store.List());
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

    [Fact]
    public void ABrokenFileIsSetAsideInsteadOfOverwritten()
    {
        File.WriteAllText(_path, "[{\"Name\":\"Home\",\"Host\":\"10.0.0.1\",\"Port\":\"not a number\"}]");
        Assert.Empty(_store.List());

        _store.Add("New", "10.0.0.2", 25566);
        Assert.True(File.Exists(_path + ".bak"), "the unreadable file was not kept");
        Assert.Contains("10.0.0.1", File.ReadAllText(_path + ".bak"));
        File.Delete(_path + ".bak");
        var only = Assert.Single(_store.List());
        Assert.Equal("New", only.Name);
    }

    [Theory]
    [InlineData("add", false)]
    [InlineData("delete", false)]
    [InlineData("touch", false)]
    [InlineData("add", true)]
    [InlineData("delete", true)]
    [InlineData("touch", true)]
    public void ReadFailureDoesNotReplaceSavedBytes(string mutation, bool unauthorized)
    {
        var server = _store.Add("Keep", "10.0.0.1", 25566);
        var original = File.ReadAllBytes(_path);
        var originalLastPlayed = server.LastPlayedUnixMs;
        _store.ReadAllText = _ =>
        {
            if (unauthorized)
                throw new UnauthorizedAccessException("injected server-list read failure");
            throw new IOException("injected server-list read failure");
        };

        Exception? failure = null;
        try
        {
            Mutate(mutation, server);
        }
        catch (Exception e)
        {
            failure = e;
        }

        var after = File.ReadAllBytes(_path);
        var bytesUnchanged = original.SequenceEqual(after);
        var timestampUnchanged = mutation != "touch" || server.LastPlayedUnixMs == originalLastPlayed;
        Assert.True(failure is IOException && bytesUnchanged && timestampUnchanged,
            $"exception={failure?.GetType().Name ?? "none"}; bytesUnchanged={bytesUnchanged}; " +
            $"beforeLength={original.Length}; afterLength={after.Length}; timestampUnchanged={timestampUnchanged}");
    }

    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("touch")]
    public void FailedBrokenFileArchiveDoesNotReplaceSavedBytes(string mutation)
    {
        var server = _store.Add("Keep", "10.0.0.1", 25566);
        var original = System.Text.Encoding.UTF8.GetBytes("[{ broken server list bytes");
        File.WriteAllBytes(_path, original);
        Directory.CreateDirectory(_path + ".bak");

        Exception? failure = null;
        try
        {
            Mutate(mutation, server);
        }
        catch (Exception e)
        {
            failure = e;
        }

        var after = File.ReadAllBytes(_path);
        var bytesUnchanged = original.SequenceEqual(after);
        Assert.True(failure is IOException && bytesUnchanged,
            $"exception={failure?.GetType().Name ?? "none"}; bytesUnchanged={bytesUnchanged}; " +
            $"beforeLength={original.Length}; afterLength={after.Length}");
    }

    [Fact]
    public void OddEntriesNeverThrowAndAnIdlessEntryStaysDeletable()
    {
        File.WriteAllText(_path, "[null, {\"Name\":\"Old\",\"Host\":\"10.0.0.1\",\"Port\":25566}]");
        var old = Assert.Single(_store.List());
        Assert.Equal("Old", old.Name);

        _store.Delete(old);
        Assert.Empty(_store.List());
    }

    private void Mutate(string mutation, ServerInfo server)
    {
        switch (mutation)
        {
            case "add":
                _store.Add("New", "10.0.0.2", 25567);
                break;
            case "delete":
                _store.Delete(server);
                break;
            case "touch":
                _store.TouchLastPlayed(server);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation.");
        }
    }
}
