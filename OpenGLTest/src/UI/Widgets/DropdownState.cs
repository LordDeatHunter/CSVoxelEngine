using System.Collections.Generic;
using System.Numerics;

namespace VoxelEngine.UI.Widgets;

/// <summary>What a click did to a <see cref="DropdownState"/>.</summary>
public enum DropdownClick
{
    /// <summary>The dropdown was closed and the click was elsewhere: not the dropdown's.</summary>
    Missed,

    /// <summary>The dropdown was open and the click was elsewhere: it closed, and the click is spent.</summary>
    Dismissed,

    /// <summary>The click was on the closed box and opened or closed the list.</summary>
    Toggled,

    /// <summary>The click picked an option of the open list, which closed.</summary>
    Chosen,
}

/// <summary>
/// A drop-down list, GL-free: a box showing the chosen option that a click opens into one row per option, as tall
/// as the box, directly under it (over it when <see cref="OpensUp"/>), and the rule for what a click does. The
/// chosen option belongs to the owner, which passes it to the draw; the owner sets <see cref="Box"/> wherever it
/// lays the dropdown out and, since an open list covers what is under it, asks <see cref="Click"/> before its other
/// widgets and draws the open list last. <c>DropdownRenderer</c> draws it.
/// </summary>
public sealed class DropdownState(IReadOnlyList<string> options)
{
    public IReadOnlyList<string> Options { get; } = options;

    public bool IsOpen { get; private set; }

    /// <summary>The closed box, in canvas units.</summary>
    public UiRect Box { get; set; }

    /// <summary>Whether the list opens upward (for a box near the bottom of the canvas).</summary>
    public bool OpensUp { get; set; }

    public void Close() => IsOpen = false;

    /// <summary>The row of option <paramref name="index"/> in the open list.</summary>
    public UiRect OptionRect(int index)
    {
        var step = Box.Size.Y * (index + 1);
        return Box with { Center = Box.Center + new Vector2(0f, OpensUp ? -step : step) };
    }

    /// <summary>The whole open list.</summary>
    public UiRect ListRect
    {
        get
        {
            var height = Box.Size.Y * Options.Count;
            var offset = (Box.Size.Y + height) * 0.5f;
            return new UiRect(Box.Center + new Vector2(0f, OpensUp ? -offset : offset), new Vector2(Box.Size.X, height));
        }
    }

    /// <summary>The option of the open list under <paramref name="point"/>, or null (also when closed).</summary>
    public int? OptionAt(Vector2 point)
    {
        if (!IsOpen) return null;
        for (var i = 0; i < Options.Count; i++)
            if (OptionRect(i).Contains(point))
                return i;
        return null;
    }

    /// <summary>Applies a click at <paramref name="point"/>; <paramref name="chosen"/> is the picked option on
    /// <see cref="DropdownClick.Chosen"/>, else -1.</summary>
    public DropdownClick Click(Vector2 point, out int chosen)
    {
        chosen = -1;
        if (OptionAt(point) is { } option)
        {
            chosen = option;
            IsOpen = false;
            return DropdownClick.Chosen;
        }
        if (Box.Contains(point))
        {
            IsOpen = !IsOpen;
            return DropdownClick.Toggled;
        }
        if (!IsOpen) return DropdownClick.Missed;
        IsOpen = false;
        return DropdownClick.Dismissed;
    }
}
