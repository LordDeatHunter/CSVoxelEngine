using VoxelEngine.Networking;
using Xunit;

namespace VoxelEngine.Tests.Networking;

public sealed class ServerAddressTests
{
    [Theory]
    [InlineData("192.168.1.10", "192.168.1.10", ServerAddress.DefaultPort)]
    [InlineData("play.example.net", "play.example.net", ServerAddress.DefaultPort)]
    [InlineData("192.168.1.10:25599", "192.168.1.10", 25599)]
    [InlineData("  host:1  ", "host", 1)]
    [InlineData("[::1]:25566", "[::1]", 25566)]
    [InlineData("[::1]", "[::1]", ServerAddress.DefaultPort)]
    [InlineData("fe80::1", "fe80::1", ServerAddress.DefaultPort)]
    public void ParsesHostsAndOptionalPorts(string text, string host, int port)
    {
        Assert.True(ServerAddress.TryParse(text, out var address));
        Assert.Equal(host, address.Host);
        Assert.Equal(port, address.Port);
        Assert.True(address.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("host:notaport")]
    [InlineData(":25566")]
    [InlineData("[]")]
    [InlineData("[]:25566")]
    public void RejectsEmptyHostsAndBadPorts(string? text)
    {
        Assert.False(ServerAddress.TryParse(text, out var address));
        Assert.False(address.IsValid);
    }

    [Fact]
    public void FormattingRoundTripsAndHidesTheDefaultPort()
    {
        Assert.Equal("host", new ServerAddress("host", ServerAddress.DefaultPort).ToString());
        Assert.Equal("host:1234", new ServerAddress("host", 1234).ToString());

        Assert.True(ServerAddress.TryParse("host:1234", out var parsed));
        Assert.Equal("host:1234", parsed.ToString());
    }
}
