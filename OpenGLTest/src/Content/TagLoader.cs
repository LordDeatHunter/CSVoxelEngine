using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Items;

namespace VoxelEngine.Content;

/// <summary>Loads the block and item tags under <c>data/tags</c> that tool gates and crafting share, warning about any tool tag a block gates on that no item provides.</summary>
internal static class TagLoader
{
    // Block and item tags: named sets from data/tags/<name>.json ({"blocks": [...],
    // "items": [...]}), resolved here so gameplay and crafting share them.
    internal static void LoadTags(string folder)
    {
        foreach (var (name, path) in ContentFiles.JsonFilesIn(folder))
        {
            var definition = ContentFiles.ReadJson<TagDefinition>(path);
            var members = new HashSet<byte>();
            foreach (var entry in definition.Blocks ?? [])
            {
                var id = ResolveTagMember(entry, path);
                if (!members.Add(id))
                    throw new InvalidOperationException($"Tag '{path}' lists '{entry}' twice.");
            }
            foreach (var entry in definition.Items ?? [])
            {
                var id = ResolveTagMember(entry, path);
                if (!members.Add(id))
                    throw new InvalidOperationException($"Tag '{path}' lists '{entry}' twice.");
            }
            ContentRegistry.Tags[name] = members;
        }

        // Tool tags are free-form strings, so a block may gate on one no item provides: a typo,
        // or content authored ahead of its tool. Warn and leave the gate as written (nothing can
        // mine the block until a matching item exists) rather than abort startup.
        foreach (var type in BlockType.Types.Values)
        {
            if (type.Tools == null) continue;
            foreach (var tag in type.Tools.Keys)
                if (!ContentRegistry.Tags.TryGetValue(tag, out var members) || !members.Any(ItemRegistry.IsItem))
                    Log.Warn($"Block '{type.Name}' gates on tool tag '{tag}', which no item provides.");
        }
    }

    private static byte ResolveTagMember(string name, string path)
    {
        var bare = name.StartsWith("block/", StringComparison.Ordinal) ? name["block/".Length..]
            : name.StartsWith("item/", StringComparison.Ordinal) ? name["item/".Length..]
            : name;
        if (name.StartsWith("block/", StringComparison.Ordinal))
        {
            if (ContentRegistry.BlockIds.TryGetValue(bare, out var blockId)) return blockId;
            throw new InvalidOperationException($"Tag '{path}' names '{name}', which is not a registered block.");
        }
        if (name.StartsWith("item/", StringComparison.Ordinal))
        {
            if (ContentRegistry.ItemIds.TryGetValue(bare, out var itemId)) return itemId;
            throw new InvalidOperationException($"Tag '{path}' names '{name}', which is not a registered item.");
        }
        if (ContentFiles.TryResolve(name, out var id)) return id;
        throw new InvalidOperationException($"Tag '{path}' names '{name}', which is not a registered block or item.");
    }
}
