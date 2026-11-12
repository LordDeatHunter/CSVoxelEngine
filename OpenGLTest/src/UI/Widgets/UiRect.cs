using System;
using System.Numerics;

namespace VoxelEngine.UI.Widgets;

/// <summary>An axis-aligned rectangle in canvas units (see <see cref="UiCanvas"/>), as the (center, size)
/// pair every renderer primitive takes.</summary>
public readonly record struct UiRect(Vector2 Center, Vector2 Size)
{
    public float Left => Center.X - Size.X * 0.5f;
    public float Right => Center.X + Size.X * 0.5f;
    public float Top => Center.Y - Size.Y * 0.5f;
    public float Bottom => Center.Y + Size.Y * 0.5f;

    public static UiRect FromTopLeft(Vector2 topLeft, Vector2 size) => new(topLeft + size * 0.5f, size);

    /// <summary>Whether <paramref name="point"/> lies in the rectangle (left and top edges in, right and bottom
    /// out, so two rectangles that share an edge never both claim a point on it).</summary>
    public bool Contains(Vector2 point) =>
        point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

    /// <summary>The rectangle shrunk by <paramref name="amount"/> on every side (never below zero size).</summary>
    public UiRect Inset(float amount) =>
        new(Center, new Vector2(MathF.Max(0f, Size.X - amount * 2f), MathF.Max(0f, Size.Y - amount * 2f)));
}
