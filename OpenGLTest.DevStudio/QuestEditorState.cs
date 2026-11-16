using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using VoxelEngine.Content;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Quests;
using VoxelEngine.Items;

namespace VoxelEngine.DevStudio;

/// <summary>
/// The QUESTS tab's rules, GL-free: the graph documents being edited, the selection, every edit (add, delete, move,
/// link, rename, the fields and the requirements), saving and reverting the whole set or one quest. Every edit
/// recompiles, which also works out what differs from disk (<see cref="QuestGraphDocument.Dirty"/>,
/// <see cref="IsQuestDirty"/>):
/// <see cref="Preview"/> is a lenient book that always draws (unknown icons and parents are skipped), and
/// <see cref="Error"/> is what the real <see cref="QuestLoader.Compile"/> refuses, which blocks <see cref="Save"/>.
/// </summary>
public sealed class QuestEditorState
{
    /// <summary>The grid step a dragged quest or graph origin snaps to.</summary>
    public const float SnapStep = 0.5f;

    /// <summary>The requirement types, in the order the type dropdown lists them.</summary>
    public static readonly string[] RequirementTypes = ["obtain", "craft", "break", "place", "kill"];

    /// <summary>The prefix that makes an obtain or craft target a tag instead of an item.</summary>
    public const string TagPrefix = "#";

    private const string DefaultIcon = "stone";

    public QuestEditorState(string folder, IEnumerable<QuestGraphDocument> documents)
    {
        Folder = folder;
        Documents = [.. documents];
        Recompile();
    }

    private readonly QuestEditHistory _history = new();
    // Set while an undo or redo rebuilds the documents, so the rebuild is not recorded as a new step
    private bool _restoring;

    /// <summary>The key of the gesture in progress (one typed field, one drag), whose changes undo as one step; the
    /// page sets it when the gesture starts and clears it when it ends.</summary>
    public string? Gesture { get; set; }

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    /// <summary>Puts the graphs and the selection back as they were before the last recorded change. False when there
    /// is nothing to undo. Saving is not undone: a quest saved since reads as unsaved again.</summary>
    public bool Undo() => Restore(_history.Undo());

    /// <summary>Re-applies the last undone change. False when there is nothing to redo.</summary>
    public bool Redo() => Restore(_history.Redo());

    private bool Restore(QuestEditSnapshot? snapshot)
    {
        if (snapshot == null) return false;
        var current = Documents.ToDictionary(d => d.Path);
        Documents.Clear();
        foreach (var graph in snapshot.Graphs)
        {
            var saved = current.TryGetValue(graph.Path, out var document) ? document.Saved
                : File.Exists(graph.Path) ? QuestGraphDocument.Load(graph.Path).Saved
                : null;
            Documents.Add(QuestGraphDocument.Restore(graph, saved));
        }
        SelectedGraph = Math.Clamp(snapshot.SelectedGraph, 0, Math.Max(0, Documents.Count - 1));
        Select(snapshot.SelectedQuest is { } id && Find(id) != null ? id : null);
        if (snapshot.SelectedLink is { } link) _selectedLink = link;
        _restoring = true;
        Recompile();
        _restoring = false;
        return true;
    }

    private QuestEditSnapshot Capture(string text) => new(text,
        [
            .. Documents.Select(d => new QuestEditSnapshot.Graph(d.Path, d.Header, QuestGraphDocument.Clone(d.Definition),
                [.. d.Quests.Select(d.OriginalId)]))
        ],
        SelectedQuestId, _selectedLink, SelectedGraph);

    /// <summary>Every graph file under <paramref name="folder"/>, in the loader's (file name) order.</summary>
    public static QuestEditorState Load(string folder) => new(folder, ReadFolder(folder));

    private static IEnumerable<QuestGraphDocument> ReadFolder(string folder) => Directory.Exists(folder)
        ? ContentFiles.JsonFilesIn(folder).Select(file => QuestGraphDocument.Load(file.Path))
        : [];

