using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Content.Quests;
using VoxelEngine.Data;
using VoxelEngine.Graphics;
using VoxelEngine.Quests;
using VoxelEngine.UI;
using VoxelEngine.UI.Quests;
using VoxelEngine.UI.Widgets;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The [P] quest book: a dark grid backdrop, the edges between visible quests (green into a complete
/// quest, dusty brown into an open one), the nodes (a hex or circle frame tinted by state, a check badge on
/// a complete one, a gold ring on the selected one, the quest's icon inside), the header with the tally,
/// the controls hint and, for the selected quest, the details panel. Positions come from
/// <see cref="QuestBookLayout"/>, which the clicks read too; also draws the quest-complete toast card.
/// Owns the node textures. Render thread only.
/// </summary>
internal sealed class QuestBookRenderer : IDisposable
{
    private static readonly Rgba Backdrop = new(36, 42, 54);
    // The details panel is opaque, so no node under it shows through
    private static readonly Rgba DetailsFill = new(20, 22, 28);
    private static readonly Rgba GridLine = new(255, 255, 255, 14);
    private static readonly Rgba EdgeOpen = new(122, 96, 96);
    private static readonly Rgba EdgeDone = new(110, 200, 96);
    private static readonly Rgba NodeOpen = Rgba.Gray(0.62f);
    private static readonly Rgba NodeDone = Rgba.Gray(0.95f);
    private static readonly Rgba NodeHover = Rgba.White;
    private const float PanelPad = 18f;
    private const float RowHeight = 34f;
    private const float RowIcon = 26f;

    private readonly GL _gl;
    private readonly UIRenderer _ui;
    private readonly uint _circleTexture;
    private readonly uint _hexTexture;
    private readonly uint _checkTexture;
    private readonly List<string> _wrapScratch = [];

    internal QuestBookRenderer(GL gl, UIRenderer ui)
    {
        _gl = gl;
        _ui = ui;
        _circleTexture = TextureLoader.LoadFromFile(gl, "assets/textures/ui/quest_node_circle.png");
        _hexTexture = TextureLoader.LoadFromFile(gl, "assets/textures/ui/quest_node_hex.png");
        _checkTexture = TextureLoader.LoadFromFile(gl, "assets/textures/ui/quest_check.png");
    }

    /// <summary>Draws the book over the whole canvas; <paramref name="time"/> animates item icons.</summary>
    internal void Draw(QuestBookView view, QuestLog log, float time)
    {
        var canvas = _ui.ScreenSize;
        var camera = view.Camera;
        _ui.DrawRect(canvas * 0.5f, canvas, Backdrop);
        DrawGrid(canvas, camera);

        var book = log.Book;
        var edge = QuestConfig.EdgeThickness * camera.Zoom;
        foreach (var quest in book.Quests)
        {
            if (!log.IsVisible(quest)) continue;
            var to = QuestBookLayout.ToCanvas(quest.Position, camera, canvas);
            foreach (var parent in quest.Parents)
            {
                var from = QuestBookLayout.ToCanvas(book.Quests[parent].Position, camera, canvas);
                DrawLine(from, to, edge, log.IsComplete(quest) ? EdgeDone : EdgeOpen);
            }
        }

        var hovered = view.Hover is { } pointer && !view.Dragging
            ? QuestBookLayout.NodeAt(log, camera, canvas, pointer)
            : null;
        var size = QuestBookLayout.NodeSize(camera);
        // The 3D block icons depth-test among themselves; one clear before the first
        _ui.EnsureDepthCleared();
        var complete = 0;
        foreach (var quest in book.Quests)
        {
            if (log.IsComplete(quest)) complete++;
            if (!log.IsVisible(quest)) continue;
            DrawNode(quest, QuestBookLayout.ToCanvas(quest.Position, camera, canvas), size, log.IsComplete(quest),
                quest == view.Selected, quest == hovered, camera.Zoom, time);
        }

        // Each graph's title rides above its root
        foreach (var quest in book.Quests)
        {
            if (quest.Parents.Count > 0) continue;
            var top = QuestBookLayout.ToCanvas(quest.Position, camera, canvas) - new Vector2(0f, size * 0.5f + 16f);
            _ui.DrawTextMiddle(book.Graphs[quest.Graph], top, _ui.CountScale, TextDim);
        }

        var header = QuestBookLayout.Header;
        _ui.DrawTextCenteredY(QuestText.Title, header.X, header.Y, _ui.HeadScale, AccentGold);
        _ui.DrawTextCenteredY(QuestText.Tally(complete, book.Quests.Count),
            header.X + _ui.TextWidth(QuestText.Title, _ui.HeadScale) + 20f, header.Y, _ui.CountScale, TextDim);
        var hint = QuestBookLayout.Hint(canvas);
        _ui.DrawTextCenteredY(QuestText.Hint, hint.X, hint.Y, _ui.CountScale, TextDim);

        if (view.Selected is { } selected)
            DrawDetails(selected, log, canvas, time);
    }

    // A faint grid that pans and zooms with the book; dropped once its lines would crowd together
    private void DrawGrid(Vector2 canvas, VoxelEngine.Saves.QuestViewData camera)
    {
        var step = QuestConfig.GridSpacing * camera.Zoom;
        if (step < 24f) return;
        var origin = QuestBookLayout.ToCanvas(Vector2.Zero, camera, canvas);
        var startX = origin.X - MathF.Floor(origin.X / step) * step;
        var startY = origin.Y - MathF.Floor(origin.Y / step) * step;
        for (var x = startX; x < canvas.X; x += step)
            _ui.DrawRect(new Vector2(x, canvas.Y * 0.5f), new Vector2(1f, canvas.Y), GridLine);
        for (var y = startY; y < canvas.Y; y += step)
            _ui.DrawRect(new Vector2(canvas.X * 0.5f, y), new Vector2(canvas.X, 1f), GridLine);
    }

    // A thick segment: the unit square stretched along the line and turned to its angle
    private void DrawLine(Vector2 from, Vector2 to, float thickness, Rgba color)
    {
        var delta = to - from;
        var length = delta.Length();
        if (length < 1f) return;
        var model = Matrix4x4.CreateScale(length, thickness, 1f)
                    * Matrix4x4.CreateRotationZ(MathF.Atan2(delta.Y, delta.X))
                    * Matrix4x4.CreateTranslation((from.X + to.X) * 0.5f, (from.Y + to.Y) * 0.5f, 0f);
        _ui.DrawMesh(_ui.UnitSquare, model, color);
    }

    private void DrawNode(QuestType quest, Vector2 center, float size, bool done, bool selected, bool hovered,
        float zoom, float time)
    {
        var frame = quest.Shape == QuestShape.Hex ? _hexTexture : _circleTexture;
        if (selected)
            _ui.DrawTexture(center, new Vector2(size * 1.18f), frame, AccentGold);
        _ui.DrawTexture(center, new Vector2(size), frame, hovered ? NodeHover : done ? NodeDone : NodeOpen);
        _ui.DrawIdIcon(center, QuestConfig.NodeIconSize * zoom, quest.IconId, 0f, time);
        if (done)
        {
            var badge = size * 0.38f;
            _ui.DrawTexture(center + new Vector2(size * 0.36f, -size * 0.36f), new Vector2(badge), _checkTexture);
        }
    }

    private void DrawDetails(QuestType quest, QuestLog log, Vector2 canvas, float time)
    {
        var panel = QuestBookLayout.Details(canvas);
        // Node icons wrote depth where the panel now covers them; the panel's own icons must draw over
        _ui.ClearDepthForOverlay();
        _ui.DrawPanelFrame(panel, DetailsFill);
        var left = panel.Left + PanelPad;
        var width = panel.Size.X - PanelPad * 2f;
        var y = panel.Top + PanelPad + 16f;

        _ui.DrawIdIcon(new Vector2(left + 20f, y + 4f), 40f, quest.IconId, 0f, time);
        _ui.DrawTextCenteredY(_ui.FittedText(quest.Title, _ui.HeadScale, width - 56f), left + 52f, y, _ui.HeadScale, AccentGold);
        var done = log.IsComplete(quest);
        _ui.DrawTextCenteredY(done ? QuestText.Complete : QuestText.InProgress, left + 52f, y + 26f, _ui.CountScale,
            done ? ReadyGreen : TextDim);
        y += 64f;

        y = DrawWrapped(quest.Description, left, y, width, Rgba.White) + 14f;
        _ui.DrawRect(new Vector2(panel.Center.X, y), new Vector2(width, 1.5f), SlotBorderColor);
        y += 22f;
        _ui.DrawTextCenteredY(QuestText.Requirements, left, y, _ui.CountScale, AccentGold);
        y += 28f;

        if (!quest.Readable && !done)
        {
            DrawWrapped(QuestText.Hidden, left, y - 8f, width, TextDim);
            return;
        }
        for (var r = 0; r < quest.Requirements.Count; r++)
        {
            var requirement = quest.Requirements[r];
            var have = log.ProgressOf(quest, r);
            var met = have >= requirement.Count;
            if (requirement.Entity == null)
                _ui.DrawIdIcon(new Vector2(left + RowIcon * 0.5f, y), RowIcon, requirement.Id, 0f, time);
            var progress = QuestText.Progress(have, requirement.Count);
            var progressWidth = _ui.TextWidth(progress, _ui.CountScale);
            _ui.DrawTextCenteredY(_ui.FittedText(QuestText.Line(requirement), _ui.CountScale, width - RowIcon - progressWidth - 24f),
                left + RowIcon + 10f, y, _ui.CountScale, met ? ReadyGreen : Rgba.White);
            _ui.DrawTextCenteredY(progress, panel.Right - PanelPad - progressWidth, y, _ui.CountScale, met ? ReadyGreen : TextDim);
            y += RowHeight;
        }
    }

    // Word-wrapped text from its top-left; returns the y under the last line
    private float DrawWrapped(string text, float left, float top, float width, Rgba color)
    {
        if (string.IsNullOrEmpty(text)) return top;
        var scale = _ui.CountScale;
        _wrapScratch.Clear();
        TextWrap.Wrap(text, width, line => _ui.MeasureTextWidth(line.ToString(), scale), _wrapScratch);
        var y = top;
        foreach (var line in _wrapScratch)
        {
            _ui.DrawTextCenteredY(line, left, y + 9f, scale, color);
            y += 24f;
        }
        return y;
    }

    /// <summary>The bottom-right quest-complete card <paramref name="toasts"/> has on screen, slid and faded by its phase.</summary>
    internal void DrawToast(QuestToastQueue toasts, float time)
    {
        if (toasts.Current is not { } quest || toasts.Alpha <= 0f) return;
        var canvas = _ui.ScreenSize;
        var size = new Vector2(QuestConfig.ToastWidth, QuestConfig.ToastHeight);
        var slide = toasts.Slide * (size.X + QuestConfig.ToastMarginRight);
        var card = new UiRect(canvas - new Vector2(QuestConfig.ToastMarginRight, QuestConfig.ToastMarginBottom) - size * 0.5f, size);
        _ui.SetOverlay(new Vector2(slide, 0f), toasts.Alpha);
        _ui.ClearDepthForOverlay();
        _ui.DrawRect(card.Center, card.Size, CardBackground);
        _ui.DrawBorder(card.Center, card.Size, Border.Standard, SlotBorderColor);
        _ui.DrawRect(new Vector2(card.Left + 3f, card.Center.Y), new Vector2(6f, card.Size.Y - 4f), AccentGold);
        var iconCenter = new Vector2(card.Left + 16f + 26f, card.Center.Y);
        _ui.DrawTexture(iconCenter, new Vector2(56f), quest.Shape == QuestShape.Hex ? _hexTexture : _circleTexture, NodeDone);
        _ui.DrawIdIcon(iconCenter, 32f, quest.IconId, 0f, time);
        var textLeft = iconCenter.X + 40f;
        var textWidth = card.Right - textLeft - 12f;
        _ui.DrawTextCenteredY(QuestText.ToastHeading, textLeft, card.Center.Y - 14f, _ui.CountScale, AccentGold);
        _ui.DrawTextCenteredY(_ui.FittedText(quest.Title, _ui.CountScale, textWidth), textLeft, card.Center.Y + 14f,
            _ui.CountScale, Rgba.White);
        _ui.ClearOverlay();
    }

    public void Dispose()
    {
        _gl.DeleteTexture(_circleTexture);
        _gl.DeleteTexture(_hexTexture);
        _gl.DeleteTexture(_checkTexture);
    }
}
