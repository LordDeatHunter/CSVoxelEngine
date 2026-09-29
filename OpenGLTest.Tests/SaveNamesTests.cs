using System.Collections.Generic;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The name mapping save files reference content through: every named id must come back as
/// itself, the block and item namespaces must stay apart, and anything this run does not know
/// must be refused rather than guessed.
/// </summary>
public sealed class SaveNamesTests
{
    public SaveNamesTests() => TestContent.EnsureLoaded();

    [Fact]
    public void EveryRegisteredIdRoundTripsThroughItsName()
    {
        var sawBlock = false;
        var sawItem = false;
        foreach (var id in ItemRegistry.ById.Keys)
        {
            var name = SaveNames.NameOf(id);
            if (name == null) continue;

            Assert.True(SaveNames.TryResolve(name, out var back), $"'{name}' did not resolve");
            Assert.Equal(id, back);
            sawBlock |= name.StartsWith(SaveNames.BlockPrefix, System.StringComparison.Ordinal);
            sawItem |= name.StartsWith(SaveNames.ItemPrefix, System.StringComparison.Ordinal);
        }

        Assert.True(sawBlock, "no block was named");
        Assert.True(sawItem, "no pure item was named");
    }

    [Fact]
    public void BlockAndItemNamespacesNeverCollide()
    {
        // A block and an item share the file name "branch"; the prefix keeps them apart.
        var blockId = ContentRegistry.RequireBlock("branch").Id;
        var itemId = ContentRegistry.RequireItem("branch").Id;
        Assert.NotEqual(blockId, itemId);

        var blockName = SaveNames.NameOf(blockId);
        var itemName = SaveNames.NameOf(itemId);
        Assert.NotNull(blockName);
        Assert.NotNull(itemName);
        Assert.NotEqual(blockName, itemName);

        Assert.True(SaveNames.TryResolve(blockName, out var blockBack));
        Assert.Equal(blockId, blockBack);
        Assert.True(SaveNames.TryResolve(itemName, out var itemBack));
        Assert.Equal(itemId, itemBack);
    }

    [Fact]
    public void UnknownAndBareNamesAreRefused()
    {
        var refused = new List<string?> { null, "", "stone", "block/no-such-block", "item/no-such-item" };
        foreach (var name in refused)
            Assert.False(SaveNames.TryResolve(name, out _), $"'{name}' should not resolve");

        Assert.Null(SaveNames.NameOf(0));
        Assert.False(SaveNames.TryResolveMaterial("no-such-material", out _));
        Assert.False(SaveNames.TryResolveMaterial(null, out _));

        foreach (var material in ContentRegistry.MoltenMaterials)
        {
            Assert.True(SaveNames.TryResolveMaterial(SaveNames.MaterialNameOf(material.Id), out var back));
            Assert.Equal(material.Id, back);
        }
    }
}