    // The ids of the quests whose block differs from the one on disk (or that are not on disk at all)
    private readonly HashSet<string> _dirtyQuests = new(StringComparer.Ordinal);

    public string Folder { get; }

    public List<QuestGraphDocument> Documents { get; }

    /// <summary>The graph the graph selector shows, where ADD QUEST adds.</summary>
    public int SelectedGraph { get; set; }

    public string? SelectedQuestId { get; set; }

    private (string Child, string Parent)? _selectedLink;

    /// <summary>The selected parent link (the child depends on the parent), or null; it reads null once the link is
    /// gone. A quest and a link are never selected together.</summary>
    public (string Child, string Parent)? SelectedLink =>
        _selectedLink is { } link && Find(link.Child)?.Quest.Parents?.Contains(link.Parent) == true ? link : null;

    public void SelectLink(string child, string parent)
    {
        Select(null);
        _selectedLink = (child, parent);
        _history.Touch(SelectedQuestId, _selectedLink, SelectedGraph);
    }

    /// <summary>The selected quest's requirement a grid click or a PICK fills, or -1 for the quest's icon.</summary>
    public int PickRequirement { get; set; } = -1;

    public QuestBook Preview { get; private set; } = QuestBook.Empty;

    /// <summary>Why the real loader refuses the graphs, or null when they compile.</summary>
    public string? Error { get; private set; }

    /// <summary>The quest <see cref="Error"/> names, or null.</summary>
    public string? ErrorQuestId { get; private set; }

    public bool Dirty => Documents.Any(d => d.Dirty);

    /// <summary>Whether the quest differs from its saved version, or has none.</summary>
    public bool IsQuestDirty(string id) => _dirtyQuests.Contains(id);

    public QuestGraphDocument? Graph => SelectedGraph >= 0 && SelectedGraph < Documents.Count ? Documents[SelectedGraph] : null;

    public QuestGraphDefinition.QuestDefinition? SelectedQuest =>
        SelectedQuestId != null && Find(SelectedQuestId) is { } found ? found.Quest : null;

    /// <summary>The document and definition of the quest with that id, or null.</summary>
    public (QuestGraphDocument Document, QuestGraphDefinition.QuestDefinition Quest)? Find(string id)
    {
        foreach (var document in Documents)
        foreach (var quest in document.Quests)
            if (quest.Id == id)
                return (document, quest);
        return null;
    }

    public void Select(string? id)
    {
        SelectedQuestId = id;
        _selectedLink = null;
        PickRequirement = -1;
        if (id != null && Find(id) is { } found) SelectedGraph = Documents.IndexOf(found.Document);
        _history.Touch(SelectedQuestId, _selectedLink, SelectedGraph);
    }

    /// <summary>Adds a quest at the absolute grid point to the selected graph and selects it. Its icon and its one
    /// obtain requirement name <paramref name="icon"/> (default stone), so it compiles as it is; its title is
    /// <paramref name="title"/>, default "New Quest". With a <paramref name="parent"/>, it goes into that quest's graph
    /// already depending on it.</summary>
    public string? AddQuest(Vector2 grid, string? icon = null, string? title = null, string? parent = null)
    {
        var document = parent != null && Find(parent) is { } owner ? owner.Document : Graph;
        if (document == null) return null;
        var id = UniqueId("quest");
        icon ??= DefaultIcon;
        var position = Snap(grid - document.Origin, true);
        document.Quests.Add(new QuestGraphDefinition.QuestDefinition
        {
            Id = id,
            Title = title ?? "New Quest",
            Icon = icon,
            Pos = [position.X, position.Y],
            Parents = parent != null && Find(parent) != null ? [parent] : null,
            Requirements = [new QuestGraphDefinition.RequirementDefinition { Type = "obtain", Item = icon }],
        });
        Select(id);
        Recompile();
        return id;
    }

