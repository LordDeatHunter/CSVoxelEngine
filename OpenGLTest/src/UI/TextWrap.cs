using System;
using System.Collections.Generic;

namespace VoxelEngine.UI;

/// <summary>Width in canvas units of a run of text at some fixed draw scale. Injected so the wrap is
/// GL-free: UIRenderer binds it to BitmapFont.MeasureSize(text, scale).X, tests to a synthetic width.</summary>
public delegate float TextMeasure(ReadOnlySpan<char> text);

/// <summary>Greedy word wrap of UI text against an injected measure; GL-free.</summary>
public static class TextWrap
{
    /// <summary>Longest line ever emitted, so every line fits one DrawText run.</summary>
    public const int MaxLineChars = BitmapFont.MaxLayoutChars;

    /// <summary>Greedy word wrap of <paramref name="text"/> into lines no wider than <paramref name="maxWidth"/>
    /// under <paramref name="measure"/> and no longer than MaxLineChars, appended to <paramref name="lines"/>.
    /// Words (maximal runs of non-space characters) are kept whole and joined by the source's own spaces;
    /// a word that alone exceeds the width or the char cap is broken into the longest fitting prefixes,
    /// at least one character per line so the loop always progresses. Lines carry no leading or trailing
    /// spaces. Null, empty or all-space text appends nothing.</summary>
    public static void Wrap(string? text, float maxWidth, TextMeasure measure, List<string> lines)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var span = text.AsSpan();
        // A line is always a contiguous slice [lineStart, lineEnd) of the source, so the spaces
        // between its words survive exactly as written; lineStart < 0 means no line is open.
        int lineStart = -1, lineEnd = -1;
        int i = 0;
        while (i < span.Length)
        {
            if (span[i] == ' ')
            {
                i++;
                continue;
            }

            int wordStart = i;
            while (i < span.Length && span[i] != ' ')
                i++;
            int wordEnd = i;

            if (lineStart >= 0 && Fits(span[lineStart..wordEnd], maxWidth, measure))
            {
                lineEnd = wordEnd;
                continue;
            }

            if (lineStart >= 0)
                lines.Add(text.Substring(lineStart, lineEnd - lineStart));

            if (Fits(span[wordStart..wordEnd], maxWidth, measure))
            {
                lineStart = wordStart;
                lineEnd = wordEnd;
                continue;
            }

            // The word alone overflows: break it into the longest fitting prefixes. The final
            // piece stays open so the next word may still join it.
            int pos = wordStart;
            while (true)
            {
                int max = Math.Min(wordEnd - pos, MaxLineChars);
                int k = max;
                while (k > 1 && !Fits(span.Slice(pos, k), maxWidth, measure))
                    k--;
                if (pos + k >= wordEnd)
                {
                    lineStart = pos;
                    lineEnd = wordEnd;
                    break;
                }
                lines.Add(text.Substring(pos, k));
                pos += k;
            }
        }

        if (lineStart >= 0)
            lines.Add(text.Substring(lineStart, lineEnd - lineStart));
    }

    private static bool Fits(ReadOnlySpan<char> candidate, float maxWidth, TextMeasure measure) =>
        candidate.Length <= MaxLineChars && measure(candidate) <= maxWidth;
}
