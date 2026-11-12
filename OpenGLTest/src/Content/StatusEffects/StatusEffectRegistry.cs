using System;
using System.Collections.Generic;

namespace VoxelEngine.Content.StatusEffects;

/// <summary>
/// Static registry of every <see cref="StatusEffect"/>, published once by <see cref="ContentRegistry.Load"/>
/// before the <see cref="World"/> (and any worker thread) exists, then read-only. Effects are indexed by
/// their id (their position in file-name order) and by file name.
/// </summary>
public static class StatusEffectRegistry
{
    private static StatusEffect[] _byId = [];
    private static Dictionary<string, StatusEffect> _byName = new(StringComparer.Ordinal);

    /// <summary>Every registered effect, in id order.</summary>
    public static IReadOnlyList<StatusEffect> All => _byId;

    /// <summary>The OR of every registered effect's <see cref="StatusEffect.Bit"/>.</summary>
    public static byte KnownMask { get; private set; }

    /// <summary>The effect registered under that file name, if any.</summary>
    public static bool TryGet(string name, out StatusEffect effect) => _byName.TryGetValue(name, out effect!);

    /// <summary>Whether an effect is registered under that file name.</summary>
    public static bool Contains(string name) => _byName.ContainsKey(name);

    /// <summary>The effect registered under that file name; throws naming the missing effect.</summary>
    public static StatusEffect Require(string name)
    {
        if (_byName.TryGetValue(name, out var effect)) return effect;
        throw new InvalidOperationException($"No status effect '{name}' is registered.");
    }

    /// <summary>The effect with that id, or null when it is out of range.</summary>
    public static StatusEffect? ById(int id) => id >= 0 && id < _byId.Length ? _byId[id] : null;

    internal static void Publish(IReadOnlyList<StatusEffect> effects)
    {
        var byId = new StatusEffect[effects.Count];
        var byName = new Dictionary<string, StatusEffect>(StringComparer.Ordinal);
        byte mask = 0;
        foreach (var effect in effects)
        {
            byId[effect.Id] = effect;
            byName[effect.Name] = effect;
            mask |= effect.Bit;
        }
        _byId = byId;
        _byName = byName;
        KnownMask = mask;
    }

    internal static void Reset()
    {
        _byId = [];
        _byName = new Dictionary<string, StatusEffect>(StringComparer.Ordinal);
        KnownMask = 0;
    }
}
