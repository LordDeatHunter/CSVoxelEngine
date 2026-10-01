using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// Name -> <see cref="ITreeFeature"/> table a species' feature <c>type</c> is resolved through.
/// Features are code, not content: the table is built once by the static initialiser and never
/// changes afterwards, so it needs no Publish/Reset and is safe to read from the generation
/// worker threads without locking.
/// </summary>
public static class TreeFeatureRegistry
{
    private static readonly Dictionary<string, ITreeFeature> ByNameTable = new(StringComparer.Ordinal)
    {
        ["roots"] = new PlaceholderTreeFeature("roots"),
        ["buttress"] = new PlaceholderTreeFeature("buttress"),
    };

    /// <summary>Every registered feature by name.</summary>
    public static IReadOnlyDictionary<string, ITreeFeature> ByName => ByNameTable;

    /// <summary>The registered names, comma-separated, for error messages.</summary>
    public static string NamesForError => string.Join(", ", ByNameTable.Keys);

    /// <summary>The feature registered under <paramref name="name"/>, or null.</summary>
    public static ITreeFeature? TryGet(string name) =>
        ByNameTable.TryGetValue(name, out ITreeFeature? feature) ? feature : null;

    /// <summary>The feature registered under <paramref name="name"/>; throws naming it otherwise.</summary>
    public static ITreeFeature Require(string name) =>
        TryGet(name) ?? throw new InvalidOperationException(
            $"No tree feature named '{name}' is registered (known: {NamesForError}).");
}
