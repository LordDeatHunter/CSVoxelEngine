using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Quests;
using VoxelEngine.Saves;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Quests;

/// <summary>
/// One player's quest progress against a <see cref="QuestBook"/>: which quests are complete and how far
/// each requirement of the rest has come. The rules:
/// <list type="bullet">
/// <item>a quest is unlocked once every parent is complete (a root always is), and only an unlocked quest
/// makes progress or completes;</item>
/// <item>an <c>obtain</c> requirement reads the inventory only when an item is gained
/// (<see cref="Inventory.Gained"/>, through <see cref="Bind"/>) and once when its quest unlocks, never per tick;</item>
/// <item>a quest completes when every requirement has reached its count, raising <see cref="Completed"/>
/// exactly once, and its newly unlocked children check their <c>obtain</c> requirements at once (which can
/// cascade).</item>
/// </list>
/// The authoritative world (singleplayer, or the server per player) feeds the events; a client's log is a
/// mirror that only <see cref="Import"/> writes. GL-free; main thread only.
/// </summary>
public sealed class QuestLog
{
    private readonly bool[] _complete;
    private readonly int[][] _progress;
    private Func<ushort, int>? _held;

    public QuestLog(QuestBook book)
    {
        Book = book;
        _complete = new bool[book.Quests.Count];
        _progress = book.Quests.Select(q => new int[q.Requirements.Count]).ToArray();
    }

    public QuestBook Book { get; }

    /// <summary>Raised once per quest, when it completes.</summary>
    public event Action<QuestType>? Completed;

    /// <summary>Set by every change (progress or completion); a server clears it once it has sent the player's state.</summary>
    public bool Changed { get; set; }

    public bool IsComplete(QuestType quest) => _complete[quest.Index];

    /// <summary>Whether every parent is complete, so the quest shows and makes progress.</summary>
    public bool IsUnlocked(QuestType quest)
    {
        foreach (var parent in quest.Parents)
            if (!_complete[parent]) return false;
        return true;
    }

    /// <summary>Whether the quest book draws the quest: complete, or unlocked and still open.</summary>
    public bool IsVisible(QuestType quest) => _complete[quest.Index] || IsUnlocked(quest);

    /// <summary>How far requirement <paramref name="requirement"/> of the quest has come, capped at its count
    /// (the full count once the quest is complete).</summary>
    public int ProgressOf(QuestType quest, int requirement) =>
        _complete[quest.Index] ? quest.Requirements[requirement].Count : _progress[quest.Index][requirement];

    /// <summary>
    /// Makes <paramref name="inventory"/> this log's source of held counts and listens to its gains and
    /// crafts. Bound after a newcomer's starter kit is handed out, so the kit itself completes nothing.
    /// </summary>
    public void Bind(Inventory inventory)
    {
        _held = inventory.CountFor;
        inventory.Gained += OnGained;
        inventory.Crafted += OnCrafted;
    }

    /// <summary>An item entered the bound inventory: every open <c>obtain</c> requirement on it rereads the held count.</summary>
    public void OnGained(ushort id)
    {
        if (_held == null) return;
        var held = _held(id);
        Advance(r => r.Counts(QuestRequirementKind.Obtain, id), _ => held);
    }

    public void OnCrafted(ushort id, int count) => Add(QuestRequirementKind.Craft, id, count);

    public void OnBroke(ushort blockId) => Add(QuestRequirementKind.Break, blockId, 1);

    public void OnPlaced(ushort blockId) => Add(QuestRequirementKind.Place, blockId, 1);

    public void OnKilled(string entity) => Advance(r => r.CountsKill(entity), have => have + 1);

    private void Add(QuestRequirementKind kind, ushort id, int count)
    {
        if (count <= 0) return;
        Advance(r => r.Counts(kind, id), have => have + count);
    }

