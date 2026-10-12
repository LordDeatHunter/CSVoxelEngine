using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using VoxelEngine.Content.Definitions;

namespace VoxelEngine.Content.Models;

/// <summary>
/// Reads Minecraft-format model files under <c>assets/models/</c> and flattens their parent
/// chains into <see cref="BlockModel"/>s. Both the raw files and the flattened results are
/// cached by path, so the four built-in parents are parsed once no matter how many blocks
/// reference them. Startup-only: nothing here is thread-safe, and nothing runs after
/// <see cref="ContentRegistry.Load"/> returns.
/// </summary>
public sealed class ModelLoader(string modelsRoot, JsonSerializerOptions jsonOptions)
{
    // A parent chain deeper than this is a mistake even if it is not an outright cycle
    private const int MaxParentDepth = 32;

    private readonly Dictionary<string, ModelDefinition> _raw = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlockModel> _resolved = new(StringComparer.Ordinal);

    /// <summary>Flattens the model at <paramref name="path"/> (e.g. <c>"block/cube_all"</c>).</summary>
    public BlockModel Resolve(string path)
    {
        var key = Normalize(path);
        if (_resolved.TryGetValue(key, out var cached)) return cached;

        var model = Flatten(Read(key), FilePathOf(key));
        _resolved[key] = model;
        return model;
    }

    /// <summary>Flattens a model written inline in a block definition; <paramref name="source"/>
    /// names the owning file so errors point at it.</summary>
    public BlockModel ResolveInline(ModelDefinition definition, string source) =>
        Flatten(definition, source);

    private BlockModel Flatten(ModelDefinition leaf, string source)
    {
        // Child-over-parent: walk up collecting, then apply in reverse so the leaf wins
        var chain = new List<ModelDefinition> { leaf };
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var parent = leaf.Parent;
        while (parent != null)
        {
            var key = Normalize(parent);
            if (!visited.Add(key))
                throw new InvalidOperationException(
                    $"Model '{source}' has a cyclic parent chain: '{key}' is visited twice.");
            if (chain.Count > MaxParentDepth)
                throw new InvalidOperationException(
                    $"Model '{source}' has a parent chain deeper than {MaxParentDepth} models.");

            var next = Read(key);
            chain.Add(next);
            parent = next.Parent;
        }

        var textures = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyList<ModelDefinition.ElementDefinition>? elements = null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var model = chain[i];
            if (model.Textures != null)
                foreach (var (name, value) in model.Textures)
                    textures[name] = value;
            // Minecraft lets any level replace the element list wholesale
            if (model.Elements is { Count: > 0 }) elements = model.Elements;
        }

        if (elements == null)
            throw new InvalidOperationException($"Model '{source}' declares no elements.");

        return new BlockModel(source, textures, elements);
    }

    private ModelDefinition Read(string key)
    {
        if (_raw.TryGetValue(key, out var cached)) return cached;

        var path = FilePathOf(key);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Model '{key}' not found at '{path}'.", path);

        ModelDefinition? model;
        try
        {
            model = JsonSerializer.Deserialize<ModelDefinition>(File.ReadAllText(path), jsonOptions);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"Model '{path}' is not valid JSON: {e.Message}", e);
        }

        if (model == null)
            throw new InvalidOperationException($"Model '{path}' is empty.");

        _raw[key] = model;
        return model;
    }

    private string FilePathOf(string key) => Path.Combine(modelsRoot, key.Replace('/', Path.DirectorySeparatorChar) + ".json");

    // Model references may carry a namespace and a leading slash; the engine has one namespace
    private static string Normalize(string path)
    {
        var name = path;
        var colon = name.IndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..];
        return name.TrimStart('/');
    }
}
