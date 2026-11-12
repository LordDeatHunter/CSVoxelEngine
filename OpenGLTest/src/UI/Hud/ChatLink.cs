namespace VoxelEngine.UI.Hud;

/// <summary>
/// A clickable span of a chat line's <see cref="ChatMessage.Text"/> and the command a click puts into the chat input:
/// suggested, never sent (Minecraft's suggest_command), so Enter still sends it like a typed line.
/// </summary>
/// <param name="Start">The first character of the span in the line's text.</param>
/// <param name="Length">How many characters the span covers.</param>
/// <param name="Command">The line a click puts into the input.</param>
public readonly record struct ChatLink(int Start, int Length, string Command)
{
    /// <summary>Whether the span lies inside <paramref name="text"/> and the command is one the input can hold: a
    /// non-empty command of at most <see cref="ChatView.MaxInputLength"/> characters over a non-empty span.</summary>
    public bool FitsIn(string text) =>
        !string.IsNullOrEmpty(Command) && Command.Length <= ChatView.MaxInputLength
                                       && Start >= 0 && Length > 0 && Length <= text.Length - Start;
}
