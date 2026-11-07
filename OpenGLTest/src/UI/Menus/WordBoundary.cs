using System;

namespace VoxelEngine.UI.Menus;

/// <summary>
/// GL-free word rule of a text field (Ctrl+arrows, Ctrl+Backspace/Delete): whitespace is skipped,
/// then one run of word characters (letters, digits, underscore) or one run of punctuation is
/// crossed, so "foo.bar" has three stops.
/// </summary>
public static class WordBoundary
{
    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The start of the word at or before <paramref name="index"/> (the previous stop).</summary>
    public static int Left(string text, int index)
    {
        var i = Math.Clamp(index, 0, text.Length);
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        if (i == 0) return 0;
        var word = IsWord(text[i - 1]);
        while (i > 0 && !char.IsWhiteSpace(text[i - 1]) && IsWord(text[i - 1]) == word) i--;
        return i;
    }

    /// <summary>The end of the word at or after <paramref name="index"/> (the next stop).</summary>
    public static int Right(string text, int index)
    {
        var i = Math.Clamp(index, 0, text.Length);
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i == text.Length) return i;
        var word = IsWord(text[i]);
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && IsWord(text[i]) == word) i++;
        return i;
    }
}