    /// <summary>Removes the quest and every parent link to it, in every graph.</summary>
    public void DeleteQuest(string id)
    {
        if (Find(id) is not { } found) return;
        found.Document.Quests.Remove(found.Quest);
        foreach (var document in Documents)
        foreach (var quest in document.Quests)
        {
            if (quest.Parents is not { } parents || !parents.Contains(id)) continue;
            quest.Parents = Shrink(parents.Where(p => p != id));
        }
        if (SelectedQuestId == id) Select(null);
        Recompile();
    }

    /// <summary>Moves the quest so its node sits at the absolute grid point; stored relative to its graph's origin.</summary>
    public void MoveQuest(string id, Vector2 grid, bool snap)
    {
        if (Find(id) is not { } found) return;
        var position = Snap(grid - found.Document.Origin, snap);
        found.Quest.Pos = [position.X, position.Y];
        Recompile();
    }

    /// <summary>Moves a whole graph by setting its origin.</summary>
    public void MoveGraph(QuestGraphDocument document, Vector2 origin, bool snap)
    {
        var snapped = Snap(origin, snap);
        document.Definition.Origin = [snapped.X, snapped.Y];
        Recompile();
    }

    /// <summary>Links <paramref name="parent"/> as a parent of <paramref name="child"/>, or unlinks it when it already
    /// is one. Returns why a link is refused (itself, an unknown quest, or a cycle), else null.</summary>
    public string? ToggleParent(string child, string parent)
    {
        if (child == parent) return "A quest cannot be its own parent.";
        if (Find(child) is not { } found || Find(parent) == null) return "No such quest.";
        return found.Quest.Parents?.Contains(parent) == true ? RemoveLink(child, parent) : AddParent(child, parent);
    }

    /// <summary>Removes the link that makes <paramref name="child"/> depend on <paramref name="parent"/>. Returns why
    /// nothing was removed, else null.</summary>
    public string? RemoveLink(string child, string parent)
    {
        if (Find(child) is not { } found || found.Quest.Parents?.Contains(parent) != true) return "No such link.";
        found.Quest.Parents = Shrink(found.Quest.Parents.Where(p => p != parent));
        Recompile();
        return null;
    }

    /// <summary>Turns the link around: <paramref name="parent"/> depends on <paramref name="child"/> instead, and the
    /// turned link is selected. Refused (changing nothing) when another path already makes the parent a prerequisite
    /// of the child, since the turned link would close a cycle. Returns why, else null.</summary>
    public string? SwapLink(string child, string parent)
    {
        if (Find(child) is not { } dependent || dependent.Quest.Parents?.Contains(parent) != true) return "No such link.";
        if (Find(parent) is not { } prerequisite) return "No such quest.";
        var before = dependent.Quest.Parents;
        dependent.Quest.Parents = Shrink(before.Where(p => p != parent));
        if (IsAncestor(parent, child))
        {
            dependent.Quest.Parents = before;
            return $"'{child}' also depends on '{parent}' through other quests: swapping would make a cycle.";
        }
        prerequisite.Quest.Parents = [.. prerequisite.Quest.Parents ?? [], child];
        SelectLink(parent, child);
        Recompile();
        return null;
    }

    /// <summary>Makes <paramref name="child"/> depend on <paramref name="parent"/>. Returns why the link is refused
    /// (itself, an unknown quest, a link that already exists, or a cycle), else null.</summary>
    public string? AddParent(string child, string parent)
    {
        if (child == parent) return "A quest cannot be its own parent.";
        if (Find(child) is not { } found || Find(parent) == null) return "No such quest.";
        var parents = found.Quest.Parents ?? [];
        if (parents.Contains(parent)) return $"'{child}' already depends on '{parent}'.";
        if (IsAncestor(child, parent)) return $"'{parent}' already depends on '{child}': linking would make a cycle.";
        found.Quest.Parents = [.. parents, parent];
        Recompile();
        return null;
    }

