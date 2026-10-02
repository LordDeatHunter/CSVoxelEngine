using System;

namespace silkgltriangle;

/// <summary>
/// Namespaced content names for save files. Byte ids are handed out at startup in file-name
/// order (blocks, then items, sharing one id space), so adding, renaming or removing a single
/// JSON file renumbers everything after it; a save that stored raw ids would silently turn into
/// different blocks and items on the next run. Player and entity saves therefore reference
/// content by name and resolve it back to this run's id on load.
///
/// <para>
/// Every name is written with a <see cref="BlockPrefix"/> or <see cref="ItemPrefix"/>, and
/// <see cref="TryResolve"/> refuses a bare one: blocks and items may share a file name, and the prefix is what keeps such a
/// pair unambiguous. Molten materials live in their own 1-based id space and are named by
/// their file name without a prefix.
/// </para>
///
/// <para>
/// Both directions are GL-free, read-only registry lookups, valid once
/// <see cref="ContentRegistry.Load"/> has run, so any thread may call them.
/// </para>
/// </summary>
public static class SaveNames
{
    /// <summary>Prefix of a block name: <c>block/&lt;BlockType.Name&gt;</c>.</summary>
    public const string BlockPrefix = "block/";

    /// <summary>Prefix of a pure item name: <c>item/&lt;Item.SourceName&gt;</c>.</summary>
    public const string ItemPrefix = "item/";

    /// <summary>
    /// The save name of a block or item id, or null for air and for an id nothing is registered
    /// under. A synthetic item with no JSON behind it has no stable name either and also yields
    /// null; callers drop such an entry with a warning.
    /// </summary>
    public static string? NameOf(byte id)
    {
        if (id == 0) return null;
        if (BlockType.ById[id] is { } block) return BlockPrefix + block.Name;
        if (ItemRegistry.TryGet(id) is { } item && item.SourceName.Length > 0)
            return ItemPrefix + item.SourceName;
        return null;
    }

    /// <summary>
    /// This run's id for a save name written by <see cref="NameOf"/>. False for null, empty,
    /// unprefixed and unknown names — save files are always written prefixed, and refusing a
    /// bare name is what keeps a block and an item sharing a file name apart.
    /// </summary>
    public static bool TryResolve(string? name, out byte id)
    {
        id = 0;
        if (string.IsNullOrEmpty(name)) return false;
        if (name.StartsWith(BlockPrefix, StringComparison.Ordinal))
            return ContentRegistry.TryResolveBlock(name[BlockPrefix.Length..], out id);
        if (name.StartsWith(ItemPrefix, StringComparison.Ordinal))
            return ContentRegistry.TryResolveItem(name[ItemPrefix.Length..], out id);
        return false;
    }

    /// <summary>The file name of a molten material, or null for 0 ("no material") and
    /// anything out of range.</summary>
    public static string? MaterialNameOf(byte materialId) =>
        ContentRegistry.MoltenMaterialById(materialId)?.Name;

    /// <summary>This run's 1-based id of the molten material with that file name; false for
    /// null, empty and unknown names.</summary>
    public static bool TryResolveMaterial(string? name, out byte id)
    {
        id = 0;
        if (string.IsNullOrEmpty(name)) return false;
        if (ContentRegistry.MoltenMaterialByName(name) is not { } material) return false;
        id = material.Id;
        return true;
    }
}
