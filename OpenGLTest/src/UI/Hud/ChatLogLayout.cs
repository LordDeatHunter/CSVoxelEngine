using System;
using System.Collections.Generic;
using System.Numerics;

namespace VoxelEngine.UI.Hud;

/// <summary>
/// GL-free layout of the chat log, shared by the draw (<see cref="VoxelEngine.Rendering.Overlays.ChatRenderer"/>) and the
/// click hit-test so they cannot drift: which messages show, how each wraps into lines (newest at the bottom, the last
/// <see cref="ChatLayout.VisibleMessages"/> lines), each line's place and fade, and where a message's link lies on each
/// line it spans. Measured through an injected <see cref="TextMeasure"/>. Main thread only; reuses its lists.
/// </summary>
public sealed class ChatLogLayout
{
    /// <summary>A message whose alpha is at or under this has faded out: it is skipped, so the log shrinks with it.</summary>
    public const float HiddenAlpha = 0.004f;

    private readonly List<ChatLogLine> _lines = new();
    private readonly List<(int Start, int Length)> _ranges = new();

    /// <summary>The lines of the last <see cref="Build"/>: [0] is the newest and sits at the bottom.</summary>
    public IReadOnlyList<ChatLogLine> Lines => _lines;

    /// <summary>The text a message shows: "Sender: Text" for a player's line, the text alone otherwise.</summary>
    public static string DisplayText(in ChatMessage message) =>
        message.Kind == ChatKind.Player ? $"{message.Sender}: {message.Text}" : message.Text;

    /// <summary>Where the message's <see cref="ChatMessage.Text"/> starts inside its <see cref="DisplayText"/>.</summary>
    public static int TextOffset(in ChatMessage message) =>
        message.Kind == ChatKind.Player ? message.Sender.Length + 2 : 0;

    /// <summary>
    /// Lays the log out for <paramref name="view"/> on a <paramref name="screen"/>-sized canvas: walks the messages newest
    /// first, skips a faded one, wraps each to <see cref="ChatLayout.MaxWidth"/> and stacks its lines bottom-up until the
    /// log holds <see cref="ChatLayout.VisibleMessages"/> lines, so a long message is wrapped rather than cut off.
    /// </summary>
    public void Build(ChatView view, Vector2 screen, TextMeasure measure)
    {
        _lines.Clear();
        var messages = view.Messages;
        for (var m = messages.Count - 1; m >= 0 && _lines.Count < ChatLayout.VisibleMessages; m--)
        {
            var alpha = view.AlphaAt(m);
            if (alpha <= HiddenAlpha) continue;
            var message = messages[m];
            var display = DisplayText(message);
            _ranges.Clear();
            TextWrap.WrapRanges(display, ChatLayout.MaxWidth, measure, _ranges);

            // The link's span in the display text, [a, b); empty when the message has none
            int a = 0, b = 0;
            if (message.Link is { } link)
            {
                a = link.Start + TextOffset(message);
                b = a + link.Length;
            }

            // Wrapped top to bottom, collected bottom-up: push the message's last line first
            for (var l = _ranges.Count - 1; l >= 0 && _lines.Count < ChatLayout.VisibleMessages; l--)
            {
                var (s, length) = _ranges[l];
                var e = s + length;
                var top = ChatLayout.LineTop(screen, view.IsOpen, _lines.Count);
                int linkStart = 0, linkEnd = 0;
                float linkLeft = 0f, linkRight = 0f;
                var from = Math.Max(s, a);
                var to = Math.Min(e, b);
                if (to > from)
                {
                    linkStart = from - s;
                    linkEnd = to - s;
                    linkLeft = ChatLayout.Left + measure(display.AsSpan(s, linkStart));
                    linkRight = ChatLayout.Left + measure(display.AsSpan(s, linkEnd));
                }
                _lines.Add(new ChatLogLine(m, display.Substring(s, length), top, alpha, linkStart, linkEnd, linkLeft,
                    linkRight));
            }
        }
    }

    /// <summary>The link under <paramref name="canvasPoint"/> on the lines of the last <see cref="Build"/>, or null. Build
    /// with the same <paramref name="view"/> first.</summary>
    public ChatLink? LinkAt(ChatView view, Vector2 canvasPoint)
    {
        foreach (var line in _lines)
        {
            if (!line.HasLink) continue;
            if (canvasPoint.Y < line.Top || canvasPoint.Y >= line.Top + ChatLayout.LineHeight) continue;
            if (canvasPoint.X < line.LinkLeft || canvasPoint.X > line.LinkRight) continue;
            return view.Messages[line.Message].Link;
        }
        return null;
    }
}
