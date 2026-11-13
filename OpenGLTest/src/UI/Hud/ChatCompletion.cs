using System;
using VoxelEngine.Commands;
using VoxelEngine.UI.Menus;

namespace VoxelEngine.UI.Hud;

/// <summary>
/// GL-free autocomplete state of the chat input while a command line is typed: the
/// <see cref="CommandDispatcher.Suggest"/> result for the word at the caret, which completion Tab last put in, and
/// the ghost text (the untyped rest of the best completion, drawn after the caret). <see cref="Sync"/> recomputes
/// only when the text or the caret moved, so it can run every frame and picks up typing, pastes, clicks and history
/// recall alike; <see cref="Cycle"/> puts the next completion in and records that state as synced, so repeated Tabs
/// walk the same list instead of completing the completion.
/// </summary>
public sealed class ChatCompletion
{
    private string _syncedText = "";
    private int _syncedCaret = -1;
    // The end of the text a completion replaces: the typed word's end, then the inserted completion's
    private int _replaceEnd;

    /// <summary>What is offered for the word at the caret (<see cref="CommandSuggestions.None"/> on a plain line).</summary>
    public CommandSuggestions Current { get; private set; } = CommandSuggestions.None;

    /// <summary>The completion Tab last put in, or -1 before the first Tab since the line changed.</summary>
    public int Selected { get; private set; } = -1;

    /// <summary>Recomputes the offer when <paramref name="field"/>'s text or caret changed since the last sync or
    /// cycle, for the source <paramref name="source"/> builds (only then); the selection starts over.</summary>
    public void Sync(TextFieldState field, CommandDispatcher dispatcher, Func<CommandSource> source)
    {
        if (field.Caret == _syncedCaret && field.Text == _syncedText) return;
        Current = dispatcher.Suggest(field.Text, field.Caret, source());
        Selected = -1;
        var wordEnd = field.Text.IndexOf(' ', Math.Min(field.Caret, field.Text.Length));
        _replaceEnd = wordEnd < 0 ? field.Text.Length : wordEnd;
        Remember(field);
    }

    /// <summary>
    /// Puts the next completion (<paramref name="step"/> 1, Tab) or the previous one (-1, Shift+Tab) in place of the
    /// word being completed, the caret after it; the first Tab takes the best. False when nothing is offered.
    /// </summary>
    public bool Cycle(TextFieldState field, int step)
    {
        var count = Current.Items.Count;
        if (count == 0) return false;
        Selected = Selected < 0 ? (step >= 0 ? 0 : count - 1) : ((Selected + step) % count + count) % count;
        var insert = Current.Items[Selected].Insert;
        var text = field.Text;
        var start = Math.Min(Current.ReplaceStart, text.Length);
        var end = Math.Clamp(_replaceEnd, start, text.Length);
        field.Set(text[..start] + insert + text[end..]);
        field.SetCaret(start + insert.Length, extend: false);
        _replaceEnd = field.Caret;
        Remember(field);
        return true;
    }

    /// <summary>The rest of the best (or the selected) completion past what is typed of the word, when the caret is at
    /// the end of the line and the completion continues the word; empty otherwise.</summary>
    public string Ghost(TextFieldState field)
    {
        if (Current.Items.Count == 0 || field.Caret != field.Text.Length) return "";
        var start = Math.Min(Current.ReplaceStart, field.Text.Length);
        var typed = field.Text.AsSpan(start);
        var best = Current.Items[Math.Max(Selected, 0)].Insert;
        return best.Length > typed.Length && best.AsSpan().StartsWith(typed, StringComparison.OrdinalIgnoreCase)
            ? best[typed.Length..]
            : "";
    }

    /// <summary>Forgets the offer: the chat closed or the line was sent.</summary>
    public void Reset()
    {
        Current = CommandSuggestions.None;
        Selected = -1;
        _syncedText = "";
        _syncedCaret = -1;
        _replaceEnd = 0;
    }

    private void Remember(TextFieldState field)
    {
        _syncedText = field.Text;
        _syncedCaret = field.Caret;
    }
}
