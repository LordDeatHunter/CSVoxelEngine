using Xunit;

namespace VoxelEngine.Tests.Core;

public sealed class LogTests
{
    [Fact]
    public void WriteLineAfterShutdownFallsBackWithoutThrow()
    {
        Log.WriteLine("log-test line");
        Log.Shutdown();
        Log.WriteLine("log-test post-shutdown line");
    }
}
