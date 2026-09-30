using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>What the full-screen loading overlay is announcing.</summary>
public enum LoadingScreenKind
{
    /// <summary>A world is streaming in: spinner, caption and progress bar.</summary>
    Loading,

    /// <summary>A world is being saved and closed: spinner and caption, no bar.</summary>
    Saving,
}

/// <summary>
/// State of the loading screen shown while a world streams in (and the short "Saving..." frame
/// of a save and quit). Owned by <see cref="Program"/>, which drives <see cref="Update"/> from
/// <see cref="World.LoadProgress"/> and <see cref="World.IsLoaded"/>; <see cref="UIRenderer"/>
/// only reads it. A load that timed out still reports IsLoaded, so the bar completes either way.
/// The shown progress eases toward the real one and never moves backwards, so a load whose
/// progress estimate dips never makes the bar jump back; once complete the overlay fades out
/// over <see cref="WorldGenerationConfig.LoadingFadeSeconds"/>.
/// </summary>
public sealed class LoadingScreenView
{
    private float _fadeTimer;

    public LoadingScreenKind Kind { get; private set; }

    public string WorldName { get; private set; } = "";

    public string Caption { get; private set; } = "";

    /// <summary>The bar's fill, 0..1; never decreases between two <see cref="Begin"/> calls.</summary>
    public float ShownProgress { get; private set; }

    /// <summary>True once the world reported itself loaded; the bar is full from then on.</summary>
    public bool Complete { get; private set; }

    /// <summary>Opacity of the overlay: 1 until <see cref="Complete"/>, then eased down to 0.</summary>
    public float FadeAlpha { get; private set; } = 1f;

    /// <summary>
    /// True when there is nothing left to draw: before any <see cref="Begin"/> (so a storeless
    /// world never shows the overlay), after <see cref="Finish"/>, and once the fade has elapsed.
    /// </summary>
    public bool IsFinished { get; private set; } = true;

    /// <summary>Starts a fresh overlay: empty bar, fully opaque, not finished.</summary>
    public void Begin(LoadingScreenKind kind, string worldName, string caption)
    {
        Kind = kind;
        WorldName = worldName;
        Caption = caption;
        ShownProgress = 0f;
        Complete = false;
        FadeAlpha = 1f;
        IsFinished = false;
        _fadeTimer = 0f;
    }

    /// <summary>
    /// Eases the bar toward <paramref name="targetProgress"/> (monotone, never past the target)
    /// and, once <paramref name="complete"/>, fills it and advances the fade-out.
    /// </summary>
    public void Update(float targetProgress, bool complete, float deltaTime)
    {
        var target = MathUtils.Clamp01(targetProgress);
        var eased = MathUtils.Damp(ShownProgress, target, WorldGenerationConfig.LoadingProgressDampRate, deltaTime);
        ShownProgress = MathF.Max(ShownProgress, MathF.Min(target, eased));

        if (!complete)
            return;

        Complete = true;
        ShownProgress = 1f;
        _fadeTimer += MathF.Max(0f, deltaTime);
        FadeAlpha = 1f - MathUtils.SmoothStep(_fadeTimer / WorldGenerationConfig.LoadingFadeSeconds);
        if (_fadeTimer >= WorldGenerationConfig.LoadingFadeSeconds)
            IsFinished = true;
    }

    /// <summary>Ends the overlay at once (an aborted open, or a save that has finished).</summary>
    public void Finish()
    {
        FadeAlpha = 0f;
        IsFinished = true;
    }
}

/// <summary>
/// Pure screen layout of the loading screen in canvas units (origin top-left, Y down), plus the
/// spinner's animation as a pure function of the window clock. Wall time goes in rather than a
/// per-frame step, so a hitching frame never stalls the spinner. No GL and no time source.
/// </summary>
public static class LoadingScreenLayout
{
    public const float SpinnerRadius = 28f;
    public const float DotSize = 10f;

    /// <summary>Brightness of the dots furthest behind the spinner's leading dot.</summary>
    public const float DotBrightnessFloor = 0.2f;

    public static Vector2 BarSize => new(640f, 18f);

    public static Vector2 BarCentre(Vector2 screenSize) =>
        new(screenSize.X * 0.5f, screenSize.Y * 0.5f + 60f);

    public static Vector2 SpinnerCentre(Vector2 screenSize) =>
        new(screenSize.X * 0.5f, screenSize.Y * 0.5f - 40f);

    /// <summary>Top edge of the world-name title, above the spinner.</summary>
    public static float TitleTop(Vector2 screenSize) =>
        SpinnerCentre(screenSize).Y - SpinnerRadius - 90f;

    /// <summary>Top edge of the caption, between the spinner and the bar.</summary>
    public static float CaptionTop(Vector2 screenSize) =>
        BarCentre(screenSize).Y - BarSize.Y * 0.5f - 44f;

    /// <summary>Top edge of the percentage text under the bar.</summary>
    public static float PercentTop(Vector2 screenSize) =>
        BarCentre(screenSize).Y + BarSize.Y * 0.5f + 14f;

    /// <summary>
    /// Where the spinner is in its revolution, in [0, 1): the fractional part of
    /// <paramref name="time"/> over <see cref="WorldGenerationConfig.LoadingSpinnerPeriodSeconds"/>.
    /// </summary>
    public static float SpinnerPhase(double time)
    {
        var turns = time / WorldGenerationConfig.LoadingSpinnerPeriodSeconds;
        var phase = (float)(turns - Math.Floor(turns));
        // A fraction just under 1 can round up to exactly 1 as a float; that is the next turn's 0.
        return phase >= 1f ? 0f : phase;
    }

    /// <summary>Centre of dot <paramref name="dot"/> of <paramref name="dotCount"/> on the spinner ring.</summary>
    public static Vector2 DotCentre(Vector2 screenSize, int dot, int dotCount)
    {
        var angle = dotCount > 0 ? dot * MathF.Tau / dotCount : 0f;
        // Start at the top and run clockwise (Y is down).
        return SpinnerCentre(screenSize) + new Vector2(MathF.Sin(angle), -MathF.Cos(angle)) * SpinnerRadius;
    }

    /// <summary>
    /// Brightness of one spinner dot: 1 for the dot the phase points at, then falling linearly
    /// for each dot further behind it down to <see cref="DotBrightnessFloor"/>, so the ring reads
    /// as a comet with a trailing tail.
    /// </summary>
    public static float DotBrightness(int dot, int dotCount, float phase)
    {
        if (dotCount <= 1)
            return 1f;

        var lead = (int)(MathUtils.Clamp01(phase) * dotCount) % dotCount;
        var behind = ((lead - dot) % dotCount + dotCount) % dotCount;
        var step = (1f - DotBrightnessFloor) / (dotCount - 1);
        return MathF.Max(DotBrightnessFloor, 1f - behind * step);
    }
}
