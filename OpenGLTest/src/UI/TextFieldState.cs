namespace silkgltriangle;

/// <summary>
/// GL-free contents of one single-line text field on the main menu (a world name, a seed).
/// It knows nothing about Silk input: <see cref="MainMenuController"/> feeds it the characters
/// that arrive through the keyboard's character event and the Backspace key, so the typing rules
/// are covered by tests.
/// </summary>
public sealed class TextFieldState(int maxLength)
{
    /// <summary>The most characters the field holds; appends past it are refused.</summary>
    public int MaxLength { get; } = maxLength;

    public string Text { get; private set; } = "";

    public bool IsEmpty => Text.Length == 0;

    /// <summary>
    /// Appends <paramref name="c"/> when it is a character the UI font can draw and the field has
    /// room. The bitmap font covers printable ASCII only (<see cref="BitmapFont.FirstChar"/> to
    /// <c>~</c>; 127 is DEL), so Enter, Tab and Backspace arriving through the character event as
    /// control characters are refused here rather than inserted.
    /// </summary>
    public bool TryAppend(char c)
    {
        if (c < (char)BitmapFont.FirstChar || c > '~' || Text.Length >= MaxLength) return false;
        Text += c;
        return true;
    }

    /// <summary>Removes the last character; false when the field was already empty.</summary>
    public bool Backspace()
    {
        if (Text.Length == 0) return false;
        Text = Text[..^1];
        return true;
    }

    public void Clear() => Text = "";

    /// <summary>
    /// Replaces the contents with <paramref name="text"/> as if it had been typed, so a preset
    /// value (the current name on the edit page) obeys exactly the typing rules.
    /// </summary>
    public void Set(string? text)
    {
        Clear();
        if (text == null) return;
        foreach (var c in text) TryAppend(c);
    }
}
