using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class WorldNamesTests
{
    [Fact]
    public void FreeNameIsReturnedUnchangedAndTakenNameNeverCollides()
    {
        Assert.Equal("Alpha", WorldNames.Unique("Alpha", ["Beta"]));

        string[] existing = ["Alpha"];
        var result = WorldNames.Unique("alpha", existing);
        Assert.DoesNotContain(existing, e => string.Equals(e, result, StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("alpha", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedCollisionsNeverRepeatAName()
    {
        string[] initial = ["Home"];
        var taken = new List<string>(initial);
        var results = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var name = WorldNames.Unique("Home", taken);
            results.Add(name);
            taken.Add(name);
        }

        Assert.Equal(results.Count, results.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(results, r => initial.Contains(r, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void GapsAreFilledBeforeGoingHigher()
    {
        string[] existing = ["X", "X 2", "X 4"];
        var result = WorldNames.Unique("X", existing);

        Assert.DoesNotContain(existing, e => string.Equals(e, result, StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("X ", result, StringComparison.Ordinal);
        var n = int.Parse(result["X ".Length..]);
        var largestTaken = existing.Skip(1).Max(e => int.Parse(e["X ".Length..]));
        Assert.True(n < largestTaken, $"'{result}' skipped a free gap below {largestTaken}");
    }

    [Fact]
    public void EmptyOrWhitespaceFallsBackToTheDefault()
    {
        Assert.Equal(WorldNames.DefaultName, WorldNames.Unique("   ", []));

        string[] existing = [WorldNames.DefaultName];
        var result = WorldNames.Unique(null, existing);
        Assert.NotEqual(WorldNames.DefaultName, result);
        Assert.DoesNotContain(existing, e => string.Equals(e, result, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SlugIsAlwaysAValidSingleFolderName()
    {
        string[] inputs = ["../..\\evil:name?", "", "   ", "Ünïcode World", new string('a', 200)];
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var input in inputs)
        {
            var slug = WorldNames.Slug(input);
            Assert.False(string.IsNullOrEmpty(slug), $"empty slug for '{input}'");
            Assert.True(slug.IndexOfAny(invalid) < 0, $"invalid file-name char in '{slug}'");
            Assert.DoesNotContain('/', slug);
            Assert.DoesNotContain('\\', slug);
            Assert.DoesNotContain("..", slug);
            Assert.True(slug.Length <= WorldNames.MaxSlugLength, $"'{slug}' is too long");

            var plain = slug;
            var unique = WorldNames.UniqueSlug(input, s => s == plain);
            Assert.NotEqual(plain, unique);
            Assert.True(unique.IndexOfAny(invalid) < 0);
        }
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("com1")]
    [InlineData("Lpt9")]
    public void AReservedDeviceNameNeverBecomesABareSlug(string name)
    {
        var slug = WorldNames.Slug(name);
        Assert.False(WorldNames.IsReservedDeviceName(slug), $"'{slug}' is a device name");
        Assert.True(slug.Length <= WorldNames.MaxSlugLength);
        Assert.StartsWith(name.ToLowerInvariant(), slug);
    }
}
