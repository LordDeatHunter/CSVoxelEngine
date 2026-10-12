using System.Collections.Generic;
using VoxelEngine.Items;
using VoxelEngine.Saves;
using Xunit;

namespace VoxelEngine.Tests.Saves;

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
        // Blocks and items may share a file name; the prefix keeps every name apart, so no
        // block name and no item name are ever equal, and each resolves to its own id
        var names = new Dictionary<string, byte>();
        foreach (var block in BlockType.Types.Values)
        {
            var name = SaveNames.NameOf(block.Id);
            Assert.NotNull(name);
            Assert.StartsWith(SaveNames.BlockPrefix, name);
            Assert.True(names.TryAdd(name, block.Id), $"'{name}' names two ids");
        }

        var sawItem = false;
        foreach (var id in ItemRegistry.ById.Keys)
        {
            if (!ItemRegistry.IsItem(id)) continue;
            var name = SaveNames.NameOf(id);
            Assert.NotNull(name);
            Assert.StartsWith(SaveNames.ItemPrefix, name);
            Assert.True(names.TryAdd(name, id), $"'{name}' names two ids");
            sawItem = true;
        }

        Assert.True(sawItem, "no pure item was named");
        foreach (var (name, id) in names)
        {
            Assert.True(SaveNames.TryResolve(name, out var back), $"'{name}' did not resolve");
            Assert.Equal(id, back);
        }
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
