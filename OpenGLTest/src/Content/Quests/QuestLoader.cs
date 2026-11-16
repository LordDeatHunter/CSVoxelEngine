using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Entities;
using VoxelEngine.Items;

namespace VoxelEngine.Content.Quests;

/// <summary>
/// Loads <c>data/quests/&lt;id&gt;.json</c>, one quest per file named by its id, into <see cref="QuestRegistry"/>. A
/// missing folder is no quests. The book's free text labels are <c>data/quests/labels/&lt;name&gt;.json</c>, one per file.
/// Every error names its file: a duplicate or blank id, an unknown parent, a parent cycle, an unknown
/// icon, block, item or entity, a bad shape, position or count. <see cref="ReadQuests"/> and
/// <see cref="Compile"/> touch no static state but the content tables, so tests call them directly.
/// Runs last in <see cref="ContentRegistry.Load"/>, once blocks, items and entities are registered.
/// </summary>
internal static class QuestLoader
{
    internal static void LoadQuests(string folder) => QuestRegistry.Publish(ReadQuests(folder));

    /// <summary>Every quest file directly in <paramref name="folder"/>, in file-name order, each quest's id its file
    /// name; empty when the folder is missing.</summary>
    internal static QuestBook ReadQuests(string folder)
    {
        if (!Directory.Exists(folder)) return QuestBook.Empty;
        var labels = Path.Combine(folder, LabelFolder);
        return Compile([.. ContentFiles.JsonFilesIn(folder).Select(file => (ReadQuest(file.Path), file.Path))],
            Directory.Exists(labels)
                ? [.. ContentFiles.JsonFilesIn(labels).Select(file => (ReadLabel(file.Path), file.Path))]
                : null);
    }

    /// <summary>The subfolder of the quest folder that holds the book's labels.</summary>
    internal const string LabelFolder = "labels";

    /// <summary>One label file, its <see cref="QuestLabelDefinition.Id"/> set from the file name.</summary>
    internal static QuestLabelDefinition ReadLabel(string path)
    {
        var label = ContentFiles.ReadJson<QuestLabelDefinition>(path);
        label.Id = Path.GetFileNameWithoutExtension(path);
        return label;
    }

    /// <summary>The JSON names of every label size and color, in declaration order.</summary>
    internal static IReadOnlyList<string> LabelSizeNames { get; } = [.. Enum.GetNames<QuestLabelSize>().Select(n => n.ToLowerInvariant())];

    internal static IReadOnlyList<string> LabelColorNames { get; } = [.. Enum.GetNames<QuestLabelColor>().Select(n => n.ToLowerInvariant())];

    /// <summary>Compiles one label, throwing an error that names its file: blank text, no position, an unknown size or
    /// color.</summary>
    internal static QuestLabel CompileLabel(QuestLabelDefinition label, string path)
    {
        if (string.IsNullOrWhiteSpace(label.Text))
            throw new InvalidOperationException($"Quest label '{label.Id}' in '{path}' has no text.");
        var position = ReadVector(label.Pos, "pos", path)
                       ?? throw new InvalidOperationException($"Quest label '{label.Id}' in '{path}' has no pos.");
        return new QuestLabel(label.Id!, label.Text, position,
            ReadName(label.Size, QuestLabelSize.Medium, LabelSizeNames, "size", $"Quest label '{label.Id}'", path),
            ReadName(label.Color, QuestLabelColor.Gray, LabelColorNames, "color", $"Quest label '{label.Id}'", path));
    }

