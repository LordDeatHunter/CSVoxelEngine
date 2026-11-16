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
/// The QUESTS tab's rules, GL-free: one document per quest file (<see cref="QuestDocument"/>), the selection, every
/// edit (add, delete, move, link, rename, the fields and the requirements), saving and reverting every quest or one,
/// and undo. Every edit recompiles, which also works out what differs from disk (<see cref="QuestDocument.Dirty"/>,
/// <see cref="IsQuestDirty"/>): <see cref="Preview"/> is a lenient book that always draws (unknown icons and parents
/// are skipped), and <see cref="Error"/> is what the real <see cref="QuestLoader.Compile"/> refuses, which blocks
/// <see cref="Save"/>. A deleted quest's file, and a renamed quest's old file, stay on disk until the save deletes them.
/// </summary>
public sealed class QuestEditorState
{
    /// <summary>The grid step a dragged quest snaps to.</summary>
    public const float SnapStep = 0.5f;

    /// <summary>The requirement types, in the order the type dropdown lists them.</summary>
    public static readonly string[] RequirementTypes = ["obtain", "craft", "break", "place", "kill"];

    /// <summary>The prefix that makes an obtain or craft target a tag instead of an item.</summary>
    public const string TagPrefix = "#";

    private const string DefaultIcon = "stone";

    // The loader's errors about one quest start "Quest '<id>'"
    private static readonly Regex QuestNamed = new("^Quest '([^']+)'", RegexOptions.Compiled);

    private readonly QuestEditHistory _history = new();
    // Set while an undo or redo rebuilds the documents, so the rebuild is not recorded as a new step
    private bool _restoring;
    // Every selected quest, in the order they were selected
    private readonly List<string> _selection = [];
    private (string Child, string Parent)? _selectedLink;

    public QuestEditorState(string folder, IEnumerable<QuestDocument> documents)
    {
        Folder = folder;
        Documents = [.. documents];
        Recompile();
    }

    /// <summary>Every quest file in <paramref name="folder"/>, in the loader's (file name) order.</summary>
    public static QuestEditorState Load(string folder) => new(folder, ReadFolder(folder));

    private static IEnumerable<QuestDocument> ReadFolder(string folder) => Directory.Exists(folder)
        ? ContentFiles.JsonFilesIn(folder).Select(file => QuestDocument.Load(file.Path))
        : [];

    public string Folder { get; }

    /// <summary>Every quest document, deleted ones (whose file the next save deletes) included.</summary>
    public List<QuestDocument> Documents { get; }

    private IEnumerable<QuestDocument> Live => Documents.Where(d => !d.Deleted);

    /// <summary>The key of the gesture in progress (one typed field, one drag), whose changes undo as one step; the
    /// page sets it when the gesture starts and clears it when it ends.</summary>
    public string? Gesture { get; set; }

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    /// <summary>Every selected quest (none, one, or several).</summary>
    public IReadOnlyList<string> SelectedQuestIds => _selection;

    /// <summary>The selected quest when exactly one is selected, else null: the one the quest panel edits.</summary>
    public string? SelectedQuestId => _selection.Count == 1 ? _selection[0] : null;

    public QuestDefinition? SelectedQuest => SelectedQuestId != null ? Find(SelectedQuestId)?.Quest : null;

    public bool IsSelected(string id) => _selection.Contains(id);

    /// <summary>The selected parent link (the child depends on the parent), or null; it reads null once the link is
    /// gone. A quest and a link are never selected together.</summary>
    public (string Child, string Parent)? SelectedLink =>
        _selectedLink is { } link && Find(link.Child)?.Quest.Parents?.Contains(link.Parent) == true ? link : null;

    /// <summary>The selected quest's requirement a grid click or a PICK fills, or -1 for the quest's icon.</summary>
    public int PickRequirement { get; set; } = -1;

    public QuestBook Preview { get; private set; } = QuestBook.Empty;

    /// <summary>Why the real loader refuses the quests, or null when they compile.</summary>
    public string? Error { get; private set; }

    /// <summary>The quest <see cref="Error"/> names, or null.</summary>
    public string? ErrorQuestId { get; private set; }

    /// <summary>Whether anything differs from the files on disk.</summary>
    public bool Dirty => Documents.Any(d => d.Dirty);

