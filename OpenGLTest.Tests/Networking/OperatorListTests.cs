using System;
using System.IO;
using System.Text;
using VoxelEngine.Networking;
using VoxelEngine.Saves;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// The dedicated server's operator list: a name is an operator exactly when it shares the save slug
/// of a listed one (the same normalization that keys the player's save file), a broken or missing
/// <see cref="OperatorList.FileName"/> lists nobody, and the command line's <c>--op</c> names and
/// the world folder's file merge into one list.
/// </summary>
public sealed class OperatorListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static OperatorList Parse(string json) => OperatorList.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void NamesMatchBySlugAndBadInputIsEmpty()
    {
        var ops = new OperatorList();
        ops.Add("Alice");

        // Whatever spelling shares Alice's save file is Alice, and nothing else is: in particular not
        // "Alice 2", the name a second Alice is given when she joins while the first is online
        foreach (var name in new[] { "alice", "ALICE", "  Alice ", "Alice!", "Alice 2", "Bob", "Alicia" })
        {
            var sameSave = ServerPlayerSaves.FileNameFor(name) == ServerPlayerSaves.FileNameFor("Alice");
            Assert.True(ops.Contains(name) == sameSave, $"'{name}' sameSave={sameSave}");
        }
        Assert.False(ops.Contains("Alice 2"));
        Assert.False(ops.Contains("Bob"));
        Assert.False(ops.Contains(null));
        Assert.False(ops.Contains("   "));

        // A spelling of a listed name, or a blank one, adds no second operator
        var before = ops.Count;
        ops.Add("ALICE");
        ops.Add("  ");
        ops.Add(null);
        Assert.Equal(before, ops.Count);

        // No file lists nobody, and so does every file that is not a JSON array of names
        Assert.Equal(0, OperatorList.Parse(null).Count);
        Assert.Equal(0, OperatorList.Parse([]).Count);
        Assert.Equal(0, Parse("not json at all").Count);
        Assert.Equal(0, Parse("[\"Alice\"").Count);
        Assert.Equal(0, Parse("{\"ops\": [\"Alice\"]}").Count);
        Assert.Equal(0, Parse("\"Alice\"").Count);
        Assert.Equal(0, Parse("null").Count);

        // Entries that are not names are skipped, the names beside them kept
        var mixed = Parse("[\"Alice\", 3, null, {\"name\": \"Carol\"}, [\"Dave\"], \"Bob\"]");
        Assert.True(mixed.Contains("Alice"));
        Assert.True(mixed.Contains("Bob"));
        Assert.False(mixed.Contains("Carol"));
        Assert.False(mixed.Contains("Dave"));

        // A hand-edited file: a byte-order mark, comments and a trailing comma are all tolerated
        var edited = OperatorList.Parse([.. Encoding.UTF8.GetPreamble(),
            .. Encoding.UTF8.GetBytes("// server operators\n[\n  \"Alice\", // the host\n  \"Bob\",\n]\n")]);
        Assert.True(edited.Contains("alice"));
        Assert.True(edited.Contains("bob"));
    }

    [Fact]
    public void ArgsAndFileMerge()
    {
        // Every --op names the argument after it, the flag in any case; other flags name nobody
        var args = OperatorList.FromArgs(["--allow-creative", "--op", "Alice", "--OP", "Bob", "--op"]);
        Assert.True(args.Contains("Alice"));
        Assert.True(args.Contains("Bob"));
        Assert.False(args.Contains("--allow-creative"));
        Assert.False(args.Contains("--op"));
        Assert.Equal(0, OperatorList.FromArgs(["--op", "--allow-creative"]).Count);
        Assert.Equal(0, OperatorList.FromArgs([]).Count);

        // The world folder's file, read the way the server reads it: through the world's store
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, OperatorList.FileName), "[\"Carol\", \"alice\"]");
        var store = new ChunkSaveStore(_dir);
        OperatorList file;
        try
        {
            file = OperatorList.Parse(store.ReadFile(OperatorList.FileName));
        }
        finally
        {
            Assert.True(store.Shutdown(TimeSpan.FromSeconds(10)));
        }
        Assert.True(file.Contains("Carol"));
        Assert.False(file.Contains("Bob"));

        // Merged, the list holds both sources, and a name in both is one operator
        var merged = OperatorList.FromArgs(["--op", "Alice", "--op", "Bob"]);
        var argsOnly = merged.Count;
        merged.AddAll(file);
        foreach (var name in new[] { "Alice", "Bob", "Carol" })
            Assert.True(merged.Contains(name), name);
        Assert.False(merged.Contains("Dave"));
        Assert.Equal(argsOnly + 1, merged.Count);
        Assert.Equal(merged.Count, merged.Names.Count);
    }
}
