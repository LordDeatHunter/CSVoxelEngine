using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.UI;
using VoxelEngine.UI.Hud;
using static VoxelEngine.Rendering.Overlays.UIRenderer;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The chat HUD of <see cref="UIRenderer"/>: the word-wrapped message log and, while the chat is
/// open, its input line. Draws through the owner's primitives. Render thread only.
/// </summary>
internal sealed class ChatRenderer
{
    private readonly UIRenderer _ui;

    internal ChatRenderer(UIRenderer ui)
    {
        _ui = ui;
    }

    /// <summary>
    /// The chat HUD over a playing world: the most recent lines bottom-left over a translucent
    /// backdrop (sender and text together, one line per message) and, while the input is open, the
    /// single-line input field above the hotbar. Geometry comes from <see cref="ChatLayout"/>.
    /// </summary>
    internal void DrawChat(ChatView view, float time)
    {
        var screen = _ui.ScreenSize;
        var messages = view.Messages;
        if (messages.Count > 0)
        {
            // A message may be longer than one text run or wider than
            // the log, so it is word-wrapped and the log shows the last VisibleMessages *lines*,
            // newest at the bottom, rather than cutting a long line off.
            _chatLines.Clear();
            for (var m = messages.Count - 1; m >= 0 && _chatLines.Count < ChatLayout.VisibleMessages; m--)
            {
                var message = messages[m];
                var text = message.Kind == ChatKind.Player ? $"{message.Sender}: {message.Text}" : message.Text;
                var color = message.Kind == ChatKind.Player ? Rgba.White : AccentGold;
                _chatWrap.Clear();
                if (_ui.Font != null)
                    TextWrap.Wrap(text, ChatLayout.MaxWidth, s => _ui.Font.MeasureSize(s, _ui.CountScale).X, _chatWrap);
                else
                    _chatWrap.Add(text);
                // Wrapped top to bottom, collected bottom-up: push the message's last line first.
                // A fading line's alpha rides its color, and a fully faded one is skipped so the
                // log shrinks with it.
                var alpha = view.AlphaAt(m);
                if (alpha <= 0.004f) continue;
                for (var l = _chatWrap.Count - 1; l >= 0 && _chatLines.Count < ChatLayout.VisibleMessages; l--)
                    _chatLines.Add((_chatWrap[l], color.WithAlpha(alpha)));
            }

            var visible = _chatLines.Count;
            var bottom = ChatLayout.LogBottom(screen, view.IsOpen);
            var blockCenter = new Vector2(ChatLayout.Left + ChatLayout.MaxWidth * 0.5f,
                bottom - visible * ChatLayout.LineHeight * 0.5f);
            _ui.DrawRect(blockCenter, new Vector2(ChatLayout.MaxWidth + 16f, visible * ChatLayout.LineHeight + 8f),
                Rgba.Gray(0f, 0.35f));

            // _chatLines[0] is the newest line and sits at the bottom
            for (var i = 0; i < visible; i++)
                _ui.DrawText(_chatLines[i].Text,
                    new Vector2(ChatLayout.Left, ChatLayout.LineTop(screen, view.IsOpen, i)),
                    _ui.CountScale, _chatLines[i].Color);
        }

        if (view.IsOpen)
            _ui.DrawInputField(ChatLayout.InputCenter(screen), ChatLayout.InputSize, view.Input, true,
                "Say something...", time, showTail: true);
    }

    // Scratch for DrawChat's wrapping, reused every frame
    private readonly List<string> _chatWrap = new();
    private readonly List<(string Text, Rgba Color)> _chatLines = new();
}
