namespace VoxelEngine.Commands;

/// <summary>One completion for the word at the caret: <see cref="Insert"/> replaces the word, <see cref="Label"/> is
/// what the popup shows (a player's name, an option, <c>~ ~ ~</c>).</summary>
public readonly record struct Suggestion(string Insert, string Label)
{
    /// <summary>A completion shown as what it inserts.</summary>
    public Suggestion(string insert) : this(insert, insert)
    {
    }
}