    /// <summary>Whether the quest differs from its file, or has none.</summary>
    public bool IsQuestDirty(string id) => Find(id)?.Dirty == true;

    /// <summary>The document of the (not deleted) quest with that id, or null.</summary>
    public QuestDocument? Find(string id) => Live.FirstOrDefault(d => d.Id == id);

    // ---- selection

    public void Select(string? id)
    {
        _selection.Clear();
        if (id != null) _selection.Add(id);
        _selectedLink = null;
        PickRequirement = -1;
        Touch();
    }

    /// <summary>Adds the quest to the selection, or takes it out when it is already in.</summary>
    public void ToggleSelected(string id)
    {
        if (Find(id) == null) return;
        if (!_selection.Remove(id)) _selection.Add(id);
        _selectedLink = null;
        PickRequirement = -1;
        Touch();
    }

    /// <summary>Replaces the selection with the given quests (those that exist, each once).</summary>
    public void SelectMany(IEnumerable<string> ids)
    {
        _selection.Clear();
        foreach (var id in ids)
            if (!_selection.Contains(id) && Find(id) != null)
                _selection.Add(id);
        _selectedLink = null;
        PickRequirement = -1;
        Touch();
    }

    public void SelectLink(string child, string parent)
    {
        Select(null);
        _selectedLink = (child, parent);
        Touch();
    }

    private void Touch() => _history.Touch([.. _selection], _selectedLink);

    // ---- undo

    /// <summary>Puts the quests and the selection back as they were before the last recorded change. False when there
    /// is nothing to undo. Saving is not undone: a quest saved since reads as unsaved again.</summary>
    public bool Undo() => Restore(_history.Undo());

    /// <summary>Re-applies the last undone change. False when there is nothing to redo.</summary>
    public bool Redo() => Restore(_history.Redo());

    // The disk does not undo: each restored quest takes the file it recorded while that file is still there, else the
    // file of its own id, else none; a file no restored quest takes is a deletion for the next save
    private bool Restore(QuestEditSnapshot? snapshot)
    {
        if (snapshot == null) return false;
        var disk = Documents.Where(d => d.SavedId != null).ToDictionary(d => d.SavedId!, d => d.Saved!);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        Documents.Clear();
        foreach (var entry in snapshot.Quests)
        {
            var quest = QuestDocument.Clone(entry.Quest);
            var savedId = Claim(entry.SavedId) ?? (entry.Deleted ? null : Claim(quest.Id));
            if (entry.Deleted && savedId == null) continue;
            Documents.Add(new QuestDocument(quest, savedId != null ? QuestDocument.Clone(disk[savedId]) : null, savedId)
            {
                Deleted = entry.Deleted,
            });
        }
        foreach (var (id, saved) in disk)
            if (!claimed.Contains(id))
                Documents.Add(new QuestDocument(QuestDocument.Clone(saved), QuestDocument.Clone(saved), id) { Deleted = true });

        SelectMany(snapshot.SelectedQuests);
        if (snapshot.SelectedLink is { } link) _selectedLink = link;
        _restoring = true;
        Recompile();
        _restoring = false;
        return true;

        string? Claim(string? id) => id != null && disk.ContainsKey(id) && claimed.Add(id) ? id : null;
    }

    private QuestEditSnapshot Capture(string text) => new(text,
        [.. Documents.Select(d => new QuestEditSnapshot.Entry(QuestDocument.Clone(d.Quest), d.SavedId, d.Deleted))],
        [.. _selection], _selectedLink);

    // ---- edits

    /// <summary>Adds a quest at the absolute grid point and selects it. Its icon and its one obtain requirement name
    /// <paramref name="icon"/> (default stone), so it compiles as it is; its title is <paramref name="title"/>, default
    /// "New Quest". With a <paramref name="parent"/>, it already depends on that quest.</summary>
    public string AddQuest(Vector2 grid, string? icon = null, string? title = null, string? parent = null)
    {
        var id = UniqueId("quest");
        icon ??= DefaultIcon;
        var position = Snap(grid, true);
        Documents.Add(new QuestDocument(new QuestDefinition
        {
            Id = id,
            Title = title ?? "New Quest",
            Icon = icon,
            Pos = [position.X, position.Y],
            Parents = parent != null && Find(parent) != null ? [parent] : null,
            Requirements = [new QuestRequirementDefinition { Type = "obtain", Item = icon }],
        }, null, null));
        // Recorded before the selection moves to the new quest, so an undo restores the selection it was added under
        Recompile();
        Select(id);
        return id;
    }