    /// <summary>Renames a quest and every parent link to it. Returns why the new id is refused, else null.</summary>
    public string? RenameQuest(string id, string newId)
    {
        newId = newId.Trim();
        if (newId == id) return null;
        if (newId.Length == 0) return "A quest id cannot be blank.";
        if (Find(newId) != null) return $"Quest id '{newId}' is already used.";
        if (Find(id) is not { } found) return "No such quest.";
        found.Quest.Id = newId;
        foreach (var document in Documents)
        foreach (var quest in document.Quests)
        {
            if (quest.Parents is not { } parents || !parents.Contains(id)) continue;
            quest.Parents = [.. parents.Select(p => p == id ? newId : p)];
        }
        if (SelectedQuestId == id) SelectedQuestId = newId;
        if (_selectedLink is { } link)
            _selectedLink = (link.Child == id ? newId : link.Child, link.Parent == id ? newId : link.Parent);
        Recompile();
        return null;
    }

    public void SetTitle(string id, string title) => Edit(id, q => q.Title = title);

    public void SetDescription(string id, string description) =>
        Edit(id, q => q.Description = description.Length == 0 ? null : description);

    public void SetIcon(string id, string icon) => Edit(id, q => q.Icon = icon);

    /// <summary>Cycles the frame: the default (hex for a root, else circle), then hex, then circle.</summary>
    public void CycleShape(string id) => Edit(id, q => q.Shape = q.Shape switch
    {
        null => "hex",
        "hex" => "circle",
        _ => null,
    });

    /// <summary>Sets when the quest shows as a locked preview: <paramref name="mode"/> indexes
    /// <see cref="QuestLoader.RevealNames"/>, and the first (the default) clears the field.</summary>
    public void SetReveal(string id, int mode) =>
        Edit(id, q => q.Reveal = mode <= 0 || mode >= QuestLoader.RevealNames.Count ? null : QuestLoader.RevealNames[mode]);

    /// <summary>The index in <see cref="QuestLoader.RevealNames"/> of the quest's reveal mode (0, the default, for an
    /// unknown one, which <see cref="Error"/> reports).</summary>
    public static int RevealOf(QuestGraphDefinition.QuestDefinition quest) => quest.Reveal == null ? 0
        : Math.Max(0, QuestLoader.RevealNames.ToList().FindIndex(n => string.Equals(n, quest.Reveal, StringComparison.OrdinalIgnoreCase)));

    public void ToggleReadable(string id) => Edit(id, q => q.Readable = q.Readable == false ? null : false);

    public void ToggleAnyOf(string id) => Edit(id, q => q.AnyOf = q.AnyOf == true ? null : true);

    public void SetGraphTitle(QuestGraphDocument document, string title)
    {
        document.Definition.Title = title.Length == 0 ? null : title;
        Recompile();
    }

    public void AddRequirement(string id) =>
        Edit(id, q => (q.Requirements ??= []).Add(new QuestGraphDefinition.RequirementDefinition
        {
            Type = "obtain",
            Item = q.Icon ?? DefaultIcon,
        }));

    public void RemoveRequirement(string id, int index) => EditRequirement(id, index, (q, _) =>
    {
        q.Requirements!.RemoveAt(index);
        PickRequirement = -1;
    });

    /// <summary>Sets the requirement's type (one of <see cref="RequirementTypes"/>), carrying its target over to the
    /// field that type reads.</summary>
    public void SetRequirementType(string id, int index, string type) => EditRequirement(id, index, (_, r) =>
    {
        var target = TargetOf(r);
        r.Type = type;
        ApplyTarget(r, target);
    });

    /// <summary>Sets what the requirement names: an item (or <c>#tag</c>) for obtain and craft, a block for break
    /// and place, an entity for kill.</summary>
    public void SetRequirementTarget(string id, int index, string target) =>
        EditRequirement(id, index, (_, r) => ApplyTarget(r, target));

    public void SetRequirementLabel(string id, int index, string label) =>
        EditRequirement(id, index, (_, r) => r.Label = label.Length == 0 ? null : label);

