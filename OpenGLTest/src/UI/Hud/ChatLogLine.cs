namespace VoxelEngine.UI.Hud;

/// <summary>One wrapped line of the chat log as <see cref="ChatLogLayout"/> laid it out, in canvas units.</summary>
/// <param name="Message">The index into <see cref="ChatView.Messages"/> of the message the line belongs to.</param>
/// <param name="Text">The line's text (a slice of the message's display text).</param>
/// <param name="Top">Canvas y of the line's top.</param>
/// <param name="Alpha">The alpha the line draws with (its message's fade).</param>
/// <param name="LinkStart">The first character of the line's part of the message's link, an index into <paramref name="Text"/>.</param>
/// <param name="LinkEnd">One past the last character of that part; equal to <paramref name="LinkStart"/> when the line holds none.</param>
/// <param name="LinkLeft">Canvas x where that part starts.</param>
/// <param name="LinkRight">Canvas x where that part ends.</param>
public readonly record struct ChatLogLine(int Message, string Text, float Top, float Alpha, int LinkStart, int LinkEnd,
    float LinkLeft, float LinkRight)
{
    /// <summary>Whether part of its message's link lies on this line.</summary>
    public bool HasLink => LinkEnd > LinkStart;
}
