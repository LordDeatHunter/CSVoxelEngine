using System;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// The [E] panel's open and close easing: a progress that runs toward 1 while the panel is open
/// and back to 0 once it closes, read as a fade and a slide up into place. GL-free. It keeps its
/// own clock (the render time handed to <see cref="Step"/>), so it needs no frame delta of its own.
/// </summary>
public sealed class PanelTransition
{
    private float _progress;
    private float _lastTime = float.NaN;

    /// <summary>Raw 0..1 progress, linear in time.</summary>
    public float Progress => _progress;

    /// <summary>Whether anything of the panel should still be drawn.</summary>
    public bool Visible => _progress > 0f;

    /// <summary>Eased 0..1 amount: out-cubic while opening, in-cubic while closing, so a close never lingers.</summary>
    public float Alpha => _opening ? MathUtils.EaseOutCubic(_progress) : MathUtils.EaseInCubic(_progress);

    /// <summary>Canvas pixels the panel still has to rise: full at the start of opening, 0 once settled.</summary>
    public float SlideY => (1f - Alpha) * HudConfig.PanelSlidePixels;

    private bool _opening;

    /// <summary>Moves the progress toward the open or closed end by the time since the last call (the first call moves nothing).</summary>
    public void Step(bool open, float timeSeconds)
    {
        var dt = float.IsNaN(_lastTime) ? 0f : Math.Clamp(timeSeconds - _lastTime, 0f, 0.1f);
        _lastTime = timeSeconds;
        _opening = open;
        _progress = open
            ? MathF.Min(1f, _progress + dt / HudConfig.PanelOpenSeconds)
            : MathF.Max(0f, _progress - dt / HudConfig.PanelCloseSeconds);
    }

    /// <summary>Back to closed and clockless (a world opened or closed).</summary>
    public void Reset()
    {
        _progress = 0f;
        _lastTime = float.NaN;
        _opening = false;
    }
}