    public void AdjustRequirementCount(string id, int index, int delta) =>
        EditRequirement(id, index, (_, r) =>
        {
            var count = Math.Max(1, (r.Count ?? 1) + delta);
            r.Count = count == 1 ? null : count;
        });

    /// <summary>The text a requirement's target field shows: its item, <c>#tag</c>, block or entity.</summary>
    public static string TargetOf(QuestGraphDefinition.RequirementDefinition requirement) =>
        requirement.Tag is { } tag ? TagPrefix + tag
            : requirement.Item ?? requirement.Block ?? requirement.Entity ?? "";

    /// <summary>Whether the requirement's type names a tag-capable item (obtain or craft).</summary>
    public static bool TakesItem(QuestGraphDefinition.RequirementDefinition requirement) =>
        requirement.Type?.ToLowerInvariant() is "obtain" or "craft";

    /// <summary>Adds a new graph file (<c>graph_N.json</c>) holding one root quest at the origin, and selects it.</summary>
    public QuestGraphDocument AddGraph(Vector2 origin)
    {
        var n = 1;
        string path;
        do path = Path.Combine(Folder, $"graph_{n++}.json");
        while (File.Exists(path) || Documents.Any(d => d.Path == path));
        var snapped = Snap(origin, true);
        var document = new QuestGraphDocument(path,
            new QuestGraphDefinition { Title = "New Graph", Origin = [snapped.X, snapped.Y], Quests = [] }, [], saved: false);
        Documents.Add(document);
        SelectedGraph = Documents.Count - 1;
        AddQuest(snapped);
        return document;
    }

    /// <summary>Writes every dirty graph, unless the graphs do not compile. Returns the status line.</summary>
    public string Save()
    {
        Recompile();
        if (Error != null) return "Not saved: " + Error;
        var dirty = Documents.Where(d => d.Dirty).ToList();
        if (dirty.Count == 0) return "Nothing to save";
        Directory.CreateDirectory(Folder);
        foreach (var document in dirty)
        {
            QuestGraphWriter.Save(document);
            document.MarkSaved();
        }
        Recompile();
        return $"Saved {string.Join(", ", dirty.Select(d => Path.GetFileName(d.Path)))}";
    }

    /// <summary>Writes only this quest's edit to its file: the file as saved, with this quest put in (or added).
    /// Refused when the files on disk would then not compile (a rename its saved children still name, a link to a
    /// quest that is not saved yet): save all instead. Returns the status line.</summary>
    public string SaveQuest(string id)
    {
        if (Find(id) is not { } found) return "No such quest.";
        var (document, quest) = found;
        if (!IsQuestDirty(id)) return $"{id} has no unsaved edits";
        var saved = document.Saved is { } onDisk
            ? QuestGraphDocument.Clone(onDisk)
            : new QuestGraphDefinition { Title = document.Definition.Title, Origin = document.Definition.Origin?.ToArray() };
        var quests = saved.Quests ??= [];
        var copy = QuestGraphDocument.Clone(quest);
        var at = document.OriginalId(quest) is { } original ? quests.FindIndex(q => q.Id == original) : -1;
        if (at >= 0) quests[at] = copy;
        else quests.Insert(Math.Min(document.Quests.IndexOf(quest), quests.Count), copy);

        // The graphs as the disk would hold them: this file with the one change, every other file as saved
        var onDiskAfter = Documents
            .Where(d => d == document || d.Saved != null)
            .Select(d => (d.Name, d == document ? saved : d.Saved!, d.Path))
            .ToList();
        try
        {
            QuestLoader.Compile(onDiskAfter);
        }
        catch (InvalidOperationException e)
        {
            return $"Cannot save {id} on its own: {e.Message} Save all instead.";
        }

        Directory.CreateDirectory(Folder);
        QuestGraphWriter.Save(document.Path, saved, document.Header);
        document.MarkQuestSaved(saved, quest);
        Recompile();
        return $"Saved {id} to {Path.GetFileName(document.Path)}";
    }

