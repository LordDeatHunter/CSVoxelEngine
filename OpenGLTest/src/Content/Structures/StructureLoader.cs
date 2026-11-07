using System;
using VoxelEngine.WorldGen;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using VoxelEngine.Content.Definitions;

namespace VoxelEngine.Content.Structures;

/// <summary>
/// Loads <c>data/structures/&lt;name&gt;.json</c> into <see cref="StructureRegistry"/>, after the blocks. A missing folder is
/// no templates; every error names the file.
/// </summary>
internal static class StructureLoader
{
    internal static void LoadStructures(string folder)
    {
        var templates = new List<StructureTemplate>();
        if (Directory.Exists(folder))
            foreach (var (name, path) in ContentFiles.JsonFilesIn(folder))
                templates.Add(Compile(name, ContentFiles.ReadJson<StructureDefinition>(path), path));
        StructureRegistry.Publish(templates);
    }

    /// <summary>Compiles one template, throwing an error that names <paramref name="path"/> on any bad field.</summary>
    internal static StructureTemplate Compile(string name, StructureDefinition d, string path)
    {
        string Fail(string why) => $"Structure '{path}' {why}";

        if (!Enum.TryParse<StructureKind>(d.Kind, true, out var kind))
            throw new InvalidOperationException(Fail($"has kind '{d.Kind}'; expected surface, underground, hell or sky."));
        var weight = d.Weight ?? 1f;
        if (!float.IsFinite(weight) || weight <= 0f) throw new InvalidOperationException(Fail("needs a weight above 0."));
        if (d.Layers is not { Length: > 0 } layers) throw new InvalidOperationException(Fail("has no layers."));
        if (layers.Length > StructureRegistry.MaxHeight)
            throw new InvalidOperationException(Fail($"has {layers.Length} layers; the most is {StructureRegistry.MaxHeight}."));
        if (d.Palette == null) throw new InvalidOperationException(Fail("has no palette."));
        var groundLayer = d.GroundLayer ?? 0;
        if ((uint)groundLayer >= layers.Length) throw new InvalidOperationException(Fail("has a groundLayer outside its layers."));

        var palette = new Dictionary<char, (byte Block, byte State, bool Facing)>();
        foreach (var (key, value) in d.Palette)
        {
            if (key.Length != 1 || key[0] == ' ' || key[0] == '.')
                throw new InvalidOperationException(Fail($"has palette key '{key}'; expected one character other than space and a dot."));
            palette[key[0]] = ParseEntry(key, value, Fail);
        }

        if (layers[0] is not { Length: > 0 } firstRows)
            throw new InvalidOperationException(Fail("has an empty first layer."));
        var size = firstRows.Length;
        if (size > StructureRegistry.MaxSize)
            throw new InvalidOperationException(Fail($"is {size} rows deep; expected 1..{StructureRegistry.MaxSize}."));
        var cells = new int[layers.Length * size * size];
        var facing = new bool[cells.Length];
        for (var y = 0; y < layers.Length; y++)
        {
            if (layers[y] is not { } rows || rows.Length != size)
                throw new InvalidOperationException(Fail($"layer {y} does not have {size} rows (footprints are square)."));
            for (var z = 0; z < size; z++)
            {
                if (rows[z].Length != size)
                    throw new InvalidOperationException(Fail($"layer {y} row {z} is {rows[z].Length} wide; expected {size}."));
                for (var x = 0; x < size; x++)
                {
                    var index = (y * size + z) * size + x;
                    var ch = rows[z][x];
                    if (ch == ' ') cells[index] = -1;
                    else if (ch == '.') cells[index] = BlockType.Air;
                    else if (palette.TryGetValue(ch, out var entry))
                    {
                        cells[index] = entry.Block | (entry.State << 8);
                        facing[index] = entry.Facing;
                    }
                    else throw new InvalidOperationException(Fail($"uses '{ch}' in layer {y} row {z}, which is not in the palette."));
                }
            }
        }

        Biome[]? biomes = null;
        if (d.Biomes is { Length: > 0 } names)
        {
            if (kind != StructureKind.Surface) throw new InvalidOperationException(Fail("lists biomes but is not a surface template."));
            biomes = new Biome[names.Length];
            for (var i = 0; i < names.Length; i++)
                if (!Enum.TryParse(names[i], true, out biomes[i]))
                    throw new InvalidOperationException(Fail($"names unknown biome '{names[i]}'."));
        }

        return new StructureTemplate(name, kind, weight, size, layers.Length, groundLayer, biomes, cells, facing);
    }

    private static (byte Block, byte State, bool Facing) ParseEntry(string key, JsonElement value, Func<string, string> fail)
    {
        string blockName;
        string? facingName = null;
        if (value.ValueKind == JsonValueKind.String) blockName = value.GetString() ?? "";
        else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("block", out var b) && b.ValueKind == JsonValueKind.String)
        {
            blockName = b.GetString() ?? "";
            if (value.TryGetProperty("facing", out var f)) facingName = f.GetString();
        }
        else throw new InvalidOperationException(fail($"has palette entry '{key}' that is neither a block name nor a block and facing object."));

        if (blockName.StartsWith("block/", StringComparison.Ordinal)) blockName = blockName[6..];
        if (!ContentRegistry.TryResolveBlock(blockName, out var id))
            throw new InvalidOperationException(fail($"names block '{blockName}' (palette '{key}'), which does not exist."));
        if (facingName == null) return (id, 0, false);
        if (!BlockType.ById[id]!.HasHorizontalFacing)
            throw new InvalidOperationException(fail($"gives a facing to '{blockName}', which does not face."));
        var state = facingName.ToLowerInvariant() switch
        {
            "north" => 0,
            "south" => 1,
            "west" => 2,
            "east" => 3,
            _ => throw new InvalidOperationException(fail($"has facing '{facingName}'; expected north, south, west or east."))
        };
        return (id, (byte)state, true);
    }
}