    // An enum's JSON name (any case) to its member, null being the default; anything else is refused naming the owner
    // ("Quest 'x'", "Quest label 'x'") and its file
    private static T ReadName<T>(string? value, T fallback, IReadOnlyList<string> names, string field, string owner, string path)
        where T : struct, Enum
    {
        if (value == null) return fallback;
        var index = names.ToList().FindIndex(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            throw new InvalidOperationException(
                $"{owner} in '{path}' has {field} '{value}'; expected {string.Join(", ", names)}.");
        return Enum.GetValues<T>()[index];
    }

    /// <summary>One quest file, its <see cref="QuestDefinition.Id"/> set from the file name.</summary>
    internal static QuestDefinition ReadQuest(string path)
    {
        var quest = ContentFiles.ReadJson<QuestDefinition>(path);
        quest.Id = Path.GetFileNameWithoutExtension(path);
        return quest;
    }

    /// <summary>Compiles the quests (each with its id set, and the file it came from) into one book, throwing an error
    /// that names the offending file.</summary>
    internal static QuestBook Compile(IReadOnlyList<(QuestDefinition Quest, string Path)> quests,
        IReadOnlyList<(QuestLabelDefinition Label, string Path)>? labels = null)
    {
        // Pass 1: ids, so a parent may name any quest
        var entries = new List<(QuestDefinition Quest, string Path)>();
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (quest, path) in quests)
        {
            if (string.IsNullOrWhiteSpace(quest.Id))
                throw new InvalidOperationException($"Quest file '{path}' has no id.");
            if (!indexById.TryAdd(quest.Id, entries.Count))
                throw new InvalidOperationException($"Quest file '{path}' repeats quest id '{quest.Id}', already used.");
            entries.Add((quest, path));
        }

        // Pass 2: parents and children by index
        var parents = new int[entries.Count][];
        var children = new List<int>[entries.Count];
        for (var i = 0; i < entries.Count; i++) children[i] = [];
        for (var i = 0; i < entries.Count; i++)
        {
            var (quest, path) = entries[i];
            var names = quest.Parents ?? [];
            var list = new List<int>();
            foreach (var parent in names)
            {
                if (!indexById.TryGetValue(parent, out var p))
                    throw new InvalidOperationException($"Quest '{quest.Id}' in '{path}' names parent '{parent}', which is not a quest.");
                if (p == i)
                    throw new InvalidOperationException($"Quest '{quest.Id}' in '{path}' names itself as a parent.");
                if (list.Contains(p))
                    throw new InvalidOperationException($"Quest '{quest.Id}' in '{path}' names parent '{parent}' twice.");
                list.Add(p);
                children[p].Add(i);
            }
            parents[i] = [.. list];
        }
        RejectCycles(entries, parents, children);

        var compiled = new QuestType[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var (quest, path) = entries[i];
            var position = ReadVector(quest.Pos, "pos", path)
                           ?? throw new InvalidOperationException($"Quest '{quest.Id}' in '{path}' has no pos.");
            if (quest.Icon == null || !ContentFiles.TryResolve(quest.Icon, out var icon))
                throw new InvalidOperationException($"Quest '{quest.Id}' in '{path}' has icon '{quest.Icon}', which is not a registered block or item.");
            if (quest.Requirements is not { Count: > 0 } requirements)
                throw new InvalidOperationException($"Quest '{quest.Id}' in '{path}' has no requirements.");

            compiled[i] = new QuestType
            {
                Index = i,
                Id = quest.Id!,
                Title = string.IsNullOrWhiteSpace(quest.Title) ? quest.Id! : quest.Title,
                Description = quest.Description ?? "",
                IconId = icon,
                Position = position,
                Shape = ReadShape(quest.Shape, parents[i].Length == 0, quest.Id!, path),
                Reveal = ReadName(quest.Reveal, QuestReveal.Unlocked, RevealNames, "reveal", $"Quest '{quest.Id}'", path),
                Unlock = ReadName(quest.Unlock, QuestUnlock.AllParentsDone, UnlockNames, "unlock", $"Quest '{quest.Id}'", path),
                Readable = quest.Readable ?? true,
                AnyOf = quest.AnyOf ?? false,
                Parents = parents[i],
                Children = [.. children[i]],
                Requirements = [.. requirements.Select(r => CompileRequirement(r, quest.Id!, path))]
            };
        }
        return new QuestBook(compiled, [.. (labels ?? []).Select(l => CompileLabel(l.Label, l.Path))]);
    }

