using System;
using System.Numerics;
using VoxelEngine.Content.Quests;
using VoxelEngine.Quests;
using VoxelEngine.Saves;
using VoxelEngine.UI.Widgets;

namespace VoxelEngine.UI.Quests;

/// <summary>
/// Where the quest book puts everything, shared by its renderer and its input: the grid-to-canvas mapping
/// (the pan is the grid point at the canvas center, one grid step is <see cref="QuestConfig.GridSpacing"/>
/// canvas units at zoom 1), zooming about the pointer, panning by a pointer's travel, the node hit test and
/// the details panel. GL-free.
/// </summary>
internal static class QuestBookLayout
{
    private const float Margin = 24f;

    /// <summary>The canvas point a grid position is drawn at.</summary>
    public static Vector2 ToCanvas(Vector2 grid, QuestViewData camera, Vector2 canvas) =>
        canvas * 0.5f + (grid - new Vector2(camera.PanX, camera.PanY)) * (QuestConfig.GridSpacing * camera.Zoom);

    /// <summary>The grid position under a canvas point.</summary>
    public static Vector2 ToGrid(Vector2 point, QuestViewData camera, Vector2 canvas) =>
        new Vector2(camera.PanX, camera.PanY) + (point - canvas * 0.5f) / (QuestConfig.GridSpacing * camera.Zoom);

    /// <summary>The pan that moves the book by a pointer's travel from <paramref name="pan"/> (the content follows the pointer).</summary>
    public static Vector2 PannedBy(Vector2 pan, Vector2 travel, float zoom) =>
        pan - travel / (QuestConfig.GridSpacing * zoom);

    /// <summary>Multiplies the zoom by <paramref name="factor"/> (clamped to the zoom range) about
    /// <paramref name="point"/>: the grid position under the pointer stays under it.</summary>
    public static void ZoomAbout(QuestViewData camera, Vector2 point, Vector2 canvas, float factor)
    {
        var anchor = ToGrid(point, camera, canvas);
        camera.Zoom = Math.Clamp(camera.Zoom * factor, QuestConfig.MinZoom, QuestConfig.MaxZoom);
        var pan = anchor - (point - canvas * 0.5f) / (QuestConfig.GridSpacing * camera.Zoom);
        camera.PanX = pan.X;
        camera.PanY = pan.Y;
    }

    /// <summary>A node's diameter at the camera's zoom.</summary>
    public static float NodeSize(QuestViewData camera) => QuestConfig.NodeSize * camera.Zoom;

    /// <summary>The visible quest whose node is under <paramref name="point"/>, or null.</summary>
    public static QuestType? NodeAt(QuestLog log, QuestViewData camera, Vector2 canvas, Vector2 point)
    {
        var radius = NodeSize(camera) * 0.5f;
        foreach (var quest in log.Book.Quests)
        {
            if (!log.IsVisible(quest)) continue;
            if (Vector2.DistanceSquared(ToCanvas(quest.Position, camera, canvas), point) <= radius * radius)
                return quest;
        }
        return null;
    }

    /// <summary>The details panel along the right edge.</summary>
    public static UiRect Details(Vector2 canvas)
    {
        var width = MathF.Min(QuestConfig.DetailsWidth, canvas.X * 0.45f);
        var height = canvas.Y - Margin * 2f;
        return new UiRect(new Vector2(canvas.X - Margin - width * 0.5f, canvas.Y * 0.5f), new Vector2(width, height));
    }

    /// <summary>The title line's left edge and center height, top-left of the canvas.</summary>
    public static Vector2 Header => new(Margin, Margin + 14f);

    /// <summary>The controls hint line's left edge and center height, bottom-left of the canvas.</summary>
    public static Vector2 Hint(Vector2 canvas) => new(Margin, canvas.Y - Margin - 8f);
}
