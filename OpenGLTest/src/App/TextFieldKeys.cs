using Silk.NET.Input;
using VoxelEngine.Platform;
using VoxelEngine.UI.Menus;

namespace VoxelEngine.App;

/// <summary>Maps the window's key events onto <see cref="TextFieldEditing"/> for whichever field has the focus: the one place that reads the Ctrl and Shift state and the clipboard. Main thread only.</summary>
internal sealed class TextFieldKeys(InputManager inputManager)
{
    private sealed class KeyboardClipboard(IKeyboard keyboard) : IClipboard
    {
        public string Text
        {
            get => keyboard.ClipboardText ?? "";
            set => keyboard.ClipboardText = value;
        }
    }

    internal bool CtrlHeld => Held(Key.ControlLeft, Key.ControlRight);

    internal bool ShiftHeld => Held(Key.ShiftLeft, Key.ShiftRight);

    private bool Held(Key a, Key b) =>
        inputManager.Keyboard is { } keyboard && (keyboard.IsKeyPressed(a) || keyboard.IsKeyPressed(b));

    /// <summary>
    /// Applies a pressed key to <paramref name="field"/>; true when it was an editing key the field
    /// consumed. <paramref name="changedText"/> reports an edit of the text itself.
    /// </summary>
    internal bool TryHandle(TextFieldState field, Key key, bool upDownJump, out bool changedText)
    {
        changedText = false;
        if (Map(key) is not { } editKey) return false;
        var clipboard = inputManager.Keyboard is { } keyboard ? new KeyboardClipboard(keyboard) : null;
        return TextFieldEditing.Handle(field, editKey, CtrlHeld, ShiftHeld, clipboard, upDownJump, out changedText);
    }

    private static EditKey? Map(Key key) => key switch
    {
        Key.Left => EditKey.Left,
        Key.Right => EditKey.Right,
        Key.Up => EditKey.Up,
        Key.Down => EditKey.Down,
        Key.Home => EditKey.Home,
        Key.End => EditKey.End,
        Key.Backspace => EditKey.Backspace,
        Key.Delete => EditKey.Delete,
        Key.A => EditKey.A,
        Key.C => EditKey.C,
        Key.X => EditKey.X,
        Key.V => EditKey.V,
        _ => null,
    };
}
