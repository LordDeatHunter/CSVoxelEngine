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
/// Loads <c>data/quests/&lt;graph&gt;.json</c> into <see cref="QuestRegistry"/>. A missing folder is no quests.
/// Every error names its file: a duplicate or blank id, an unknown parent, a parent cycle, an unknown
/// icon, block, item or entity, a bad shape, position or count. <see cref="ReadQuests"/> and
/// <see cref="Compile"/> touch no static state but the content tables, so tests call them directly.
/// Runs last in <see cref="ContentRegistry.Load"/>, once blocks, items and entities are registered.
/// </summary>
internal static class QuestLoader
{
    internal static void LoadQuests(string folder) => QuestRegistry.Publish(ReadQuests(folder));

    /// <summary>Every quest graph under <paramref name="folder"/>, in file-name order; empty when the folder is missing.</summary>
    internal static QuestBook ReadQuests(string folder)
    {
        if (!Directory.Exists(folder)) return QuestBook.Empty;
        var graphs = ContentFiles.JsonFilesIn(folder)
            .Select(file => (file.Name, ContentFiles.ReadJson<QuestGraphDefinition>(file.Path), file.Path))
            .ToList();
        return Compile(graphs);
    }

    /// <summary>Compiles the graphs into one book, throwing an error that names the offending file.</summary>
    internal static QuestBook Compile(IReadOnlyList<(string Name, QuestGraphDefinition Definition, string Path)> graphs)
    {
        var titles = new List<string>();
        // Pass 1: ids, so a parent may name a quest further down the file or in another file
        var entries = new List<(QuestGraphDefinition.QuestDefinition Quest, int Graph, string Path, Vector2 Origin)>();
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var g = 0; g < graphs.Count; g++)
        {
            var (name, definition, path) = graphs[g];
            titles.Add(string.IsNullOrWhiteSpace(definition.Title) ? name : definition.Title);
            var origin = ReadVector(definition.Origin, "origin", path) ?? Vector2.Zero;
            if (definition.Quests is not { Count: > 0 } quests)
                throw new InvalidOperationException($"Quest graph '{path}' has no quests.");
            foreach (var quest in quests)
            {
                if (string.IsNullOrWhiteSpace(quest.Id))
                    throw new InvalidOperationException($"Quest graph '{path}' has a quest with no id.");
                if (!indexById.TryAdd(quest.Id, entries.Count))
                    throw new InvalidOperationException($"Quest graph '{path}' repeats quest id '{quest.Id}', already used.");
                entries.Add((quest, g, path, origin));
            }
        }

        // Pass 2: parents and children by index
        var parents = new int[entries.Count][];
        var children = new List<int>[entries.Count];
        for (var i = 0; i < entries.Count; i++) children[i] = [];
        for (var i = 0; i < entries.Count; i++)
        {
            var (quest, _, path, _) = entries[i];
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
            var (quest, graph, path, origin) = entries[i];
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
                Position = origin + position,
                Shape = ReadShape(quest.Shape, parents[i].Length == 0, quest.Id!, path),
                Readable = quest.Readable ?? true,
                Graph = graph,
                Parents = parents[i],
                Children = [.. children[i]],
                Requirements = [.. requirements.Select(r => CompileRequirement(r, quest.Id!, path))]
            };
        }
        return new QuestBook(compiled, titles);
    }

    private static QuestRequirement CompileRequirement(QuestGraphDefinition.RequirementDefinition definition, string quest, string path)
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
                if (definition.Item == null || !ContentFiles.TryResolve(definition.Item, out var item) || ItemRegistry.TryGet(item) is not { } type)
                    throw new InvalidOperationException($"Quest '{quest}' in '{path}' names item '{definition.Item}', which is not a registered item or block item.");
                return new QuestRequirement(kind, item, null, count, type.Name);
        }
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

    private static Vector2? ReadVector(float[]? values, string field, string path)
    {
        if (values == null) return null;
        if (values is not { Length: 2 } || !float.IsFinite(values[0]) || !float.IsFinite(values[1]))
            throw new InvalidOperationException($"Quest graph '{path}' has a {field} that is not two finite numbers.");
        return new Vector2(values[0], values[1]);
    }

    // Kahn's algorithm: every quest must be reachable by peeling off quests whose parents are all peeled
    private static void RejectCycles(
        List<(QuestGraphDefinition.QuestDefinition Quest, int Graph, string Path, Vector2 Origin)> entries,
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