    /// <summary>Removes the quest and every parent link to it.</summary>
    public void DeleteQuest(string id) => DeleteQuests([id]);

    /// <summary>Removes the quests and every parent link to them as one change (one undo step); a quest with a file
    /// stays as a deletion until the next save. Returns how many were removed.</summary>
    public int DeleteQuests(IEnumerable<string> ids)
    {
        var doomed = ids.Where(id => Find(id) != null).ToHashSet(StringComparer.Ordinal);
        if (doomed.Count == 0) return 0;
        foreach (var id in doomed)
        {
            var document = Find(id)!;
            if (document.SavedId == null) Documents.Remove(document);
            else document.Deleted = true;
        }
        foreach (var document in Live)
        {
            if (document.Quest.Parents is not { } parents || !parents.Any(doomed.Contains)) continue;
            document.Quest.Parents = Shrink(parents.Where(p => !doomed.Contains(p)));
        }
        // Recorded before the selection follows, so an undo brings the deleted quests back selected
        Recompile();
        if (_selection.RemoveAll(doomed.Contains) > 0) Touch();
        return doomed.Count;
    }

    /// <summary>Moves the quest so its node sits at the absolute grid point.</summary>
    public void MoveQuest(string id, Vector2 grid, bool snap) =>
        MoveQuests(id, grid, new Dictionary<string, Vector2> { [id] = grid }, snap);

    /// <summary>Moves a group of quests as one change: the <paramref name="lead"/> (the dragged one) lands at
    /// <paramref name="leadTarget"/>, snapped like a single move, and every quest in <paramref name="grabbed"/> moves by
    /// the same amount from the position it was grabbed at, so the group keeps its layout exactly.</summary>
    public void MoveQuests(string lead, Vector2 leadTarget, IReadOnlyDictionary<string, Vector2> grabbed, bool snap)
    {
        if (Find(lead) == null || !grabbed.TryGetValue(lead, out var leadFrom)) return;
        var delta = Snap(leadTarget, snap) - leadFrom;
        foreach (var (id, from) in grabbed)
        {
            if (Find(id) is not { } document) continue;
            var position = from + delta;
            document.Quest.Pos = [position.X, position.Y];
        }
        Recompile();
    }

