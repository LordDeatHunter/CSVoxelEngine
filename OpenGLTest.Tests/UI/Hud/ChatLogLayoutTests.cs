using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.UI;
using VoxelEngine.UI.Hud;
using Xunit;

namespace VoxelEngine.Tests.UI.Hud;

/// <summary>
/// The chat log's layout and link hit-test (<see cref="ChatLogLayout"/>): a click on a link's characters yields its command
/// and anywhere else nothing, a player's line shifts its link by the sender prefix, a link that wraps is clickable on every
/// line it spans, and the newest line sits at the bottom while faded messages drop out.
/// </summary>
public sealed class ChatLogLayoutTests
{
    private const string Command = "/tp 10.5 70.62 -3.5";
    private static readonly Vector2 Screen = UiCanvas.ReferenceSize;

    private static float Mono(ReadOnlySpan<char> s) => s.Length * 10f;

    // The canvas x of the middle of display character i, and the y of the middle of a line
    private static float CharX(int i) => ChatLayout.Left + 10f * i + 5f;
    private static float MidY(ChatLogLine line) => line.Top + ChatLayout.LineHeight * 0.5f;

    private static ChatLogLayout Built(ChatView chat)
    {
        var layout = new ChatLogLayout();
        layout.Build(chat, Screen, Mono);
        return layout;
    }

    [Fact]
    public void AClickOnALinksCharactersYieldsItsCommand()
    {
        const string text = "Nearest at [1, 2, 3] far";
        var start = text.IndexOf('[');
        var end = text.IndexOf(']') + 1;
        var chat = new ChatView();
        chat.Open();
        chat.AddSystem("An earlier line");
        chat.AddSystem(text, new ChatLink(start, end - start, Command));

        var layout = Built(chat);
        var linked = layout.Lines.Single(l => l.Message == 1);
        var other = layout.Lines.Single(l => l.Message == 0);

        Assert.Equal(Command, layout.LinkAt(chat, new Vector2(CharX(start + 1), MidY(linked)))?.Command);
        Assert.Equal(Command, layout.LinkAt(chat, new Vector2(CharX(end - 1), MidY(linked)))?.Command);
        Assert.Null(layout.LinkAt(chat, new Vector2(CharX(start - 1), MidY(linked))));
        Assert.Null(layout.LinkAt(chat, new Vector2(CharX(end + 1), MidY(linked))));
        Assert.Null(layout.LinkAt(chat, new Vector2(CharX(start + 1), MidY(other))));
    }

    [Fact]
    public void APlayerLinesLinkIsOffsetBySenderPrefix()
    {
        const string sender = "Alice";
        const string text = "see [5, 6, 7] here";
        var start = text.IndexOf('[');
        var chat = new ChatView();
        chat.Open();
        chat.AddMessage(ChatKind.Player, sender, text, new ChatLink(start, 9, Command));

        var layout = Built(chat);
        var line = layout.Lines.Single();

        Assert.Equal(Command, layout.LinkAt(chat, new Vector2(CharX(sender.Length + 2 + start), MidY(line)))?.Command);
        // Where the span would sit without the "Alice: " prefix is plain text
        Assert.Null(layout.LinkAt(chat, new Vector2(CharX(start), MidY(line))));
    }

    [Fact]
    public void AWrappedLinkIsClickableOnEveryLineItSpans()
    {
        // Short words up to near the wrap width, then a link of several words that cannot fit on the first line whole
        var prefix = string.Concat(Enumerable.Repeat("ab ", (int)(ChatLayout.MaxWidth / 10f / 3f) - 2));
        const string linkText = "[100, -200, 300]";
        var text = prefix + linkText + " end";
        var chat = new ChatView();
        chat.Open();
        chat.AddSystem(text, new ChatLink(prefix.Length, linkText.Length, Command));

        var layout = Built(chat);
        var linkLines = layout.Lines.Where(l => l.HasLink).ToList();

        Assert.True(linkLines.Count > 1, "the link did not wrap across lines");
        foreach (var line in linkLines)
        {
            var center = new Vector2((line.LinkLeft + line.LinkRight) * 0.5f, MidY(line));
            Assert.Equal(Command, layout.LinkAt(chat, center)?.Command);
        }

        // Lines are stacked bottom-up, so read them top to bottom
        var pieces = linkLines.OrderBy(l => l.Top).Select(l => l.Text[l.LinkStart..l.LinkEnd]);
        Assert.Equal(linkText.Replace(" ", ""), string.Concat(pieces).Replace(" ", ""));
    }

    [Fact]
    public void TheNewestLineSitsAtTheBottomAndFadedMessagesAreSkipped()
    {
        var chat = new ChatView();
        chat.AddSystem("an old line");
        chat.Update(ChatView.HoldSeconds + ChatView.FadeSeconds + 1f);
        var longText = string.Concat(Enumerable.Repeat("word ", (int)(ChatLayout.MaxWidth / 10f / 5f) + 4)).TrimEnd();
        chat.AddSystem(longText);

        var layout = Built(chat);

        Assert.True(layout.Lines.Count > 1, "the newer line did not wrap");
        Assert.All(layout.Lines, l => Assert.Equal(1, l.Message));
        Assert.True(layout.Lines[0].Top > layout.Lines[1].Top, "the newest line is not at the bottom");

        for (var i = 0; i < ChatLayout.VisibleMessages * 2; i++)
            chat.AddSystem(longText);
        layout = Built(chat);
        Assert.True(layout.Lines.Count <= ChatLayout.VisibleMessages);
        Assert.Equal(chat.Messages.Count - 1, layout.Lines[0].Message);
    }
}