    /// <summary>Puts the quest back as it is on disk (its id too, with every link to it), or removes a quest that was
    /// never saved. Returns the status line.</summary>
    public string RevertQuest(string id)
    {
        if (Find(id) is not { } found) return "No such quest.";
        var (document, quest) = found;
        if (document.SavedQuest(quest) is not { } saved)
        {
            DeleteQuest(id);
            return $"Removed {id}, which was never saved";
        }
        var original = saved.Id!;
        if (RenameQuest(id, original) is { } refused) return $"Cannot revert {id}: {refused}";
        document.Replace(quest, QuestGraphDocument.Clone(saved), original);
        Recompile();
        return $"Reverted {original}";
    }

    /// <summary>Drops every unsaved edit: the graphs are read from disk again, and a graph never saved is gone. It is
    /// a recorded change, so <see cref="Undo"/> brings the edits back.</summary>
    public string RevertAll()
    {
        Documents.Clear();
        Documents.AddRange(ReadFolder(Folder));
        SelectedGraph = Math.Clamp(SelectedGraph, 0, Math.Max(0, Documents.Count - 1));
        PickRequirement = -1;
        if (SelectedQuestId != null && Find(SelectedQuestId) == null) SelectedQuestId = null;
        Recompile();
        return "Reverted every graph to the files on disk";
    }

    /// <summary>The content name a quest icon or target uses for a block or item id: a block's bare name, an
    /// item's <c>item/</c> name.</summary>
    public static string ContentName(ushort id)
    {
        if (id < BlockType.ById.Length && BlockType.ById[id] is { } block) return block.Name;
        foreach (var (name, itemId) in ContentRegistry.ItemIds)
            if (itemId == id)
                return "item/" + name;
        return ItemRegistry.TryGet(id)?.Name ?? DefaultIcon;
    }

    /// <summary>Rebuilds <see cref="Preview"/>, <see cref="Error"/> and what differs from disk from the documents.</summary>
    public void Recompile()
    {
        Preview = BuildPreview();
        _dirtyQuests.Clear();
        var text = new StringBuilder();
        foreach (var document in Documents)
        {
            var formatted = QuestGraphWriter.Format(document);
            text.Append(document.Path).Append('\n').Append(formatted).Append('\0');
            document.Dirty = document.Saved is not { } saved || formatted != QuestGraphWriter.Format(saved, document.Header);
            foreach (var quest in document.Quests)
                if (quest.Id != null && (document.SavedQuest(quest) is not { } savedQuest
                                         || QuestGraphWriter.FormatQuest(quest) != QuestGraphWriter.FormatQuest(savedQuest)))
                    _dirtyQuests.Add(quest.Id);
        }
        try
        {
            QuestLoader.Compile([.. Documents.Select(d => (d.Name, d.Definition, d.Path))]);
            Error = null;
            ErrorQuestId = null;
        }
        catch (InvalidOperationException e)
        {
            Error = e.Message;
            ErrorQuestId = QuestNamed.Match(e.Message) is { Success: true } match ? match.Groups[1].Value : null;
        }
        if (_restoring) return;
        var all = text.ToString();
        _history.Observe(all, () => Capture(all), Gesture, SelectedQuestId, _selectedLink, SelectedGraph);
    }

    private void Edit(string id, Action<QuestGraphDefinition.QuestDefinition> edit)
    {
        if (Find(id) is not { } found) return;
        edit(found.Quest);
        Recompile();
    }

    private void EditRequirement(string id, int index,
        Action<QuestGraphDefinition.QuestDefinition, QuestGraphDefinition.RequirementDefinition> edit) =>
        Edit(id, q =>
        {
            if (q.Requirements is not { } requirements || index < 0 || index >= requirements.Count) return;
            edit(q, requirements[index]);
        });

