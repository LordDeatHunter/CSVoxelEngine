namespace VoxelEngine.UI.Hud;

/// <summary>
/// One HUD toast line (a day change, a first pickup, a full inventory): a text with a hold-then-fade
/// clock, GL-free so the wording and the curve are testable. The caller shows a line, updates it
/// once a frame and draws it while <see cref="Alpha"/> is above zero; a new <see cref="Show"/>
/// restarts the clock.
/// </summary>
public sealed class HudToast
{
    /// <summary>Seconds the line stays fully opaque.</summary>
    public const float HoldSeconds = 2.5f;

    /// <summary>Seconds the fade out takes once the hold is over.</summary>
    public const float FadeSeconds = 1f;

    private float _elapsed = HoldSeconds + FadeSeconds;

    /// <summary>The line shown, or empty when none is.</summary>
    public string Text { get; private set; } = "";

    /// <summary>The line's alpha: 1 through the hold, easing to 0 over the fade.</summary>
    public float Alpha { get; private set; }

    /// <summary>Shows a line, restarting the clock (a line already up is replaced).</summary>
    public void Show(string text)
    {
        Text = text;
        _elapsed = 0f;
        Alpha = 1f;
    }

    /// <summary>Advances the clock; a finished line parks at alpha 0 with its text cleared.</summary>
    public void Update(float deltaTime)
    {
        if (Alpha <= 0f) return;
        _elapsed += System.MathF.Max(0f, deltaTime);
        if (_elapsed <= HoldSeconds)
        {
            Alpha = 1f;
            return;
        }

        Alpha = 1f - MathUtils.SmoothStep(MathUtils.InverseLerp(0f, FadeSeconds, _elapsed - HoldSeconds));
        if (Alpha <= 0f)
        {
            Alpha = 0f;
            Text = "";
        }
    }
}
