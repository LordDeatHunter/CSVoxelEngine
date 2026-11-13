using System;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.UI;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.Menus;
using VoxelEngine.UI.Widgets;
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

    // The completion popup and the usage hint cover the log lines behind them, so they are near opaque
    private static readonly Rgba PopupBackground = Rgba.Gray(0.06f, 0.96f);

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

        if (!view.IsOpen) return;
        _ui.DrawInputField(ChatLayout.InputCenter(screen), ChatLayout.InputSize, view.Input, true,
            "Say something...", time);
        DrawCompletion(view.Input, view.Completion, screen);
    }

    /// <summary>
    /// A command line's autocomplete over the open input: the dimmed ghost of the best completion after the caret,
    /// and above the input either the completion popup (left-aligned under the word being completed, the
    /// Tab-selected row in gold and, before the first Tab, the best one in white) or, with nothing to offer, the
    /// command's usage line.
    /// </summary>
    private void DrawCompletion(TextFieldState input, ChatCompletion completion, Vector2 screen)
    {
        var scale = _ui.CountScale;
        var box = new TextBoxLayout(ChatLayout.InputRect(screen));
        var text = input.Text;
        // DrawInputField just scrolled the field; text left of ViewStart is off the box
        var viewStart = Math.Clamp(input.ViewStart, 0, text.Length);
        var ghost = completion.Ghost(input);
        if (ghost.Length > 0)
        {
            var typedWidth = _ui.TextWidth(text[viewStart..], scale);
            if (typedWidth + _ui.TextWidth(ghost, scale) <= box.TextBudget)
                _ui.DrawTextCenteredY(ghost, box.TextLeft + typedWidth, box.CenterY, scale, TextDim.WithAlpha(0.8f));
        }

        var bottom = ChatLayout.SuggestionBottom(screen);
        var items = completion.Current.Items;
        if (items.Count == 0)
        {
            if (completion.Current.UsageHint is not { } usage) return;
            DrawPopupBox(ChatLayout.Left, bottom, _ui.TextWidth(usage, scale), 1, screen);
            _ui.DrawText(usage, new Vector2(ChatLayout.Left + ChatLayout.SuggestionPad,
                bottom - ChatLayout.SuggestionPad - ChatLayout.LineHeight), scale, TextDim);
            return;
        }

        var rows = Math.Min(items.Count, ChatLayout.SuggestionRows);
        var first = ChatLayout.SuggestionWindowStart(items.Count, completion.Selected);
        var width = 0f;
        for (var i = first; i < first + rows; i++)
            width = MathF.Max(width, _ui.TextWidth(items[i].Label, scale));
        var replaceStart = Math.Clamp(completion.Current.ReplaceStart, viewStart, text.Length);
        var left = box.TextLeft + _ui.TextWidth(text[viewStart..replaceStart], scale) - ChatLayout.SuggestionPad;
        left = MathF.Max(0f, MathF.Min(left, screen.X - width - ChatLayout.SuggestionPad * 2f));
        DrawPopupBox(left, bottom, width, rows, screen);

        var top = bottom - ChatLayout.SuggestionPad - rows * ChatLayout.LineHeight;
        for (var row = 0; row < rows; row++)
        {
            var index = first + row;
            var color = index == completion.Selected ? AccentGold
                : completion.Selected < 0 && index == 0 ? Rgba.White
                : TextDim;
            _ui.DrawText(items[index].Label, new Vector2(left + ChatLayout.SuggestionPad, top + row * ChatLayout.LineHeight),
                scale, color);
        }
    }

    // The backdrop of rows text lines of the given width, its bottom-left corner at (left, bottom)
    private void DrawPopupBox(float left, float bottom, float textWidth, int rows, Vector2 screen)
    {
        var size = new Vector2(textWidth + ChatLayout.SuggestionPad * 2f, rows * ChatLayout.LineHeight + ChatLayout.SuggestionPad * 2f);
        _ui.DrawRect(new Vector2(MathF.Min(left + size.X * 0.5f, screen.X - size.X * 0.5f), bottom - size.Y * 0.5f), size,
            PopupBackground);
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
