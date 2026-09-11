using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>What a click on the settings page landed on; Row indexes <see cref="SettingsRows.All"/>.</summary>
public enum SettingsHitKind
{
    None,
    Back,
    Toggle,
    Slider,
    Options,
}

public readonly record struct SettingsHit(SettingsHitKind Kind, int Row = -1, float Fraction = 0f);

public abstract record SettingsRow(string Section, string Label);
public sealed record ToggleRow(string Section, string Label, Func<bool> Get, Action<bool> Set)
    : SettingsRow(Section, Label);
public sealed record SliderRow(string Section, string Label, float Min, float Max, int Decimals,
    string Suffix, Func<float> Get, Action<float> Set) : SettingsRow(Section, Label);
public sealed record OptionsRow(string Section, string Label, string[] Options,
    Func<int> Get, Action<int> Set) : SettingsRow(Section, Label);

/// <summary>
/// The settings page's rows: section, label, widget kind and the getter/setter into
/// <see cref="GameSettings"/>. Setters only mutate + save (via <see cref="Program"/>); the
/// engine reads the values live, and window-level ones are synced per frame, so no row
/// needs its own apply hook.
/// </summary>
public static class SettingsRows
{
    public const float FogNearMin = 50f;
    public const float FogNearMax = 600f;
    public const float FogFarMin = 400f;
    public const float FogFarMax = 2500f;
    public const float DayLengthMin = 60f;
    public const float DayLengthMax = 1800f;
    public const float LeafSwayMax = 2f;
    public const float SunSizeMin = 0.5f;
    public const float SunSizeMax = 8f;
    public const float SunGlowMax = 1.5f;
    public const float WaterWavesMax = 4f;
    public const float WaterRefractionMax = 0.15f;
    public const float WaterCausticsMax = 1.5f;
    public const float WaterSunShaftsMax = 0.3f;
    public const float SensitivityMin = 0.05f;
    public const float SensitivityMax = 0.6f;
    public const float FovMin = 60f;
    public const float FovMax = 100f;

    public static readonly SettingsRow[] All =
    [
        new ToggleRow("Display", "VSync",
            () => GameSettings.VSync, v => GameSettings.VSync = v),
        new OptionsRow("Display", "Resolution",
            ["1280x720", "1600x900", "1920x1080", "2560x1440"],
            () => GameSettings.ResolutionIndex, v => GameSettings.ResolutionIndex = v),
        new OptionsRow("Display", "Max FPS",
            ["Unlimited", "30", "60", "120", "144", "240"],
            () => GameSettings.MaxFpsIndex, v => GameSettings.MaxFpsIndex = v),

        new ToggleRow("Graphics", "Shadows",
            () => GameSettings.Shadows, v => GameSettings.Shadows = v),
        new SliderRow("Graphics", "Fog Near", FogNearMin, FogNearMax, 0, "",
            () => GameSettings.FogNear, v => GameSettings.FogNear = v),
        new SliderRow("Graphics", "Fog Far", FogFarMin, FogFarMax, 0, "",
            () => GameSettings.FogFar, v => GameSettings.FogFar = v),
        new SliderRow("Graphics", "Day Length", DayLengthMin, DayLengthMax, 0, "s",
            () => GameSettings.DayLengthSeconds, v => GameSettings.DayLengthSeconds = v),
        new SliderRow("Graphics", "Leaf Sway", 0f, LeafSwayMax, 2, "",
            () => GameSettings.LeafSway, v => GameSettings.LeafSway = v),
        new SliderRow("Graphics", "Sun Size", SunSizeMin, SunSizeMax, 1, "deg",
            () => GameSettings.SunSize, v => GameSettings.SunSize = v),
        new SliderRow("Graphics", "Sun Glow", 0f, SunGlowMax, 2, "",
            () => GameSettings.SunGlow, v => GameSettings.SunGlow = v),
        new SliderRow("Graphics", "Clouds", 0f, 1f, 2, "",
            () => GameSettings.CloudOpacity, v => GameSettings.CloudOpacity = v),

        new ToggleRow("Water", "Reflections",
            () => GameSettings.WaterReflections, v => GameSettings.WaterReflections = v),
        new OptionsRow("Water", "Reflection Quality",
            ["Low", "Medium", "High"],
            () => GameSettings.ReflectionIndex, v => GameSettings.ReflectionIndex = v),
        new SliderRow("Water", "Waves", 0f, WaterWavesMax, 2, "",
            () => GameSettings.WaterWaves, v => GameSettings.WaterWaves = v),
        new SliderRow("Water", "Refraction", 0f, WaterRefractionMax, 3, "",
            () => GameSettings.WaterRefraction, v => GameSettings.WaterRefraction = v),
        new SliderRow("Water", "Caustics", 0f, WaterCausticsMax, 2, "",
            () => GameSettings.WaterCaustics, v => GameSettings.WaterCaustics = v),
        new SliderRow("Water", "Sun Shafts", 0f, WaterSunShaftsMax, 3, "",
            () => GameSettings.WaterSunShafts, v => GameSettings.WaterSunShafts = v),

        new SliderRow("Audio", "Ambience", 0f, 1f, 2, "",
            () => GameSettings.AmbienceVolume, v => GameSettings.AmbienceVolume = v),

        new SliderRow("Controls", "Mouse Sensitivity", SensitivityMin, SensitivityMax, 2, "",
            () => GameSettings.MouseSensitivity, v => GameSettings.MouseSensitivity = v),
        new SliderRow("Controls", "Field of View", FovMin, FovMax, 0, "deg",
            () => GameSettings.FieldOfView, v => GameSettings.FieldOfView = v),
    ];

