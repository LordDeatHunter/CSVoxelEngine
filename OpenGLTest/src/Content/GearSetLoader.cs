using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Items;

namespace VoxelEngine.Content;

/// <summary>
/// Loads <c>data/sets/&lt;name&gt;.json</c> into <see cref="GearSetRegistry"/>, after the items: a set's members are
/// the head, chest and legs items whose <c>equip.set</c> names its file (boots and gloves never count). A missing folder is no sets; an item naming a set that does
/// not exist, a bonus that asks for more pieces than the set has, or an unknown stat all throw naming the file.
/// <see cref="Compile"/> touches no static state, so tests call it directly.
/// </summary>
internal static class GearSetLoader
{
    internal static void LoadSets(string folder)
    {
        var sets = new List<GearSet>();
        if (Directory.Exists(folder))
            foreach (var (name, path) in ContentFiles.JsonFilesIn(folder))
                sets.Add(Compile(name, ContentFiles.ReadJson<GearSetDefinition>(path), path, MembersOf(name)));

        var known = sets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var item in ItemRegistry.All)
            if (item is GearItem { Info.SetName: { } setName } && !known.Contains(setName))
                throw new InvalidOperationException(
                    $"Item '{item.SourceName}' belongs to set '{setName}', which has no file under data/sets.");
        GearSetRegistry.Publish(sets);
    }

    private static List<byte> MembersOf(string setName) =>
        ItemRegistry.All.OfType<GearItem>().Where(g => g.Info.SetName == setName && GearSlots.CountsForSets(g.Info.Slot)).Select(g => g.Id).ToList();

    /// <summary>Compiles one set, throwing an error that names <paramref name="path"/> on any bad field.</summary>
    internal static GearSet Compile(string name, GearSetDefinition definition, string path, IReadOnlyList<byte> members)
    {
        if (definition.Bonuses is not { Length: > 0 } bonuses)
            throw new InvalidOperationException($"Gear set '{path}' has no bonuses.");
        var compiled = new List<GearSetBonus>();
        var previous = 0;
        foreach (var bonus in bonuses)
        {
            if (bonus?.Pieces is not { } pieces || pieces <= previous || pieces > members.Count)
                throw new InvalidOperationException(
                    $"Gear set '{path}' has a bonus for {bonus?.Pieces} pieces; expected ascending counts of 1..{members.Count} (its member pieces).");
            previous = pieces;
            if (bonus.Modifiers is not { Count: > 0 } modifiers)
                throw new InvalidOperationException($"Gear set '{path}' has a {pieces}-piece bonus with no modifiers.");
            var list = new List<GearModifier>();
            foreach (var (statName, value) in modifiers)
            {
                if (!GearStats.TryParse(statName, out var stat))
                    throw new InvalidOperationException($"Gear set '{path}' has unknown stat '{statName}'.");
                if (!float.IsFinite(value) || MathF.Abs(value) > 1000f)
                    throw new InvalidOperationException($"Gear set '{path}' has {statName} {value}; expected a finite number up to 1000.");
                list.Add(new GearModifier(stat, value));
            }
            compiled.Add(new GearSetBonus(pieces, list));
        }
        return new GearSet(name, string.IsNullOrWhiteSpace(definition.Name) ? name : definition.Name!, compiled, members);
    }
}
