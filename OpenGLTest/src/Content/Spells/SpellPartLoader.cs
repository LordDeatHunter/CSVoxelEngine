using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.WorldGen.Trees;

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

    /// <summary>The <c>impact</c> names an element may list, each with its flag.</summary>
    private static readonly (string Name, SpellImpact Flag)[] ImpactNames =
    [
        ("ignite", SpellImpact.Ignite),
        ("freeze", SpellImpact.Freeze),
        ("thaw", SpellImpact.Thaw),
        ("prime", SpellImpact.Prime),
        ("light", SpellImpact.Light),
        ("grow", SpellImpact.Grow),
    ];

    /// <summary>The <c>delivery</c> names a shape may set, each with its delivery.</summary>
    private static readonly (string Name, SpellDelivery Delivery)[] DeliveryNames =
    [
        ("projectile", SpellDelivery.Projectile),
        ("self", SpellDelivery.Self),
    ];

    /// <summary>
    /// Every part under <paramref name="folder"/> by key; empty when the folder is missing.
    /// <paramref name="effectExists"/> and <paramref name="speciesExists"/> are the status effect and tree
    /// species lookups an element's <c>effect</c> and <c>plant</c> are checked against (see <see cref="Compile"/>).
    /// </summary>
    internal static Dictionary<string, SpellPart> ReadParts(string folder, Func<string, bool>? effectExists = null,
        Func<string, bool>? speciesExists = null)
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
                var part = Compile(kind, name, ContentFiles.ReadJson<SpellPartDefinition>(path), path, effectExists,
                    speciesExists);
                parts[part.Key] = part;
            }
        }
        return parts;
    }

    /// <summary>
    /// Compiles one part, throwing an error that names <paramref name="path"/> on any bad field. An element's
    /// <c>effect</c> must be found by <paramref name="effectExists"/>, which defaults to
    /// <see cref="StatusEffectRegistry.Contains"/> (effects load before spell parts); an element's <c>plant</c>
    /// must be found by <paramref name="speciesExists"/>, which defaults to <see cref="TreeSpeciesRegistry.TryGet"/>
    /// (tree species load before them too).
    /// </summary>
    internal static SpellPart Compile(SpellPartKind kind, string name, SpellPartDefinition definition, string path,
        Func<string, bool>? effectExists = null, Func<string, bool>? speciesExists = null)
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
        var impact = CheckImpact(kind, definition.Impact, path);
        var plant = CheckPlant(kind, definition.Plant, impact, path,
            speciesExists ?? (species => TreeSpeciesRegistry.TryGet(species) != null));
        var delivery = CheckDelivery(kind, definition.Delivery, path);

        return new SpellPart(kind, name, definition.Name ?? name, costShare, complexity, tags, damageFactor, rangeFactor,
            effect, impact, plant, delivery);
    }

    /// <summary>An element's impact flags: none when unset; set only on an element, each a known name at most once.</summary>
    private static SpellImpact CheckImpact(SpellPartKind kind, string[]? names, string path)
    {
        if (names == null) return SpellImpact.None;
        if (kind != SpellPartKind.Element)
            throw new InvalidOperationException($"Spell part '{path}' sets impact, which only an element may carry.");

        var impact = SpellImpact.None;
        foreach (var name in names)
        {
            var flag = ImpactFlagOf(name);
            if (flag == SpellImpact.None)
                throw new InvalidOperationException(
                    $"Spell part '{path}' lists impact '{name}'; expected one of {string.Join(", ", ImpactNames.Select(entry => entry.Name))}.");
            if ((impact & flag) != 0)
                throw new InvalidOperationException($"Spell part '{path}' lists impact '{name}' twice.");
            impact |= flag;
        }
        return impact;
    }

    private static SpellImpact ImpactFlagOf(string? name)
    {
        foreach (var (known, flag) in ImpactNames)
        {
            if (string.Equals(name, known, StringComparison.Ordinal)) return flag;
        }
        return SpellImpact.None;
    }

    /// <summary>An element's planted species: null when unset; set only on an element with the grow impact, naming a loaded species.</summary>
    private static string? CheckPlant(SpellPartKind kind, string? plant, SpellImpact impact, string path,
        Func<string, bool> speciesExists)
    {
        if (plant == null) return null;
        if (kind != SpellPartKind.Element)
            throw new InvalidOperationException($"Spell part '{path}' sets plant, which only an element may carry.");
        if ((impact & SpellImpact.Grow) == 0)
            throw new InvalidOperationException($"Spell part '{path}' sets plant without grow.");
        if (string.IsNullOrWhiteSpace(plant) || !speciesExists(plant))
            throw new InvalidOperationException(
                $"Spell part '{path}' names plant '{plant}', which is not a registered tree species.");
        return plant;
    }

    /// <summary>A shape's delivery: projectile when unset; set only on a shape, a known name.</summary>
    private static SpellDelivery CheckDelivery(SpellPartKind kind, string? delivery, string path)
    {
        if (delivery == null) return SpellDelivery.Projectile;
        if (kind != SpellPartKind.Shape)
            throw new InvalidOperationException($"Spell part '{path}' sets delivery, which only a shape may carry.");
        foreach (var (known, value) in DeliveryNames)
        {
            if (string.Equals(delivery, known, StringComparison.Ordinal)) return value;
        }
        throw new InvalidOperationException(
            $"Spell part '{path}' has delivery '{delivery}'; expected one of {string.Join(", ", DeliveryNames.Select(entry => entry.Name))}.");
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
