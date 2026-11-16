using System;
using System.Collections.Generic;

namespace VoxelEngine.DevStudio;

/// <summary>
/// The quest editor's undo and redo, GL-free. It watches the editor after every change (<see cref="Observe"/>): when
/// the graphs' text differs from the current entry, the current entry goes on the undo stack and the redo stack is
/// cleared, so a new edit after an undo drops everything that was undone. A change with the same gesture key as the
/// last recorded one (one typed field, one drag) replaces the current entry instead, so a whole gesture is one step.
/// A change that leaves the text alone (a refused edit, a save, a selection) records nothing, but keeps the current
/// entry's selection up to date, so an undo restores the selection the change was made under.
/// </summary>
public sealed class QuestEditHistory
{
    /// <summary>The most undo steps kept; the oldest goes first.</summary>
    public const int Capacity = 200;

    private readonly List<QuestEditSnapshot> _undo = [];
    private readonly List<QuestEditSnapshot> _redo = [];
    private string? _gesture;

    /// <summary>The state the editor is in, or null before the first observation.</summary>
    public QuestEditSnapshot? Current { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Records the editor's state after a change. <paramref name="capture"/> builds the full snapshot and is
    /// called only when the text changed; <paramref name="gesture"/> is the key of the gesture in progress, or null.</summary>
    public void Observe(string text, Func<QuestEditSnapshot> capture, string? gesture,
        IReadOnlyList<string> selectedQuests, (string Child, string Parent)? selectedLink, int selectedGraph)
    {
        if (Current != null && Current.Text == text)
        {
            Touch(selectedQuests, selectedLink, selectedGraph);
            return;
        }
        var now = capture();
        if (Current != null && !(gesture != null && gesture == _gesture))
        {
            _undo.Add(Current);
            if (_undo.Count > Capacity) _undo.RemoveAt(0);
        }
        if (Current != null) _redo.Clear();
        Current = now;
        _gesture = gesture;
    }

    /// <summary>Keeps the current entry's selection up to date without recording a step.</summary>
    public void Touch(IReadOnlyList<string> selectedQuests, (string Child, string Parent)? selectedLink, int selectedGraph)
    {
        if (Current != null)
            Current = Current with { SelectedQuests = selectedQuests, SelectedLink = selectedLink, SelectedGraph = selectedGraph };
    }

    /// <summary>Steps back one entry and returns the state to restore, or null when there is none.</summary>
    public QuestEditSnapshot? Undo() => Step(_undo, _redo);

    /// <summary>Steps forward one undone entry and returns the state to restore, or null when there is none.</summary>
    public QuestEditSnapshot? Redo() => Step(_redo, _undo);

    private QuestEditSnapshot? Step(List<QuestEditSnapshot> from, List<QuestEditSnapshot> to)
    {
        if (from.Count == 0 || Current == null) return null;
        to.Add(Current);
        Current = from[^1];
        from.RemoveAt(from.Count - 1);
        // The next change starts its own step even if it continues the gesture it was undone from
        _gesture = null;
        return Current;
    }
}
