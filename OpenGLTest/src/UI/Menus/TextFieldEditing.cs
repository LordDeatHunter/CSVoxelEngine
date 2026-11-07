namespace VoxelEngine.UI.Menus;

/// <summary>The editing keys a text field reacts to, without any windowing-library type.</summary>
public enum EditKey { Left, Right, Up, Down, Home, End, Backspace, Delete, A, C, X, V }

/// <summary>The clipboard a field copies to and pastes from.</summary>
public interface IClipboard
{
    string Text { get; set; }
}

/// <summary>
/// GL-free key rules shared by every text field: arrows and Home/End (Ctrl for words, Shift to
/// select), Backspace and Delete (Ctrl for words), and Ctrl+A/C/X/V. The app maps its keyboard
/// events onto <see cref="EditKey"/> once, so the menu, the chat and the search fields cannot drift.
/// </summary>
public static class TextFieldEditing
{
    /// <summary>
    /// Applies <paramref name="key"/> to <paramref name="field"/>. Up and Down jump to the start
    /// and end only when <paramref name="upDownJump"/> (the fields where they mean nothing else).
    /// Returns whether the key was consumed; <paramref name="changedText"/> says the text itself
    /// changed (the caller resets whatever derives from the query).
    /// </summary>
    public static bool Handle(TextFieldState field, EditKey key, bool ctrl, bool shift, IClipboard? clipboard,
        bool upDownJump, out bool changedText)
    {
        changedText = false;
        switch (key)
        {
            case EditKey.Left:
                field.MoveCaret(ctrl ? CaretMove.WordLeft : CaretMove.Left, shift);
                return true;
            case EditKey.Right:
                field.MoveCaret(ctrl ? CaretMove.WordRight : CaretMove.Right, shift);
                return true;
            case EditKey.Home:
                field.MoveCaret(CaretMove.Start, shift);
                return true;
            case EditKey.End:
                field.MoveCaret(CaretMove.End, shift);
                return true;
            case EditKey.Up when upDownJump:
                field.MoveCaret(CaretMove.Start, shift);
                return true;
            case EditKey.Down when upDownJump:
                field.MoveCaret(CaretMove.End, shift);
                return true;
            case EditKey.Backspace:
                changedText = ctrl ? field.DeleteWord(true) : field.Backspace();
                return true;
            case EditKey.Delete:
                changedText = ctrl ? field.DeleteWord(false) : field.Delete();
                return true;
            case EditKey.A when ctrl:
                field.SelectAll();
                return true;
            case EditKey.C when ctrl:
                if (clipboard != null && field.HasSelection) clipboard.Text = field.SelectedText;
                return true;
            case EditKey.X when ctrl:
                if (clipboard != null && field.HasSelection)
                {
                    clipboard.Text = field.SelectedText;
                    changedText = field.Backspace();
                }
                return true;
            case EditKey.V when ctrl:
                if (clipboard != null) changedText = field.Insert(clipboard.Text);
                return true;
            default:
                return false;
        }
    }
}
