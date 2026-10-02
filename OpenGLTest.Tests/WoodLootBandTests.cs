using System;
using System.Collections.Generic;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="WoodLootBand.Select"/> is the thickness-to-loot rule of tree wood: the band with
/// the greatest minimum thickness not above the cell's thickness.
/// </summary>
public sealed class WoodLootBandTests
{
    // Ascending band sets starting at 0, as the registry validates them
    public static IEnumerable<object[]> BandSets()
    {
        yield return [new[] { 0 }];
        yield return [new[] { 0, 7 }];
        yield return [new[] { 0, 1, 2 }];
        yield return [new[] { 0, 4, 9, 15 }];
        yield return [new[] { 0, 14 }];
    }

    private static WoodLootBand[] Bands(int[] mins)
    {
        var bands = new WoodLootBand[mins.Length];
        for (var i = 0; i < mins.Length; i++)
            bands[i] = new WoodLootBand(mins[i], null, i % 2 == 0);
        return bands;
    }

    [Theory]
    [MemberData(nameof(BandSets))]
    public void SelectReturnsTheBandWithTheGreatestMinimumNotAboveTheThickness(int[] mins)
    {
        var bands = Bands(mins);
        for (var t = 0; t <= WoodState.MaxThickness; t++)
        {
            var chosen = WoodLootBand.Select(bands, t);
            Assert.Contains(chosen, bands);
            Assert.True(chosen.MinThickness <= t);
            // No band sits strictly between the chosen one and the thickness
            foreach (var band in bands)
                Assert.False(band.MinThickness > chosen.MinThickness && band.MinThickness <= t);
        }
    }

    [Theory]
    [MemberData(nameof(BandSets))]
    public void RaisingTheThicknessNeverSelectsAnEarlierBand(int[] mins)
    {
        var bands = Bands(mins);
        var previous = Array.IndexOf(bands, WoodLootBand.Select(bands, 0));
        for (var t = 1; t <= WoodState.MaxThickness; t++)
        {
            var index = Array.IndexOf(bands, WoodLootBand.Select(bands, t));
            Assert.True(index >= previous);
            previous = index;
        }
    }

    [Theory]
    [MemberData(nameof(BandSets))]
    public void ThicknessZeroSelectsTheFirstBand(int[] mins)
    {
        var bands = Bands(mins);
        Assert.Equal(bands[0], WoodLootBand.Select(bands, 0));
    }
}
