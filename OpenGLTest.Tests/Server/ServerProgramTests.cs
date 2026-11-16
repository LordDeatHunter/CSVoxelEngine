using System.IO;
using VoxelEngine.Saves;
using VoxelEngine.Server;
using Xunit;

namespace VoxelEngine.Tests.Server;

public sealed class ServerProgramTests : System.IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void WorldPromptReportsRootListingFailureWithoutOfferingCreation()
    {
        Directory.CreateDirectory(_root);
        var store = new WorldStore(_root)
        {
            EnumerateDirectories = _ => throw new IOException("injected enumeration failure"),
        };
        using var input = new StringReader("");
        using var output = new StringWriter();

        var selected = ServerProgram.PromptWorld(store, input, output);

        Assert.Null(selected);
        Assert.Contains("Could not list saved worlds", output.ToString());
        Assert.DoesNotContain("No saved worlds yet.", output.ToString());
        Assert.DoesNotContain("Choose a world number", output.ToString());
        Assert.DoesNotContain("N for a new world", output.ToString());
    }
}
