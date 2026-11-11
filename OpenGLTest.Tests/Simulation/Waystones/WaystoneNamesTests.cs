using System;
using System.Collections.Generic;
using VoxelEngine.Simulation.Waystones;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Waystones;

/// <summary>Waystone names: a generated one is consonant-vowel pairs with only its first letter capitalized, drawn
/// wholly from the injected random source; a rename keeps only printable text within the length cap and a blank one is
/// refused.</summary>
public sealed class WaystoneNamesTests
{
    [Fact]
    public void AGeneratedNameIsCapitalizedConsonantVowelPairs()
    {
        var rng = new Random(11);
        for (var i = 0; i < 500; i++)
        {
            var name = WaystoneNames.Generate(rng);
            Assert.Equal(WaystoneNames.Pairs * 2, name.Length);
            Assert.True(char.IsUpper(name[0]));
            for (var c = 0; c < name.Length; c++)
            {
                var letter = char.ToLowerInvariant(name[c]);
                var set = c % 2 == 0 ? WaystoneNames.Consonants : WaystoneNames.Vowels;
                Assert.Contains(letter, set);
                if (c > 0) Assert.True(char.IsLower(name[c]));
            }
        }
    }

    [Fact]
    public void TheNameFollowsOnlyTheInjectedRandomSource()
    {
        Assert.Equal(WaystoneNames.Generate(new Random(42)), WaystoneNames.Generate(new Random(42)));

        var seen = new HashSet<string>();
        var rng = new Random(3);
        for (var i = 0; i < 200; i++) seen.Add(WaystoneNames.Generate(rng));
        Assert.True(seen.Count > 190);
    }

    [Fact]
    public void ARenameKeepsPrintableTextWithinTheCapAndABlankOneIsRefused()
    {
        Assert.True(WaystoneNames.TrySanitize("  Home\n  Base\t ", out var clean));
        Assert.Equal("Home Base", clean);

        Assert.True(WaystoneNames.TrySanitize(new string('x', WaystoneNames.MaxLength * 3), out var cut));
        Assert.Equal(WaystoneNames.MaxLength, cut.Length);

        Assert.False(WaystoneNames.TrySanitize("   \r\n\u0007", out _));
        Assert.False(WaystoneNames.TrySanitize("é中", out _));
        Assert.False(WaystoneNames.TrySanitize(null, out _));
    }
}
