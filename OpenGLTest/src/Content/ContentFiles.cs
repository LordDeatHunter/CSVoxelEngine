using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VoxelEngine.Content;

/// <summary>What every content loader shares: the one <see cref="JsonSerializerOptions"/> (also handed to <see cref="VoxelEngine.Content.Models.ModelLoader"/>), the sorted JSON file listing of a content folder, JSON reading that names the file on failure, and the block/item name-to-id resolution.</summary>
internal static class ContentFiles
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    internal static bool TryResolve(string name, out ushort id)
    {
        if (name.StartsWith("block/", StringComparison.Ordinal))
            return ContentRegistry.BlockIds.TryGetValue(name["block/".Length..], out id);
        if (name.StartsWith("item/", StringComparison.Ordinal))
            return ContentRegistry.ItemIds.TryGetValue(name["item/".Length..], out id);

        // Bare names try blocks first, so a name both a block and an item own resolves to the
        // block; an item/ prefix reaches the item
        return ContentRegistry.BlockIds.TryGetValue(name, out id) || ContentRegistry.ItemIds.TryGetValue(name, out id);
    }

    internal static List<(string Name, string Path)> JsonFilesIn(string folder)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Content folder '{folder}' does not exist.");

        return
        [
            .. Directory.GetFiles(folder, "*.json")
                .Select(path => (Name: Path.GetFileNameWithoutExtension(path), Path: path))
                .OrderBy(file => file.Name, StringComparer.Ordinal)
        ];
    }

    internal static T ReadJson<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidOperationException($"'{path}' is empty.");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"'{path}' is not valid JSON: {e.Message}", e);
        }
    }
}
