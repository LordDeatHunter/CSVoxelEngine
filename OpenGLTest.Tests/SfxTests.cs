using System;
using System.IO;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class SfxTests
{
    public SfxTests() => TestContent.EnsureLoaded();

    [Theory]
    [InlineData("hit_wood_1", "hit_wood")]
    [InlineData("hit_wood_3", "hit_wood")]
    [InlineData("hit_rock_2", "hit_rock")]
    [InlineData("item_pickup_1", "item_pickup")]
    [InlineData("hit_default", "hit_default")]
    [InlineData("something", "something")]
    public void GroupStripsOnlyATrailingVariantIndex(string file, string group) =>
        Assert.Equal(group, SfxLibrary.GroupOf(file));

    [Fact]
    public void ScanFindsEveryGroupInTheAssetsFolder()
    {
        var groups = SfxLibrary.Scan(Path.Combine(TestContent.FindAssetsRoot(), "sfx"));

        Assert.Equal(3, groups[SfxLibrary.WoodGroup].Count);
        Assert.Equal(2, groups[SfxLibrary.StoneGroup].Count);
        Assert.Equal(3, groups[SfxLibrary.PickupGroup].Count);
        Assert.Single(groups[SfxLibrary.DefaultGroup]);
    }

    [Fact]
    public void ConfiguredAmbiencePathsExist()
    {
        var root = TestContent.FindAssetsRoot();
        Assert.True(File.Exists(Path.Combine(root, WorldGenerationConfig.DayAmbiencePath["assets/".Length..])));
        Assert.True(File.Exists(Path.Combine(root, WorldGenerationConfig.NightAmbiencePath["assets/".Length..])));
    }

    [Fact]
    public void EveryBlockResolvesToALoadedGroup()
    {
        var groups = SfxLibrary.Scan(Path.Combine(TestContent.FindAssetsRoot(), "sfx"));
        Assert.All(BlockType.Types.Values, type => Assert.Contains(type.Sfx, groups.Keys));
    }

    [Fact]
    public void StoneAndWoodFamiliesMapToTheirGroups()
    {
        foreach (var name in new[] { "stone", "cobblestone", "mud_bricks", "malachite_ore", "gravel", "pebble", "quern" })
            Assert.Equal(SfxLibrary.StoneGroup, ContentRegistry.RequireBlock(name).Sfx);
        foreach (var name in new[] { "log", "branch", "limb", "sprig", "switch", "twig", "branch_pile" })
            Assert.Equal(SfxLibrary.WoodGroup, ContentRegistry.RequireBlock(name).Sfx);
        foreach (var name in new[] { "dirt", "grass", "sand" })
            Assert.Equal(SfxLibrary.DefaultGroup, ContentRegistry.RequireBlock(name).Sfx);
    }

    [Fact]
    public void UnknownSfxNameFallsBackToTheDefault()
    {
        Assert.Equal(SfxLibrary.DefaultGroup, ContentRegistry.ResolveSfx("test.json", null));
        Assert.Equal(SfxLibrary.DefaultGroup, ContentRegistry.ResolveSfx("test.json", "  "));
        Assert.Equal(SfxLibrary.DefaultGroup, ContentRegistry.ResolveSfx("test.json", "not_a_group"));
        Assert.Equal(SfxLibrary.StoneGroup, ContentRegistry.ResolveSfx("test.json", SfxLibrary.StoneGroup));
    }

    [Fact]
    public void PickIndexIsUniformAndAllowsRepeats()
    {
        var random = new Random(1234);
        var counts = new int[2];
        var repeats = 0;
        var previous = -1;
        for (var i = 0; i < 1000; i++)
        {
            var index = SfxManager.PickIndex(2, random);
            Assert.InRange(index, 0, 1);
            counts[index]++;
            if (index == previous) repeats++;
            previous = index;
        }

        // A picker that excluded the previous index would force 1-2-1-2 with zero repeats.
        Assert.True(counts[0] > 400 && counts[1] > 400, $"counts {counts[0]}/{counts[1]}");
        Assert.True(repeats > 100, $"repeats {repeats}");
    }

    [Fact]
    public void PickIndexHandlesEmptyAndSingleGroups()
    {
        var random = new Random(7);
        Assert.Equal(0, SfxManager.PickIndex(1, random));
        Assert.Equal(0, SfxManager.PickIndex(0, random));
    }

    [Fact]
    public void MixerMixesAClipThenFallsSilent()
    {
        using var mixer = new SfxMixer(1f);
        mixer.Play([1000, 1000, 1000, 1000]);

        var buffer = new byte[16];
        Assert.Equal(16, mixer.Read(buffer, 0, buffer.Length));
        Assert.Contains(buffer, b => b != 0);
        Assert.Equal(1000, BitConverter.ToInt16(buffer, 0));

        var tail = new byte[16];
        mixer.Read(tail, 0, tail.Length);
        Assert.All(tail, b => Assert.Equal(0, b));
    }

    [Fact]
    public void MixerAppliesAndClampsVolume()
    {
        using var mixer = new SfxMixer(0.5f);
        mixer.Play([1000, 1000]);

        var half = new byte[4];
        mixer.Read(half, 0, half.Length);
        Assert.Equal(500, BitConverter.ToInt16(half, 0));

        using var loud = new SfxMixer(1f);
        loud.Play([short.MaxValue, short.MaxValue]);
        loud.Play([short.MaxValue, short.MaxValue]);
        var clamped = new byte[4];
        loud.Read(clamped, 0, clamped.Length);
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(clamped, 0));
    }
}
