using System;
using System.IO;
using System.Text.Json.Nodes;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// A throwaway asset root in the temp folder: a copy of the real data, models and textures plus
/// <see cref="Count"/> extra blocks, each a copy of stone, so a registry loaded from it holds more
/// ids than a byte can name. The extra files are named to sort after every real block, so the real
/// blocks keep their ids and the synthetic ones take the ids right after them. One of them
/// (<see cref="EmitterIndex"/>) also emits light. The sfx folder holds empty files under the real
/// <c>.wav</c> names, because the sfx scan only lists names. Disposing deletes the folder.
/// </summary>
internal sealed class SyntheticContentRoot : IDisposable
{
    private const string NamePrefix = "zzz_synthetic_";

    /// <summary>The light level the emitting synthetic block declares.</summary>
    internal const byte EmitterLevel = 12;

    internal SyntheticContentRoot(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        Count = count;
        Root = Path.Combine(Path.GetTempPath(), $"voxel-wide-ids-{Guid.NewGuid():N}");

        var real = TestContent.FindAssetsRoot();
        foreach (var folder in new[] { "data", "models", "textures" })
            CopyTree(Path.Combine(real, folder), Path.Combine(Root, folder));

        var sfx = Directory.CreateDirectory(Path.Combine(Root, "sfx")).FullName;
        foreach (var wav in Directory.GetFiles(Path.Combine(real, "sfx"), "*.wav", SearchOption.TopDirectoryOnly))
            File.Create(Path.Combine(sfx, Path.GetFileName(wav))).Dispose();

        var stone = File.ReadAllText(Path.Combine(real, "data", "blocks", "stone.json"));
        var blocks = Path.Combine(Root, "data", "blocks");
        for (var i = 0; i < count; i++)
        {
            var block = JsonNode.Parse(stone)!.AsObject();
            block["name"] = DisplayNameOf(i);
            if (i == EmitterIndex)
                block["light"] = new JsonObject { ["emission"] = EmitterLevel };
            File.WriteAllText(Path.Combine(blocks, NameOf(i) + ".json"), block.ToJsonString());
        }
    }

    /// <summary>The asset root to load content from.</summary>
    internal string Root { get; }

    /// <summary>How many synthetic blocks the root adds.</summary>
    internal int Count { get; }

    /// <summary>The synthetic block that emits <see cref="EmitterLevel"/>; every other one is plain stone.</summary>
    internal int EmitterIndex => Count - 1;

    /// <summary>The registry name (file name) of synthetic block <paramref name="index"/>.</summary>
    internal static string NameOf(int index) => $"{NamePrefix}{index:D3}";

    /// <summary>The display name synthetic block <paramref name="index"/> declares.</summary>
    internal static string DisplayNameOf(int index) => $"Synthetic {index}";

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder must not fail a test; the OS cleans the temp folder up
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }
}
