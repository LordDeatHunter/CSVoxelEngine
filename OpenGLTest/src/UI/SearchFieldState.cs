namespace silkgltriangle;

/// <summary>
/// GL-free contents and cursor of one dropdown search field: the typed query, the highlighted
/// suggestion and the suggestion list's scroll. The field owns no results — callers recompute
/// them from the query through <see cref="InventorySearch"/> each frame, so it never holds stale
/// content — and it reuses <see cref="TextFieldState"/>'s typing rules.
/// </summary>
public sealed class SearchFieldState
{
    /// <summary>Most characters the query holds; well under <see cref="BitmapFont.MaxLayoutChars"/>.</summary>
    public const int MaxLength = 32;

    public TextFieldState Text { get; } = new(MaxLength);

    public string Query => Text.Text;

    public bool IsEmpty => Text.IsEmpty;

    /// <summary>Canvas pixels the suggestion list is scrolled by; the draw and hit-test clamp on read.</summary>
    public float ScrollOffset { get; set; }

    /// <summary>Index into the current suggestions, or -1 for none.</summary>
    public int Highlight { get; set; } = -1;

    public void Clear()
    {
        Text.Clear();
        ScrollOffset = 0f;
        Highlight = -1;
    }
}
