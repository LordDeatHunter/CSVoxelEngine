using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// Name -> <see cref="IGrowthLogic"/> table a species' <c>growth.logic</c> is resolved through.
/// Growth logics are code, not content: the table is built once by the static initializer and
/// never changes afterwards, so it needs no Publish/Reset and is safe to read from the
/// generation worker threads without locking.
/// </summary>
public static class GrowthLogicRegistry
{
    private static readonly Dictionary<string, IGrowthLogic> ByNameTable = new(StringComparer.Ordinal)
    {
        ["oak"] = new OakGrowthLogic(),
        ["conifer"] = new ConiferGrowthLogic(),
        ["cactus"] = new CactusGrowthLogic(),
        ["umbrella"] = new UmbrellaGrowthLogic(),
        ["baobab"] = new BaobabGrowthLogic(),
    };

    /// <summary>Every registered growth logic by name.</summary>
    public static IReadOnlyDictionary<string, IGrowthLogic> ByName => ByNameTable;

    /// <summary>The registered names, comma-separated, for error messages.</summary>
    public static string NamesForError => string.Join(", ", ByNameTable.Keys);

    /// <summary>The logic registered under <paramref name="name"/>, or null.</summary>
    public static IGrowthLogic? TryGet(string name) =>
        ByNameTable.TryGetValue(name, out IGrowthLogic? logic) ? logic : null;

    /// <summary>The logic registered under <paramref name="name"/>; throws naming it otherwise.</summary>
    public static IGrowthLogic Require(string name) =>
        TryGet(name) ?? throw new InvalidOperationException(
            $"No growth logic named '{name}' is registered (known: {NamesForError}).");
}
