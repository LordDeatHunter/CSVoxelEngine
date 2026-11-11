using System;
using System.Linq;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.Tests.Items;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The scroll schema: <c>teaches</c> and <c>use.action: "learn_parts"</c> come together, a key is listed
/// once, and every taught key must name a loaded spell part, each refusal naming the file.
/// </summary>
public sealed class ScrollSchemaTests
{
    public ScrollSchemaTests() => TestContent.EnsureLoaded();

    private const string Path = "assets/data/items/test_scroll.json";

    [Fact]
    public void TeachesNeedsLearnPartsAndViceVersa()
    {
        var key = SpellPartRegistry.All.First().Key;

        var stray = Assert.Throws<InvalidOperationException>(() =>
            ItemLoader.ParseTeaches(new ItemDefinition { Teaches = [key] }, ItemUseAction.Heal, Path));
        Assert.Contains(Path, stray.Message);

        foreach (var teaches in new[] { null, Array.Empty<string>() })
        {
            var missing = Assert.Throws<InvalidOperationException>(() =>
                ItemLoader.ParseTeaches(new ItemDefinition { Teaches = teaches }, ItemUseAction.LearnParts, Path));
            Assert.Contains(Path, missing.Message);
        }

        Assert.Null(ItemLoader.ParseTeaches(new ItemDefinition(), ItemUseAction.Heal, Path));
        Assert.Equal(new[] { key }, ItemLoader.ParseTeaches(new ItemDefinition { Teaches = [$" {key} "] },
            ItemUseAction.LearnParts, Path));
    }

    [Fact]
    public void ADuplicateTaughtKeyIsRefused()
    {
        var key = SpellPartRegistry.All.First().Key;

        foreach (var teaches in new[] { new[] { key, key }, new[] { key, " " } })
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                ItemLoader.ParseTeaches(new ItemDefinition { Teaches = teaches }, ItemUseAction.LearnParts, Path));
            Assert.Contains(Path, error.Message);
        }
    }

    [Fact]
    public void AnUnknownTaughtKeyIsRefusedNamingTheFile()
    {
        var known = SpellPartRegistry.All.First().Key;
        ItemLoader.ValidateLearnParts(LearnPartsItemTests.Scroll(known), Path);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ItemLoader.ValidateLearnParts(LearnPartsItemTests.Scroll(known, "element/no_such_part"), Path));
        Assert.Contains(Path, error.Message);
        Assert.Contains("element/no_such_part", error.Message);
    }

    [Fact]
    public void EveryShippedScrollTeachesOnlyRegisteredParts()
    {
        var scrolls = ItemRegistry.All.OfType<LearnPartsItem>().ToList();
        Assert.NotEmpty(scrolls);
        Assert.All(scrolls, scroll =>
        {
            Assert.NotEmpty(scroll.Teaches);
            Assert.All(scroll.Teaches, k => Assert.NotNull(SpellPartRegistry.TryGetByKey(k)));
        });
    }
}
