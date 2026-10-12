using VoxelEngine.Saves;
using Xunit;

namespace VoxelEngine.Tests.Saves;

public sealed class WorldSeedParserTests
{
    private static int NoRandom() => throw new Xunit.Sdk.XunitException("random must not be called");

    [Fact]
    public void NumericTextIsUsedVerbatim()
    {
        Assert.Equal(-42, WorldSeedParser.Parse("-42", NoRandom));
        Assert.Equal(7, WorldSeedParser.Parse(" 7 ", NoRandom));
    }

    [Fact]
    public void EmptyUsesTheRandomProvider()
    {
        Assert.Equal(123, WorldSeedParser.Parse("", () => 123));
        Assert.Equal(9, WorldSeedParser.Parse("  ", () => 9));
    }

    [Fact]
    public void TextSeedIsStableAndCaseSensitive()
    {
        var calls = 0;
        int Random()
        {
            calls++;
            return 0;
        }

        var first = WorldSeedParser.Parse("pumpkin", Random);
        var second = WorldSeedParser.Parse("pumpkin", Random);
        var upper = WorldSeedParser.Parse("Pumpkin", Random);

        Assert.Equal(first, second);
        Assert.NotEqual(first, upper);
        Assert.Equal(0, calls);
    }
}
