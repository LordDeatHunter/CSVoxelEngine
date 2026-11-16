using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Entities;

namespace VoxelEngine.DevStudio;

/// <summary>
/// The QUESTS tab's icon grid, GL-free: every item, block and entity a quest may name, and the ones the search and the
/// kind filters let through (<see cref="Visible"/>). A query matches an entry's display name or its quest name
/// (<see cref="IconEntry.Key"/>), ignoring case; an empty query matches everything of a shown kind.
/// </summary>
public sealed class QuestIconPicker
{
    private readonly bool[] _shown = [true, true, true];

    public QuestIconPicker(IEnumerable<IconEntry> all)
    {
        All = [.. all];
        Visible = All;
    }

    /// <summary>The grid's entries for the content loaded: the studio's items and blocks in its grid order, then every
    /// registered entity by display name.</summary>
    public static QuestIconPicker FromContent(IEnumerable<DevStudioUi.GridEntry> itemsAndBlocks) =>
        new(itemsAndBlocks
            .Select(e => new IconEntry(e.IsBlock ? IconKind.Block : IconKind.Item, e.Id, e.Name, QuestEditorState.ContentName(e.Id)))
            .Concat(EntityRegistry.ByName.Values
                .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(t => new IconEntry(IconKind.Entity, 0, t.DisplayName, t.Name))));

    public IReadOnlyList<IconEntry> All { get; }

    /// <summary>The entries the query and the filters let through, in grid order.</summary>
    public IReadOnlyList<IconEntry> Visible { get; private set; }

    public string Query { get; private set; } = "";

    public bool Shows(IconKind kind) => _shown[(int)kind];

    public void Toggle(IconKind kind)
    {
        _shown[(int)kind] = !_shown[(int)kind];
        Refresh();
    }

    public void SetQuery(string query)
    {
        Query = query.Trim();
        Refresh();
    }

    /// <summary>Whether <paramref name="entry"/> matches <paramref name="query"/> by its display or quest name.</summary>
    public static bool Matches(IconEntry entry, string query) =>
        query.Length == 0
        || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.Key.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void Refresh() => Visible = [.. All.Where(e => Shows(e.Kind) && Matches(e, Query))];
}
