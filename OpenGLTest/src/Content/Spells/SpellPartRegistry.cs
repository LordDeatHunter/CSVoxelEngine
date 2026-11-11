using System;
using System.Collections.Generic;

namespace VoxelEngine.Content.Spells;

/// <summary>
/// Static registry of every <see cref="SpellPart"/>, published once by <see cref="ContentRegistry.Load"/>
/// before the <see cref="World"/> (and any worker thread) exists, then read-only. A part is keyed by
/// <see cref="SpellPart.Key"/>, <c>&lt;kind&gt;/&lt;name&gt;</c>.
/// </summary>
public static class SpellPartRegistry
{
    private static Dictionary<string, SpellPart> _byKey = new(StringComparer.Ordinal);

    /// <summary>Every registered part.</summary>
    public static IEnumerable<SpellPart> All => _byKey.Values;

    /// <summary>The part of that kind registered under that file name, or null.</summary>
    public static SpellPart? TryGet(SpellPartKind kind, string name) =>
        _byKey.GetValueOrDefault(SpellPart.KeyOf(kind, name));

    /// <summary>
    /// The part saved under its full key <c>&lt;kind&gt;/&lt;name&gt;</c> (the form save files and staffs
    /// store), or null for an unknown or null key.
    /// </summary>
    public static SpellPart? TryGetByKey(string? key) => key == null ? null : _byKey.GetValueOrDefault(key);

    /// <summary>The part of that kind registered under that file name; throws naming the missing content.</summary>
    public static SpellPart Require(SpellPartKind kind, string name)
    {
        if (_byKey.TryGetValue(SpellPart.KeyOf(kind, name), out var part)) return part;
        throw new InvalidOperationException($"No spell part '{SpellPart.KeyOf(kind, name)}' is registered.");
    }

    internal static void Publish(Dictionary<string, SpellPart> parts) => _byKey = parts;

    internal static void Reset() => _byKey = new Dictionary<string, SpellPart>(StringComparer.Ordinal);
}
