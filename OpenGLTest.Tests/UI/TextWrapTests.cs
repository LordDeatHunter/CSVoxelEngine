using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.UI;
using Xunit;

namespace VoxelEngine.Tests.UI;

public sealed class TextWrapTests
{
    private const string Status = "Need 3 more Copper Pickaxe Head (0/3 on hand)";
    private const string Label = "2. Any log  x4/craft";
    private static readonly string LongWord = new('q', 90);

    private static readonly string[] Texts = [Status, Label, LongWord];
    private static readonly float[] Widths = [40f, 95f, 150f, 400f];
    private static readonly TextMeasure[] Measures = [Mono, Proportional];

    private static float Mono(ReadOnlySpan<char> s) => s.Length * 10f;

    private static float Proportional(ReadOnlySpan<char> s)
    {
        float w = 0f;
        foreach (var c in s)
            w += c switch
            {
                'W' or 'M' or 'm' => 18f,
                'i' or 'l' or '.' => 5f,
                _ => 10f,
            };
        return w;
    }

    private static List<string> Wrap(string? text, float width, TextMeasure measure)
    {
        var lines = new List<string>();
        TextWrap.Wrap(text, width, measure, lines);
        return lines;
    }

    private static string[] Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<(string Text, float Width, TextMeasure Measure)> Sweep() =>
        from t in Texts from w in Widths from m in Measures select (t, w, m);

    [Fact]
    public void EveryLineFitsTheWidth()
    {
        foreach (var (text, width, measure) in Sweep())
            foreach (var line in Wrap(text, width, measure))
            {
                Assert.True(measure(line) <= width || line.Length == 1, $"'{line}' overflows {width}");
                Assert.True(line.Length <= TextWrap.MaxLineChars);
            }
    }

    [Fact]
    public void NothingIsLostOrReordered()
    {
        foreach (var (text, width, measure) in Sweep())
        {
            var lines = Wrap(text, width, measure);
            Assert.Equal(text.Replace(" ", ""), string.Concat(lines).Replace(" ", ""));
            foreach (var line in lines)
            {
                Assert.False(line.StartsWith(' '));
                Assert.False(line.EndsWith(' '));
            }

            if (Words(text).All(w => w.Length <= TextWrap.MaxLineChars && measure(w) <= width))
                Assert.Equal(Words(text), lines.SelectMany(Words));
        }
    }

    [Fact]
    public void FittingTextIsOneLine()
    {
        const string text = "  " + Label + " ";
        var width = Mono(text) + 1f;
        var lines = Wrap(text, width, Mono);
        Assert.Equal([text.Trim()], lines);
    }

    [Fact]
    public void WordsBreakOnlyWhenTheyAloneOverflow()
    {
        foreach (var measure in Measures)
        {
            // Every word of the status fits 150 alone, so each line is a run of whole words.
            var words = Words(Status);
            Assert.All(words, w => Assert.True(measure(w) <= 150f));
            var lines = Wrap(Status, 150f, measure);
            Assert.True(lines.Count > 1);
            int next = 0;
            foreach (var line in lines)
            {
                var lineWords = Words(line);
                Assert.Equal(words.Skip(next).Take(lineWords.Length), lineWords);
                Assert.Contains(line, Status);
                next += lineWords.Length;
            }
            Assert.Equal(words.Length, next);

            // One over-long word between short ones spans several lines, each piece fitting.
            var text = "ab " + LongWord + " cd";
            var broken = Wrap(text, 95f, measure);
            var pieces = broken.Where(l => l.Contains('q')).ToList();
            Assert.True(pieces.Count > 1);
            Assert.All(broken, l => Assert.True(measure(l) <= 95f || l.Length == 1));
            Assert.Equal(LongWord, string.Concat(pieces.Select(p => new string(p.Where(c => c == 'q').ToArray()))));
        }
    }

    [Fact]
    public void LongRunsRespectTheGlyphCap()
    {
        var text = new string('a', 200);
        var lines = Wrap(text, float.MaxValue, Mono);
        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(l.Length <= TextWrap.MaxLineChars));
        Assert.Equal(text, string.Concat(lines));
    }

    [Fact]
    public void WideningNeverAddsLines()
    {
        int previous = int.MaxValue;
        for (float width = 10f; width <= 600f; width += 7f)
        {
            int count = Wrap(Status, width, Proportional).Count;
            Assert.True(count <= previous, $"width {width} gave {count} lines after {previous}");
            previous = count;
        }
    }

    [Fact]
    public void EmptyTextAppendsNothing()
    {
        foreach (var text in new[] { null, "", "   " })
        {
            var lines = new List<string> { "keep" };
            TextWrap.Wrap(text, 100f, Mono, lines);
            Assert.Equal(["keep"], lines);
        }
    }
}
