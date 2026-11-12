using System;

namespace VoxelEngine.UI.Menus;

/// <summary>Where <see cref="TextFieldState.MoveCaret"/> goes.</summary>
public enum CaretMove { Left, Right, WordLeft, WordRight, Start, End }

/// <summary>
/// GL-free contents of one single-line text field (a world name, a seed, the chat line, a search
/// query): the text, a caret and an optional selection. It knows nothing about Silk input: the
/// app feeds it the characters that arrive through the keyboard's character event and the
/// editing keys, so the typing rules are covered by tests.
/// </summary>
public sealed class TextFieldState(int maxLength)
{
    /// <summary>The most characters the field holds; inserts past it are refused or truncated.</summary>
    public int MaxLength { get; } = maxLength;

    public string Text { get; private set; } = "";

    public bool IsEmpty => Text.Length == 0;

    /// <summary>The insertion point, 0..<see cref="Text"/>.Length.</summary>
    public int Caret { get; private set; }

    /// <summary>The fixed end of the selection; equal to <see cref="Caret"/> when nothing is selected.</summary>
    public int Anchor { get; private set; }

    /// <summary>First visible character of a field wider than its box; owned by the draw.</summary>
    public int ViewStart { get; set; }

    public bool HasSelection => Anchor != Caret;

    public int SelectionStart => Math.Min(Anchor, Caret);

    public int SelectionEnd => Math.Max(Anchor, Caret);

    public string SelectedText => Text[SelectionStart..SelectionEnd];

    /// <summary>Whether the font can draw <paramref name="c"/>: printable ASCII (<see cref="BitmapFont.FirstChar"/> to <c>~</c>; 127 is DEL).</summary>
    private static bool IsTypable(char c) => c >= (char)BitmapFont.FirstChar && c <= '~';

    /// <summary>
    /// Inserts <paramref name="c"/> at the caret (replacing a selection) when it is a character the
    /// UI font can draw and the field has room. Enter, Tab and Backspace arriving through the
    /// character event as control characters are refused here rather than inserted.
    /// </summary>
    public bool TryAppend(char c)
    {
        if (!IsTypable(c) || Text.Length - (SelectionEnd - SelectionStart) >= MaxLength) return false;
        ReplaceSelection(c.ToString());
        return true;
    }

    /// <summary>
    /// Inserts <paramref name="text"/> as if typed (a paste): untypable characters are dropped and
    /// the rest is cut to the room left. False when nothing was inserted.
    /// </summary>
    public bool Insert(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var room = MaxLength - (Text.Length - (SelectionEnd - SelectionStart));
        var sb = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (sb.Length >= room) break;
            if (IsTypable(c)) sb.Append(c);
        }
        if (sb.Length == 0) return false;
        ReplaceSelection(sb.ToString());
        return true;
    }

    /// <summary>Deletes the selection, else the character before the caret; false when there was nothing to delete.</summary>
    public bool Backspace()
    {
        if (HasSelection) return DeleteSelection();
        if (Caret == 0) return false;
        Text = Text.Remove(Caret - 1, 1);
        Caret--;
        Anchor = Caret;
        return true;
    }

    /// <summary>Deletes the selection, else the character after the caret; false when there was nothing to delete.</summary>
    public bool Delete()
    {
        if (HasSelection) return DeleteSelection();
        if (Caret >= Text.Length) return false;
        Text = Text.Remove(Caret, 1);
        return true;
    }

    /// <summary>Deletes the selection, else a word before (<paramref name="backward"/>) or after the caret.</summary>
    public bool DeleteWord(bool backward)
    {
        if (HasSelection) return DeleteSelection();
        var edge = backward ? WordBoundary.Left(Text, Caret) : WordBoundary.Right(Text, Caret);
        if (edge == Caret) return false;
        Anchor = edge;
        return DeleteSelection();
    }

    /// <summary>
    /// Moves the caret. With <paramref name="extend"/> the anchor stays and the selection grows or
    /// shrinks; without it a selection collapses (Left/Right to its own edge) and the caret moves on.
    /// </summary>
    public void MoveCaret(CaretMove move, bool extend)
    {
        if (!extend && HasSelection && move is CaretMove.Left or CaretMove.Right)
        {
            Caret = Anchor = move == CaretMove.Left ? SelectionStart : SelectionEnd;
            return;
        }
        var target = move switch
        {
            CaretMove.Left => Caret - 1,
            CaretMove.Right => Caret + 1,
            CaretMove.WordLeft => WordBoundary.Left(Text, Caret),
            CaretMove.WordRight => WordBoundary.Right(Text, Caret),
            CaretMove.Start => 0,
            _ => Text.Length,
        };
        SetCaret(target, extend);
    }

    /// <summary>Puts the caret at <paramref name="index"/> (clamped), keeping the anchor when <paramref name="extend"/>; the mouse's entry point.</summary>
    public void SetCaret(int index, bool extend)
    {
        Caret = Math.Clamp(index, 0, Text.Length);
        if (!extend) Anchor = Caret;
    }

    public void SelectAll()
    {
        Anchor = 0;
        Caret = Text.Length;
    }

    public void Clear()
    {
        Text = "";
        Caret = Anchor = ViewStart = 0;
    }

    /// <summary>
    /// Replaces the contents with <paramref name="text"/> as if it had been typed, so a preset
    /// value (the current name on the edit page) obeys exactly the typing rules; the caret ends
    /// after it.
    /// </summary>
    public void Set(string? text)
    {
        Clear();
        Insert(text);
    }

    private bool DeleteSelection()
    {
        if (!HasSelection) return false;
        ReplaceSelection("");
        return true;
    }

    private void ReplaceSelection(string insert)
    {
        var start = SelectionStart;
        Text = Text[..start] + insert + Text[SelectionEnd..];
        Caret = Anchor = start + insert.Length;
    }
}