    /// <summary>Every quest linked to <paramref name="id"/> through parent links followed either way, the quest itself
    /// included: the quests a shift-drag moves together. A quest with no link to it is not part of it.</summary>
    public IReadOnlyList<string> ComponentOf(string id)
    {
        if (!Preview.TryGet(id, out var start)) return [];
        var seen = new HashSet<int> { start.Index };
        var queue = new Queue<int>([start.Index]);
        while (queue.TryDequeue(out var index))
        {
            var quest = Preview.Quests[index];
            foreach (var next in quest.Parents.Concat(quest.Children))
                if (seen.Add(next)) queue.Enqueue(next);
        }
        return [.. seen.Select(i => Preview.Quests[i].Id)];
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

    /// <summary>Renames a quest (its file follows on the next save) and every parent link to it. Returns why the new
    /// id is refused, else null.</summary>
    public string? RenameQuest(string id, string newId)
    {
        newId = newId.Trim();
        if (newId == id) return null;
        if (newId.Length == 0) return "A quest id cannot be blank.";
        if (newId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return $"'{newId}' cannot be a file name.";
        if (Find(newId) != null) return $"Quest id '{newId}' is already used.";
        if (Find(id) is not { } found) return "No such quest.";
        found.Quest.Id = newId;
        foreach (var document in Live)
        {
            if (document.Quest.Parents is not { } parents || !parents.Contains(id)) continue;
            document.Quest.Parents = [.. parents.Select(p => p == id ? newId : p)];
        }
        if (_selection.IndexOf(id) is var at and >= 0) _selection[at] = newId;
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
    public static int RevealOf(QuestDefinition quest) => quest.Reveal == null ? 0
        : Math.Max(0, QuestLoader.RevealNames.ToList().FindIndex(n => string.Equals(n, quest.Reveal, StringComparison.OrdinalIgnoreCase)));

    public void ToggleReadable(string id) => Edit(id, q => q.Readable = q.Readable == false ? null : false);

    public void ToggleAnyOf(string id) => Edit(id, q => q.AnyOf = q.AnyOf == true ? null : true);

    public void AddRequirement(string id) =>
        Edit(id, q => (q.Requirements ??= []).Add(new QuestRequirementDefinition
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
    public static string TargetOf(QuestRequirementDefinition requirement) =>
        requirement.Tag is { } tag ? TagPrefix + tag
            : requirement.Item ?? requirement.Block ?? requirement.Entity ?? "";

    /// <summary>Whether the requirement's type names a tag-capable item (obtain or craft).</summary>
    public static bool TakesItem(QuestRequirementDefinition requirement) =>
        requirement.Type?.ToLowerInvariant() is "obtain" or "craft";

    // ---- saving and reverting

    /// <summary>Writes every changed quest file and deletes the files of deleted and renamed quests, unless the quests do
    /// not compile. Returns the status line.</summary>
    public string Save()
    {
        Recompile();
        if (Error != null) return "Not saved: " + Error;
        var stale = Documents.Where(d => d.SavedId != null && (d.Deleted || d.SavedId != d.Id))
            .Select(d => d.SavedId!).Distinct().ToList();
        var dirty = Live.Where(d => d.Dirty).ToList();
        if (stale.Count == 0 && dirty.Count == 0) return "Nothing to save";
        Directory.CreateDirectory(Folder);
        // Deletions first: a quest may have been renamed to a name another quest just gave up
        foreach (var id in stale) File.Delete(QuestDocument.PathOf(Folder, id));
        foreach (var document in dirty)
        {
            QuestWriter.Save(QuestDocument.PathOf(Folder, document.Id), document.Quest);
            document.MarkSaved();
        }
        Documents.RemoveAll(d => d.Deleted);
        Recompile();
        var written = dirty.Count == 1 ? $"{dirty[0].Id}.json" : $"{dirty.Count} quests";
        return stale.Count == 0 ? $"Saved {written}" : $"Saved {written}, removed {stale.Count} old file(s)";
    }

    /// <summary>Writes only this quest's file (and removes its old file after a rename). Refused when the files on disk
    /// would then not load (a rename its saved children still name, a link to a quest that is not saved yet, a name
    /// whose old file another quest still holds): save all instead. Returns the status line.</summary>
    public string SaveQuest(string id)
    {
        if (Find(id) is not { } document) return "No such quest.";
        if (!document.Dirty) return $"{id} has no unsaved edits";

        // The quests as the disk would hold them: every file as it is, this quest's old one gone and its new one written
        var onDisk = new List<(QuestDefinition Quest, string Path)>();
        foreach (var other in Documents)
        {
            if (other == document || other.SavedId == null) continue;
            var saved = QuestDocument.Clone(other.Saved!);
            saved.Id = other.SavedId;
            onDisk.Add((saved, QuestDocument.PathOf(Folder, other.SavedId)));
        }
        onDisk.Add((QuestDocument.Clone(document.Quest), QuestDocument.PathOf(Folder, id)));
        try
        {
            QuestLoader.Compile(onDisk);
        }
        catch (InvalidOperationException e)
        {
            return $"Cannot save {id} on its own: {e.Message} Save all instead.";
        }

        Directory.CreateDirectory(Folder);
        if (document.SavedId is { } old && old != id) File.Delete(QuestDocument.PathOf(Folder, old));
        QuestWriter.Save(QuestDocument.PathOf(Folder, id), document.Quest);
        document.MarkSaved();
        Recompile();
        return $"Saved {id}.json";
    }

    /// <summary>Puts the quest back as its file holds it (its id too, with every link to it), or removes a quest that
    /// has no file. Returns the status line.</summary>
    public string RevertQuest(string id)
    {
        if (Find(id) is not { } document) return "No such quest.";
        if (document.Saved == null || document.SavedId == null)
        {
            DeleteQuest(id);
            return $"Removed {id}, which was never saved";
        }
        var original = document.SavedId;
        if (RenameQuest(id, original) is { } refused) return $"Cannot revert {id}: {refused}";
        document.Revert();
        Recompile();
        return $"Reverted {original}";
    }

    /// <summary>Drops every unsaved edit: the quests are read from disk again. It is a recorded change, so
    /// <see cref="Undo"/> brings the edits back.</summary>
    public string RevertAll()
    {
        Documents.Clear();
        Documents.AddRange(ReadFolder(Folder));
        PickRequirement = -1;
        _selection.RemoveAll(id => Find(id) == null);
        Recompile();
        return "Reverted every quest to the files on disk";
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

    /// <summary>Rebuilds <see cref="Preview"/>, <see cref="Error"/> and what differs from disk from the documents, and
    /// records an undo step when the quests changed.</summary>
    public void Recompile()
    {
        var live = Live.ToList();
        Preview = BuildPreview(live);
        var text = new StringBuilder();
        foreach (var document in Documents)
        {
            var formatted = QuestWriter.Format(document.Quest);
            if (!document.Deleted) text.Append(document.Id).Append('\n').Append(formatted).Append('\0');
            document.Dirty = document.Deleted
                ? document.SavedId != null
                : document.Saved is not { } saved || document.SavedId != document.Id || formatted != QuestWriter.Format(saved);
        }
        try
        {
            QuestLoader.Compile([.. live.Select(d => (d.Quest, QuestDocument.PathOf(Folder, d.Id)))]);
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
        _history.Observe(all, () => Capture(all), Gesture, [.. _selection], _selectedLink);
    }

    private void Edit(string id, Action<QuestDefinition> edit)
    {
        if (Find(id) is not { } found) return;
        edit(found.Quest);
        Recompile();
    }

    private void EditRequirement(string id, int index, Action<QuestDefinition, QuestRequirementDefinition> edit) =>
        Edit(id, q =>
        {
            if (q.Requirements is not { } requirements || index < 0 || index >= requirements.Count) return;
            edit(q, requirements[index]);
        });

    private static void ApplyTarget(QuestRequirementDefinition requirement, string target)
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

    // A fresh id that names no quest and no file waiting to be deleted
    private string UniqueId(string stem)
    {
        var n = 1;
        while (Documents.Any(d => d.Id == $"{stem}_{n}" || d.SavedId == $"{stem}_{n}")) n++;
        return $"{stem}_{n}";
    }

    private static string[]? Shrink(IEnumerable<string> parents) => parents.ToArray() is { Length: > 0 } left ? left : null;

    private static Vector2 Snap(Vector2 value, bool snap) => snap
        ? new Vector2(MathF.Round(value.X / SnapStep) * SnapStep, MathF.Round(value.Y / SnapStep) * SnapStep)
        : value;

    // Every quest as it would draw, however broken: duplicate ids after the first, unknown parents and unresolved icons
    // are skipped rather than refused, so the canvas never goes blank mid-edit
    private static QuestBook BuildPreview(IReadOnlyList<QuestDocument> live)
    {
        var entries = new List<QuestDefinition>();
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var document in live)
            if (indexById.TryAdd(document.Id, entries.Count))
                entries.Add(document.Quest);

        var parents = entries.Select(e => (e.Parents ?? [])
            .Select(p => indexById.GetValueOrDefault(p, -1)).Where(p => p >= 0).Distinct().ToArray()).ToArray();
        var children = entries.Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < entries.Count; i++)
        foreach (var p in parents[i])
            children[p].Add(i);

        var quests = new QuestType[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var quest = entries[i];
            quests[i] = new QuestType
            {
                Index = i,
                Id = quest.Id!,
                Title = string.IsNullOrWhiteSpace(quest.Title) ? quest.Id! : quest.Title,
                Description = quest.Description ?? "",
                IconId = quest.Icon != null && ContentFiles.TryResolve(quest.Icon, out var icon) ? icon : (ushort)0,
                Position = quest.Pos is { Length: 2 } p ? new Vector2(p[0], p[1]) : Vector2.Zero,
                Shape = quest.Shape?.ToLowerInvariant() switch
                {
                    "hex" => QuestShape.Hex,
                    "circle" => QuestShape.Circle,
                    _ => parents[i].Length == 0 ? QuestShape.Hex : QuestShape.Circle,
                },
                Reveal = (QuestReveal)RevealOf(quest),
                Readable = quest.Readable ?? true,
                AnyOf = quest.AnyOf ?? false,
                Parents = parents[i],
                Children = children[i],
                Requirements = [],
            };
        }
        return new QuestBook(quests);
    }
}
