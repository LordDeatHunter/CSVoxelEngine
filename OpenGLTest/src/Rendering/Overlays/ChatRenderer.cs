using System;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.UI;
using VoxelEngine.UI.Hud;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The chat HUD of <see cref="UIRenderer"/>: the word-wrapped message log, with its links drawn underlined, and, while the
/// chat is open, its input line. Draws through the owner's primitives from the GL-free <see cref="ChatLogLayout"/>, which
/// the click hit-test (<see cref="LinkCommandAt"/>) rebuilds, so drawing and clicking cannot drift. Render thread only.
/// </summary>
internal sealed class ChatRenderer
{
    // Where a link's underline sits under the line's top, and how thick it is (canvas units)
    private const float LinkUnderlineDrop = ChatLayout.LineHeight - 4f;
    private const float LinkUnderlineHeight = 2f;

    private readonly UIRenderer _ui;
    private readonly ChatLogLayout _log = new();
    // Built once, so measuring a line allocates no closure
    private readonly TextMeasure _measure;

    internal ChatRenderer(UIRenderer ui)
    {
        _ui = ui;
        _measure = s => _ui.Font?.MeasureSize(s, _ui.CountScale).X ?? 0f;
    }

    /// <summary>
    /// The chat HUD over a playing world: the most recent lines bottom-left over a translucent
    /// backdrop (sender and text together, a long message wrapped over several lines) and, while the
    /// input is open, the single-line input field above the hotbar. A link's run is drawn in the link
    /// color and underlined. Geometry comes from <see cref="ChatLayout"/> and <see cref="ChatLogLayout"/>.
    /// </summary>
    internal void DrawChat(ChatView view, float time)
    {
        var screen = _ui.ScreenSize;
        _log.Build(view, screen, _measure);
        var lines = _log.Lines;
        var visible = lines.Count;
        if (visible > 0)
        {
            var bottom = ChatLayout.LogBottom(screen, view.IsOpen);
            var blockCenter = new Vector2(ChatLayout.Left + ChatLayout.MaxWidth * 0.5f,
                bottom - visible * ChatLayout.LineHeight * 0.5f);
            _ui.DrawRect(blockCenter, new Vector2(ChatLayout.MaxWidth + 16f, visible * ChatLayout.LineHeight + 8f),
                Rgba.Gray(0f, 0.35f));

            // Lines[0] is the newest line and sits at the bottom; a fading line's alpha rides its color
            for (var i = 0; i < visible; i++)
                DrawLine(lines[i], view.Messages[lines[i].Message].Kind);
        }

        if (view.IsOpen)
            _ui.DrawInputField(ChatLayout.InputCenter(screen), ChatLayout.InputSize, view.Input, true,
                "Say something...", time);
    }

    private void DrawLine(in ChatLogLine line, ChatKind kind)
    {
        var color = (kind == ChatKind.Player ? Rgba.White : AccentGold).WithAlpha(line.Alpha);
        var scale = _ui.CountScale;
        if (!line.HasLink)
        {
            _ui.DrawText(line.Text, new Vector2(ChatLayout.Left, line.Top), scale, color);
            return;
        }

        var text = line.Text.AsSpan();
        var linkColor = LinkBlue.WithAlpha(line.Alpha);
        _ui.DrawText(text[..line.LinkStart], new Vector2(ChatLayout.Left, line.Top), scale, color);
        _ui.DrawText(text[line.LinkStart..line.LinkEnd], new Vector2(line.LinkLeft, line.Top), scale, linkColor);
        _ui.DrawText(text[line.LinkEnd..], new Vector2(line.LinkRight, line.Top), scale, color);
        _ui.DrawRect(new Vector2((line.LinkLeft + line.LinkRight) * 0.5f, line.Top + LinkUnderlineDrop),
            new Vector2(line.LinkRight - line.LinkLeft, LinkUnderlineHeight), linkColor);
    }

    /// <summary>The command of the chat link under the window point <paramref name="windowPosition"/>, or null. The log is
    /// rebuilt from the current <paramref name="view"/>, so a line that arrived after the last draw can never shift the
    /// hit.</summary>
    internal string? LinkCommandAt(Vector2 windowPosition, ChatView view)
    {
        _log.Build(view, _ui.ScreenSize, _measure);
        return _log.LinkAt(view, _ui.ToCanvas(windowPosition))?.Command;
    }
}
