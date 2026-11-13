using System;
using System.Collections.Generic;
using VoxelEngine.Content;

namespace VoxelEngine.Commands.Arguments;

/// <summary>
/// A block or item by name (a <see cref="ContentItem"/>): a plain name resolves to the block first, like a loot entry,
/// and <c>block/</c> or <c>item/</c> pins which one. Completes the plain names, and the pinned forms once one of the
/// two prefixes is typed.
/// </summary>
public sealed class ItemArgument : IArgumentType
{
    private const string BlockPrefix = "block/";
    private const string ItemPrefix = "item/";

    public bool TryParse(ReadOnlySpan<string> tokens, CommandSource source, out object value, out string error)
    {
        if (TryResolve(tokens[0], out var id))
        {
            value = new ContentItem(id, tokens[0]);
            error = "";
            return true;
        }
        value = default(ContentItem);
        error = $"Unknown item '{tokens[0]}'";
        return false;
    }

    public void Suggest(CommandSource source, int tokenIndex, string typed, List<Suggestion> into)
    {
        if (typed.StartsWith(BlockPrefix, StringComparison.OrdinalIgnoreCase))
        {
            AddAll(ContentRegistry.BlockIds.Keys, BlockPrefix, into);
            return;
        }
        if (typed.StartsWith(ItemPrefix, StringComparison.OrdinalIgnoreCase))
        {
            AddAll(ContentRegistry.ItemIds.Keys, ItemPrefix, into);
            return;
        }
        AddAll(ContentRegistry.BlockIds.Keys, "", into);
        AddAll(ContentRegistry.ItemIds.Keys, "", into);
    }

    /// <summary>A block or item by plain name (blocks first) or by its <c>block/</c> or <c>item/</c> pinned name.</summary>
    public static bool TryResolve(string name, out ushort id)
    {
        if (name.StartsWith(BlockPrefix, StringComparison.OrdinalIgnoreCase))
            return ContentRegistry.TryResolveBlock(name[BlockPrefix.Length..], out id);
        if (name.StartsWith(ItemPrefix, StringComparison.OrdinalIgnoreCase))
            return ContentRegistry.TryResolveItem(name[ItemPrefix.Length..], out id);
        return ContentRegistry.TryResolveBlock(name, out id) || ContentRegistry.TryResolveItem(name, out id);
    }

    private static void AddAll(IEnumerable<string> names, string prefix, List<Suggestion> into)
    {
        foreach (var name in names)
            into.Add(new Suggestion(prefix + name));
    }
}