    private static QuestRequirement CompileRequirement(QuestRequirementDefinition definition, string quest, string path)
    {
        var count = definition.Count ?? 1;
        if (count < 1)
            throw new InvalidOperationException($"Quest '{quest}' in '{path}' has a requirement count {count}; expected at least 1.");

        var kind = definition.Type?.ToLowerInvariant() switch
        {
            "obtain" => QuestRequirementKind.Obtain,
            "craft" => QuestRequirementKind.Craft,
            "break" => QuestRequirementKind.Break,
            "place" => QuestRequirementKind.Place,
            "kill" => QuestRequirementKind.Kill,
            _ => throw new InvalidOperationException(
                $"Quest '{quest}' in '{path}' has requirement type '{definition.Type}'; expected obtain, craft, break, place or kill.")
        };

        switch (kind)
        {
            case QuestRequirementKind.Kill:
                if (definition.Entity == null || EntityRegistry.TryGet(definition.Entity) is not { } entity)
                    throw new InvalidOperationException($"Quest '{quest}' in '{path}' names entity '{definition.Entity}', which is not a registered entity.");
                return new QuestRequirement(kind, 0, entity.Name, count, entity.DisplayName);
            case QuestRequirementKind.Break or QuestRequirementKind.Place:
                if (definition.Block == null || !ContentRegistry.BlockIds.TryGetValue(StripPrefix(definition.Block, "block/"), out var block))
                    throw new InvalidOperationException($"Quest '{quest}' in '{path}' names block '{definition.Block}', which is not a registered block.");
                return new QuestRequirement(kind, block, null, count, BlockType.ById[block]!.DisplayName);
            default:
                if (definition.Tag != null)
                    return CompileTagRequirement(definition, kind, count, quest, path);
                if (definition.Item == null || !ContentFiles.TryResolve(definition.Item, out var item) || ItemRegistry.TryGet(item) is not { } type)
                    throw new InvalidOperationException($"Quest '{quest}' in '{path}' names item '{definition.Item}', which is not a registered item or block item.");
                return new QuestRequirement(kind, item, null, count, type.Name);
        }
    }

    private static QuestRequirement CompileTagRequirement(QuestRequirementDefinition definition,
        QuestRequirementKind kind, int count, string quest, string path)
    {
        if (definition.Item != null)
            throw new InvalidOperationException($"Quest '{quest}' in '{path}' names both item '{definition.Item}' and tag '{definition.Tag}'.");
        if (!ContentRegistry.Tags.TryGetValue(definition.Tag!, out var members) || members.Count == 0)
            throw new InvalidOperationException($"Quest '{quest}' in '{path}' names tag '{definition.Tag}', which is not a loaded, non-empty tag.");
        var label = string.IsNullOrWhiteSpace(definition.Label) ? "Any " + definition.Tag!.Replace('_', ' ') : definition.Label;
        return new QuestRequirement(kind, members.Min(), null, count, label, members);
    }

    private static string StripPrefix(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;

    private static QuestShape ReadShape(string? shape, bool root, string quest, string path) => shape?.ToLowerInvariant() switch
    {
        null => root ? QuestShape.Hex : QuestShape.Circle,
        "hex" => QuestShape.Hex,
        "circle" => QuestShape.Circle,
        _ => throw new InvalidOperationException($"Quest '{quest}' in '{path}' has shape '{shape}'; expected hex or circle.")
    };

    /// <summary>The JSON names of every reveal mode, in declaration order (camelCase).</summary>
    internal static IReadOnlyList<string> RevealNames { get; } = CamelNames<QuestReveal>();

    /// <summary>The JSON names of every unlock mode, in declaration order (camelCase).</summary>
    internal static IReadOnlyList<string> UnlockNames { get; } = CamelNames<QuestUnlock>();

    private static string[] CamelNames<T>() where T : struct, Enum =>
        [.. Enum.GetNames<T>().Select(n => char.ToLowerInvariant(n[0]) + n[1..])];

    private static Vector2? ReadVector(float[]? values, string field, string path)
    {
        if (values == null) return null;
        if (values is not { Length: 2 } || !float.IsFinite(values[0]) || !float.IsFinite(values[1]))
            throw new InvalidOperationException($"Quest graph '{path}' has a {field} that is not two finite numbers.");
        return new Vector2(values[0], values[1]);
    }

    // Kahn's algorithm: every quest must be reachable by peeling off quests whose parents are all peeled
    private static void RejectCycles(
        List<(QuestDefinition Quest, string Path)> entries,
        int[][] parents, List<int>[] children)
    {
        var pending = parents.Select(p => p.Length).ToArray();
        var ready = new Queue<int>(Enumerable.Range(0, entries.Count).Where(i => pending[i] == 0));
        var peeled = 0;
        while (ready.TryDequeue(out var i))
        {
            peeled++;
            foreach (var child in children[i])
                if (--pending[child] == 0) ready.Enqueue(child);
        }
        if (peeled == entries.Count) return;
        var stuck = Enumerable.Range(0, entries.Count).First(i => pending[i] > 0);
        throw new InvalidOperationException(
            $"Quest '{entries[stuck].Quest.Id}' in '{entries[stuck].Path}' is part of a parent cycle, so it could never unlock.");
    }
}
