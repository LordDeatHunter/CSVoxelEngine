using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.StatusEffects;

namespace VoxelEngine.Content.Spells;

/// <summary>
/// Loads <c>data/spell/&lt;kind&gt;/&lt;name&gt;.json</c> into <see cref="SpellPartRegistry"/>. A missing
/// spell folder or kind folder is no parts; an unknown kind folder or a file lying directly in the spell
/// folder is an error naming it, and every part error names its file. <see cref="ReadParts"/> and
/// <see cref="Compile"/> touch no static state, so tests call them directly instead of replacing the
/// published registry.
/// </summary>
internal static class SpellPartLoader
{
    private static readonly string KnownKinds =
        string.Join(", ", Enum.GetValues<SpellPartKind>().Select(SpellPart.FolderOf));

    internal static void LoadSpellParts(string folder) => SpellPartRegistry.Publish(ReadParts(folder));

    /// <summary>
    /// Every part under <paramref name="folder"/> by key; empty when the folder is missing.
    /// <paramref name="effectExists"/> is the status effect lookup an element's <c>effect</c> is checked
    /// against (see <see cref="Compile"/>).
    /// </summary>
    internal static Dictionary<string, SpellPart> ReadParts(string folder, Func<string, bool>? effectExists = null)
    {
        var parts = new Dictionary<string, SpellPart>(StringComparer.Ordinal);
        if (!Directory.Exists(folder)) return parts;

        var looseFiles = Directory.GetFiles(folder, "*.json");
        if (looseFiles.Length > 0)
            throw new InvalidOperationException(
                $"Spell part '{looseFiles[0]}' lies directly in '{folder}'; a part goes in a kind folder ({KnownKinds}).");

        foreach (var dir in Directory.GetDirectories(folder))
        {
            // Matched ordinally against the folder names, never Enum.TryParse, which accepts numbers
            if (!IsKindFolder(Path.GetFileName(dir)))
                throw new InvalidOperationException(
                    $"Spell part folder '{dir}' is not a spell part kind; expected one of {KnownKinds}.");
        }

        foreach (var kind in Enum.GetValues<SpellPartKind>())
        {
            var kindFolder = Path.Combine(folder, SpellPart.FolderOf(kind));
            if (!Directory.Exists(kindFolder)) continue;
            foreach (var (name, path) in ContentFiles.JsonFilesIn(kindFolder))
            {
                var part = Compile(kind, name, ContentFiles.ReadJson<SpellPartDefinition>(path), path, effectExists);
                parts[part.Key] = part;
            }
        }
        return parts;
    }

    /// <summary>
    /// Compiles one part, throwing an error that names <paramref name="path"/> on any bad field. An element's
    /// <c>effect</c> must be found by <paramref name="effectExists"/>, which defaults to
    /// <see cref="StatusEffectRegistry.Contains"/> (effects load before spell parts).
    /// </summary>
    internal static SpellPart Compile(SpellPartKind kind, string name, SpellPartDefinition definition, string path,
        Func<string, bool>? effectExists = null)
    {
        if (definition.CostShare is not { } costShare || !float.IsFinite(costShare) || costShare <= 0f || costShare > 1f)
            throw new InvalidOperationException(
                $"Spell part '{path}' has costShare {definition.CostShare}; expected a share of the starting mana pool in 0..1, exclusive of 0.");

        if (definition.Complexity is not { } complexity || !float.IsFinite(complexity) || complexity < 0f)
            throw new InvalidOperationException(
                $"Spell part '{path}' has complexity {definition.Complexity}; expected 0 or more.");

        if (definition.Tags is not { Length: > 0 } tagList)
            throw new InvalidOperationException(
                $"Spell part '{path}' has no tags; a part needs at least one tag a staff can allow.");

        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tagList)
        {
            if (string.IsNullOrWhiteSpace(tag))
                throw new InvalidOperationException($"Spell part '{path}' has a blank tag.");
            if (!tags.Add(tag))
                throw new InvalidOperationException($"Spell part '{path}' lists tag '{tag}' twice.");
        }

        var damageFactor = CheckFactor(kind, definition.DamageFactor, "damageFactor", path);
        var rangeFactor = CheckFactor(kind, definition.RangeFactor, "rangeFactor", path);
        var effect = CheckEffect(kind, definition.Effect, path, effectExists ?? StatusEffectRegistry.Contains);

        return new SpellPart(kind, name, definition.Name ?? name, costShare, complexity, tags, damageFactor, rangeFactor, effect);
    }

    /// <summary>An element's status effect: null when unset; set only on an element, naming a loaded effect.</summary>
    private static string? CheckEffect(SpellPartKind kind, string? effect, string path, Func<string, bool> effectExists)
    {
        if (effect == null) return null;
        if (kind != SpellPartKind.Element)
            throw new InvalidOperationException($"Spell part '{path}' sets effect, which only an element may carry.");
        if (string.IsNullOrWhiteSpace(effect) || !effectExists(effect))
            throw new InvalidOperationException(
                $"Spell part '{path}' names effect '{effect}', which is not a registered status effect.");
        return effect;
    }

    private static bool IsKindFolder(string folderName)
    {
        foreach (var kind in Enum.GetValues<SpellPartKind>())
        {
            if (string.Equals(folderName, SpellPart.FolderOf(kind), StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>A modifier factor: 1 when unset; set only on a modifier, finite and above 0.</summary>
    private static float CheckFactor(SpellPartKind kind, float? factor, string field, string path)
    {
        if (factor is not { } value) return 1f;
        if (kind != SpellPartKind.Modifier)
            throw new InvalidOperationException($"Spell part '{path}' sets {field}, which only a modifier may carry.");
        if (!float.IsFinite(value) || value <= 0f)
            throw new InvalidOperationException($"Spell part '{path}' has {field} {value}; expected more than 0.");
        return value;
    }
}
