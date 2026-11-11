using System;
using System.Numerics;

namespace VoxelEngine.UI.Widgets;

/// <summary>A button's geometry and state rule, GL-free: the hit rectangle is the drawn one, and the label
/// gets the width left after the side padding.</summary>
public readonly record struct ButtonLayout(UiRect Rect)
{
    /// <summary>Disabled wins over everything (a disabled button ignores the pointer), then pressed
    /// (only while the pointer is over it), then hover.</summary>
    public static WidgetState StateOf(bool enabled, bool hovered, bool pressed)
    {
        if (!enabled) return WidgetState.Disabled;
        if (pressed && hovered) return WidgetState.Pressed;
        return hovered ? WidgetState.Hover : WidgetState.Normal;
    }

    /// <summary>The state for a pointer at <paramref name="pointer"/> (null when there is none).</summary>
    public WidgetState StateAt(Vector2? pointer, bool pointerDown, bool enabled) =>
        StateOf(enabled, pointer is { } p && Rect.Contains(p), pointerDown);

    /// <summary>Whether a click at <paramref name="point"/> activates the button.</summary>
    public bool Hit(Vector2 point, bool enabled) => enabled && Rect.Contains(point);

    /// <summary>Width the label may use.</summary>
    public float LabelWidth => MathF.Max(0f, Rect.Size.X - UiTheme.Space.Sm * 2f);
}