    public static string FormatValue(SettingsRow row) => row switch
    {
        ToggleRow t => t.Get() ? "ON" : "OFF",
        SliderRow s => s.Get().ToString($"F{s.Decimals}") + (s.Suffix.Length > 0 ? $" {s.Suffix}" : ""),
        OptionsRow o => o.Options[Math.Clamp(o.Get(), 0, o.Options.Length - 1)],
        _ => "",
    };
}

/// <summary>Per-open state of the settings page: list scroll and the slider being dragged.</summary>
public sealed class SettingsMenuView
{
    public float ScrollOffset { get; set; }
    public int? DragRow { get; set; }
}

/// <summary>
/// Pure settings-page geometry (no GL): row rectangles, the slider track and click
/// hit-testing, so tests cover the targets the renderer draws. Y down, pixels.
/// </summary>
public static class SettingsLayout
{
    public const float PanelWidth = 640f;
    public const float PanelTop = 64f;
    public const float PanelBottomMargin = 16f;
    public const float TitleHeight = 44f;
    public const float SectionHeight = 30f;
    public const float RowHeight = 40f;
    public const float BackHeight = 52f;
    public const float BackGap = 12f;
    public const float LabelWidth = 210f;
    public const float ValueWidth = 100f;
    public const float TrackHeight = 8f;
    public const float ToggleSize = 24f;

    public static float PanelLeft(Vector2 screenSize) => (screenSize.X - PanelWidth) * 0.5f;

    public static float ListTop(Vector2 screenSize) => PanelTop + TitleHeight;

    public static float ListBottom(Vector2 screenSize) =>
        screenSize.Y - PanelBottomMargin - BackGap - BackHeight;

    public static Vector2 BackCentre(Vector2 screenSize) =>
        new(screenSize.X * 0.5f, ListBottom(screenSize) + BackGap + BackHeight * 0.5f);

    public static float ContentHeight()
    {
        var height = 0f;
        string? section = null;
        foreach (var row in SettingsRows.All)
        {
            if (row.Section != section)
            {
                section = row.Section;
                height += SectionHeight;
            }
            height += RowHeight;
        }
        return height;
    }

    public static float MaxScroll(Vector2 screenSize) =>
        Math.Max(0f, ContentHeight() - (ListBottom(screenSize) - ListTop(screenSize)));

    public static float RowTop(int row, float scrollOffset, Vector2 screenSize)
    {
        var y = ListTop(screenSize) - scrollOffset;
        string? section = null;
        for (var i = 0; i <= row; i++)
        {
            if (SettingsRows.All[i].Section != section)
            {
                section = SettingsRows.All[i].Section;
                y += SectionHeight;
            }
            if (i < row) y += RowHeight;
        }
        return y;
    }

    public static Vector2 RowCentre(int row, float scrollOffset, Vector2 screenSize) =>
        new(screenSize.X * 0.5f, RowTop(row, scrollOffset, screenSize) + RowHeight * 0.5f);

    /// <summary>Slider track rectangle: between the label column and the value slot.</summary>
    public static (Vector2 Centre, Vector2 Size) TrackRect(int row, float scrollOffset, Vector2 screenSize)
    {
        var left = PanelLeft(screenSize) + LabelWidth;
        var right = PanelLeft(screenSize) + PanelWidth - ValueWidth;
        var y = RowCentre(row, scrollOffset, screenSize).Y;
        return (new Vector2((left + right) * 0.5f, y), new Vector2(right - left, TrackHeight));
    }

    /// <summary>Right edge the slider/toggle value text aligns to.</summary>
    public static float ValueRight(Vector2 screenSize) =>
        PanelLeft(screenSize) + PanelWidth - 8f;

    public static Vector2 ToggleCentre(int row, float scrollOffset, Vector2 screenSize)
    {
        var (centre, _) = TrackRect(row, scrollOffset, screenSize);
        return centre;
    }

    public static SettingsHit HitAt(Vector2 point, float scrollOffset, Vector2 screenSize)
    {
        if (Math.Abs(point.X - screenSize.X * 0.5f) <= PauseMenuLayout.ButtonWidth * 0.5f
            && Math.Abs(point.Y - BackCentre(screenSize).Y) <= BackHeight * 0.5f)
            return new SettingsHit(SettingsHitKind.Back);

        if (point.X < PanelLeft(screenSize) || point.X > PanelLeft(screenSize) + PanelWidth
            || point.Y < ListTop(screenSize) || point.Y > ListBottom(screenSize))
            return new SettingsHit(SettingsHitKind.None);

        for (var i = 0; i < SettingsRows.All.Length; i++)
        {
            var top = RowTop(i, scrollOffset, screenSize);
            if (point.Y < top || point.Y >= top + RowHeight) continue;
            return SettingsRows.All[i] switch
            {
                ToggleRow => new SettingsHit(SettingsHitKind.Toggle, i),
                SliderRow => new SettingsHit(SettingsHitKind.Slider, i,
                    SliderFraction(point.X, i, scrollOffset, screenSize)),
                OptionsRow => new SettingsHit(SettingsHitKind.Options, i),
                _ => new SettingsHit(SettingsHitKind.None),
            };
        }
        return new SettingsHit(SettingsHitKind.None);
    }

    public static float SliderFraction(float x, int row, float scrollOffset, Vector2 screenSize)
    {
        var (centre, size) = TrackRect(row, scrollOffset, screenSize);
        return Math.Clamp((x - (centre.X - size.X * 0.5f)) / size.X, 0f, 1f);
    }

    public static float SliderValue(SliderRow row, float fraction) =>
        row.Min + fraction * (row.Max - row.Min);
}