    private static void ApplyTarget(QuestGraphDefinition.RequirementDefinition requirement, string target)
    {
        target = target.Trim();
        requirement.Item = requirement.Tag = requirement.Block = requirement.Entity = null;
        switch (requirement.Type?.ToLowerInvariant())
        {
            case "kill":
                requirement.Entity = target;
                requirement.Label = null;
                break;
            case "break" or "place":
                requirement.Block = StripTag(target);
                requirement.Label = null;
                break;
            default:
                if (target.StartsWith(TagPrefix, StringComparison.Ordinal)) requirement.Tag = target[TagPrefix.Length..];
                else
                {
                    requirement.Item = target;
                    requirement.Label = null;
                }
                break;
        }
    }

    private static string StripTag(string target) =>
        target.StartsWith(TagPrefix, StringComparison.Ordinal) ? target[TagPrefix.Length..] : target;

    // Whether ancestor is reachable from quest by walking up the parent links
    private bool IsAncestor(string ancestor, string quest)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([quest]);
        while (stack.TryPop(out var current))
        {
            if (current == ancestor) return true;
            if (!seen.Add(current) || Find(current) is not { } found) continue;
            foreach (var parent in found.Quest.Parents ?? []) stack.Push(parent);
        }
        return false;
    }

    private string UniqueId(string stem)
    {
        var n = 1;
        while (Find($"{stem}_{n}") != null) n++;
        return $"{stem}_{n}";
    }

    private static string[]? Shrink(IEnumerable<string> parents) => parents.ToArray() is { Length: > 0 } left ? left : null;

    private static Vector2 Snap(Vector2 value, bool snap) => snap
        ? new Vector2(MathF.Round(value.X / SnapStep) * SnapStep, MathF.Round(value.Y / SnapStep) * SnapStep)
        : value;

    // Every quest as it would draw, however broken the graphs are: duplicate ids after the first, unknown parents
    // and unresolved icons are skipped rather than refused, so the canvas never goes blank mid-edit
    private QuestBook BuildPreview()
    {
        var entries = new List<(QuestGraphDefinition.QuestDefinition Quest, int Graph, Vector2 Origin)>();
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var g = 0; g < Documents.Count; g++)
        foreach (var quest in Documents[g].Quests)
            if (quest.Id != null && indexById.TryAdd(quest.Id, entries.Count))
                entries.Add((quest, g, Documents[g].Origin));

        var parents = entries.Select(e => (e.Quest.Parents ?? [])
            .Select(p => indexById.GetValueOrDefault(p, -1)).Where(p => p >= 0).Distinct().ToArray()).ToArray();
        var children = entries.Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < entries.Count; i++)
        foreach (var p in parents[i])
            children[p].Add(i);

        var quests = new QuestType[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var (quest, graph, origin) = entries[i];
            var pos = quest.Pos is { Length: 2 } p ? new Vector2(p[0], p[1]) : Vector2.Zero;
            quests[i] = new QuestType
            {
                Index = i,
                Id = quest.Id!,
                Title = string.IsNullOrWhiteSpace(quest.Title) ? quest.Id! : quest.Title,
                Description = quest.Description ?? "",
                IconId = quest.Icon != null && ContentFiles.TryResolve(quest.Icon, out var icon) ? icon : (ushort)0,
                Position = origin + pos,
                Shape = quest.Shape?.ToLowerInvariant() switch
                {
                    "hex" => QuestShape.Hex,
                    "circle" => QuestShape.Circle,
                    _ => parents[i].Length == 0 ? QuestShape.Hex : QuestShape.Circle,
                },
                Reveal = (QuestReveal)RevealOf(quest),
                Readable = quest.Readable ?? true,
                AnyOf = quest.AnyOf ?? false,
                Graph = graph,
                Parents = parents[i],
                Children = children[i],
                Requirements = [],
            };
        }
        return new QuestBook(quests, [.. Documents.Select(d => d.Title)]);
    }

    // The loader's errors about one quest start "Quest '<id>'"
    private static readonly Regex QuestNamed = new("^Quest '([^']+)'", RegexOptions.Compiled);
}
