using System;
using System.Collections.Generic;

namespace VoxelEngine.Commands;

/// <summary>
/// Orders completions for what is typed of the word: with nothing typed, all of them alphabetically; otherwise only
/// those containing it, a prefix match before a match further in, each tier by how alike the two strings are
/// (Sørensen–Dice over letter pairs), then alphabetically. Duplicates (a block and an item of one name) show once.
/// </summary>
internal static class SuggestionRanking
{
    internal static IReadOnlyList<Suggestion> Rank(List<Suggestion> items, string typed)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<(Suggestion Item, float Score)>(items.Count);
        foreach (var item in items)
        {
            if (!seen.Add(item.Insert)) continue;
            if (typed.Length > 0 && !item.Insert.Contains(typed, StringComparison.OrdinalIgnoreCase)) continue;
            kept.Add((item, Score(item.Insert, typed)));
        }
        kept.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : string.Compare(a.Item.Label, b.Item.Label, StringComparison.OrdinalIgnoreCase);
        });
        var ranked = new Suggestion[kept.Count];
        for (var i = 0; i < kept.Count; i++)
            ranked[i] = kept[i].Item;
        return ranked;
    }

    private static float Score(string candidate, string typed)
    {
        if (typed.Length == 0) return 0f;
        var tier = candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? 2f : 1f;
        return tier + Similarity(candidate.ToLowerInvariant(), typed.ToLowerInvariant());
    }

    // Twice the letter pairs the two share over the pairs they have together, 0..1
    private static float Similarity(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return a == b ? 1f : 0f;
        var pairs = new List<(char, char)>(a.Length - 1);
        for (var i = 0; i < a.Length - 1; i++)
            pairs.Add((a[i], a[i + 1]));
        var shared = 0;
        for (var i = 0; i < b.Length - 1; i++)
        {
            var at = pairs.IndexOf((b[i], b[i + 1]));
            if (at < 0) continue;
            pairs.RemoveAt(at);
            shared++;
        }
        return 2f * shared / (a.Length - 1 + b.Length - 1);
    }
}