    // Moves every matching requirement of the quests open and unlocked before the event to next(have)
    // (never down, never past its count), then completes the quests that are done. The open set is taken
    // first, so a quest the event itself unlocks only counts what is held (CheckHeld), not the event again.
    private void Advance(Func<QuestRequirement, bool> matches, Func<int, int> next)
    {
        var open = Book.Quests.Where(q => !_complete[q.Index] && IsUnlocked(q)).ToList();
        foreach (var quest in open)
        {
            var progress = _progress[quest.Index];
            var moved = false;
            for (var r = 0; r < quest.Requirements.Count; r++)
            {
                var requirement = quest.Requirements[r];
                if (!matches(requirement)) continue;
                var value = Math.Max(progress[r], Math.Min(requirement.Count, next(progress[r])));
                if (value == progress[r]) continue;
                progress[r] = value;
                moved = true;
            }
            if (!moved) continue;
            Changed = true;
            if (!_complete[quest.Index]) TryComplete(quest);
        }
    }

    private void TryComplete(QuestType quest)
    {
        var progress = _progress[quest.Index];
        for (var r = 0; r < quest.Requirements.Count; r++)
            if (progress[r] < quest.Requirements[r].Count) return;

        _complete[quest.Index] = true;
        Changed = true;
        Completed?.Invoke(quest);
        foreach (var child in quest.Children)
        {
            var unlocked = Book.Quests[child];
            if (!_complete[child] && IsUnlocked(unlocked)) CheckHeld(unlocked);
        }
    }

    // A quest that just unlocked counts what the player already holds toward its obtain requirements
    private void CheckHeld(QuestType quest)
    {
        if (_held == null) return;
        var progress = _progress[quest.Index];
        for (var r = 0; r < quest.Requirements.Count; r++)
        {
            var requirement = quest.Requirements[r];
            if (requirement.Kind != QuestRequirementKind.Obtain) continue;
            progress[r] = Math.Max(progress[r], Math.Min(requirement.Count, _held(requirement.Id)));
        }
        TryComplete(quest);
    }

    /// <summary>Completes a quest and its unlock cascade as if its requirements were met (the scenario seam);
    /// false when it is already complete or still locked.</summary>
    public bool ForceComplete(QuestType quest)
    {
        if (_complete[quest.Index] || !IsUnlocked(quest)) return false;
        for (var r = 0; r < quest.Requirements.Count; r++)
            _progress[quest.Index][r] = quest.Requirements[r].Count;
        TryComplete(quest);
        return true;
    }

    /// <summary>The progress by quest id (completed ids, and the counts of every open quest that has any).</summary>
    public QuestSaveData Export()
    {
        var data = new QuestSaveData { Completed = [], Progress = [] };
        foreach (var quest in Book.Quests)
        {
            if (_complete[quest.Index]) data.Completed.Add(quest.Id);
            else if (_progress[quest.Index].Any(v => v > 0)) data.Progress[quest.Id] = [.. _progress[quest.Index]];
        }
        return data;
    }

    /// <summary>
    /// Replaces the progress with <paramref name="data"/> (a save, or a server's snapshot on a client). An id
    /// this book does not know is dropped with a warning; counts are clamped to the requirement counts. Raises
    /// no <see cref="Completed"/>.
    /// </summary>
    public void Import(QuestSaveData data)
    {
        Array.Clear(_complete);
        foreach (var progress in _progress) Array.Clear(progress);

        foreach (var id in data.Completed ?? [])
        {
            if (Book.TryGet(id, out var quest)) _complete[quest.Index] = true;
            else Log.Warn($"[Quests] Dropped unknown completed quest '{id}'.");
        }
        foreach (var (id, counts) in data.Progress ?? [])
        {
            if (!Book.TryGet(id, out var quest))
            {
                Log.Warn($"[Quests] Dropped progress of unknown quest '{id}'.");
                continue;
            }
            var progress = _progress[quest.Index];
            for (var r = 0; r < Math.Min(progress.Length, counts?.Length ?? 0); r++)
                progress[r] = Math.Clamp(counts![r], 0, quest.Requirements[r].Count);
        }
        Changed = true;
    }

    /// <summary>The quests in <paramref name="data"/>'s completed list this log has not completed yet (a
    /// client's toast for a server snapshot).</summary>
    public List<QuestType> NewlyCompletedIn(QuestSaveData data)
    {
        var fresh = new List<QuestType>();
        foreach (var id in data.Completed ?? [])
            if (Book.TryGet(id, out var quest) && !_complete[quest.Index])
                fresh.Add(quest);
        return fresh;
    }
}
