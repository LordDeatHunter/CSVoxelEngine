using System;
using System.Linq;
using System.Numerics;

namespace silkgltriangle;

/// <summary>What a click on the settings page landed on; Row indexes <see cref="SettingsRows.All"/>,
/// except for Tab where it indexes <see cref="SettingsRows.Tabs"/>.</summary>
public enum SettingsHitKind
{
    None,
    Back,
    Tab,
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
/// needs its own apply hook. The GUI Scale row is read by <c>RenderManager.RenderUI</c> every
/// frame through <see cref="GameSettings.RequestedGuiScale"/>.
/// </summary>
public static class SettingsRows
{
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

    public static readonly string[] Tabs = ["Display", "Graphics", "Water", "Audio", "Controls"];

    /// <summary>The GUI Scale row's labels: Auto, then every <see cref="GameSettings.GuiScales"/> entry
    /// through <see cref="UiCanvas.FormatScale"/>, so the length is always 1 + GuiScales.Length.</summary>
    public static readonly string[] GuiScaleOptions = ["Auto", .. GameSettings.GuiScales.Select(UiCanvas.FormatScale)];

    /// <summary>A single static instance: the settings page recognises it by reference to show the
    /// effective scale while the pick is capped.</summary>
    public static readonly OptionsRow GuiScaleRow = new("Display", "GUI Scale", GuiScaleOptions,
        () => GameSettings.GuiScaleIndex, v => GameSettings.GuiScaleIndex = v);

    public static readonly SettingsRow[] All =
    [
        new ToggleRow("Display", "VSync",
            () => GameSettings.VSync, v => GameSettings.VSync = v),
        new OptionsRow("Display", "Resolution",
            ["1280x720", "1600x900", "1920x1080", "2560x1440"],
            () => GameSettings.ResolutionIndex, v => GameSettings.ResolutionIndex = v),
        GuiScaleRow,
        new OptionsRow("Display", "Max FPS",
            ["Unlimited", "30", "60", "120", "144", "240"],
            () => GameSettings.MaxFpsIndex, v => GameSettings.MaxFpsIndex = v),

        new ToggleRow("Graphics", "Shadows",
            () => GameSettings.Shadows, v => GameSettings.Shadows = v),
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
        new SliderRow("Audio", "SFX", 0f, 1f, 2, "",
            () => GameSettings.SfxVolume, v => GameSettings.SfxVolume = v),

        new SliderRow("Controls", "Mouse Sensitivity", SensitivityMin, SensitivityMax, 2, "",
            () => GameSettings.MouseSensitivity, v => GameSettings.MouseSensitivity = v),
        new ToggleRow("Controls", "Sprint Toggle",
            () => GameSettings.SprintToggle, v => GameSettings.SprintToggle = v),
        new SliderRow("Controls", "Field of View", FovMin, FovMax, 0, "deg",
            () => GameSettings.FieldOfView, v => GameSettings.FieldOfView = v),
    ];

    /// <summary>Indexes into <see cref="All"/> of the rows on one tab, in draw order.</summary>
    public static int[] RowsInTab(string tab)
    {
        var rows = new int[All.Length];
        var n = 0;
        for (var i = 0; i < All.Length; i++)
            if (All[i].Section == tab)
                rows[n++] = i;
        Array.Resize(ref rows, n);
        return rows;
    }

    public static string FormatValue(SettingsRow row) => row switch
    {
        ToggleRow t => t.Get() ? "ON" : "OFF",
        SliderRow s => s.Get().ToString($"F{s.Decimals}") + (s.Suffix.Length > 0 ? $" {s.Suffix}" : ""),
        OptionsRow o => o.Options[Math.Clamp(o.Get(), 0, o.Options.Length - 1)],
        _ => "",
    };
}

/// <summary>Per-open state of the settings page: active tab and the slider being dragged
/// (an index into <see cref="SettingsRows.All"/>).</summary>
public sealed class SettingsMenuView
{
    public string Tab { get; set; } = SettingsRows.Tabs[0];
    public int? DragRow { get; set; }
}

/// <summary>
/// Pure settings-page geometry (no GL): tab strip, row rectangles, the slider track and
/// click hit-testing, so tests cover the targets the renderer draws. Y down, pixels.
/// Rows are addressed two ways: tab-relative for geometry, absolute (<see cref="SettingsRows.All"/>)
/// in hit results.
/// </summary>
public static class SettingsLayout
{
    public const float PanelWidth = 880f;
    public const float PanelTop = 64f;
    public const float PanelBottomMargin = 16f;
    public const float TitleHeight = 44f;
    public const float TabHeight = 52f;
    public const float RowHeight = 46f;
    public const float BackHeight = 52f;
    public const float BackGap = 12f;
    public const float LabelWidth = 210f;
    public const float ValueWidth = 100f;
    public const float TrackHeight = 8f;
    public const float ToggleSize = 24f;

    public static float PanelLeft(Vector2 screenSize) => (screenSize.X - PanelWidth) * 0.5f;

    public static float ListTop(Vector2 screenSize) => PanelTop + TitleHeight + TabHeight;

    public static float ListBottom(Vector2 screenSize) =>
        screenSize.Y - PanelBottomMargin - BackGap - BackHeight;

    public static Vector2 BackCentre(Vector2 screenSize) =>
        new(screenSize.X * 0.5f, ListBottom(screenSize) + BackGap + BackHeight * 0.5f);

    public static float TabWidth() => PanelWidth / SettingsRows.Tabs.Length;

    public static Vector2 TabCentre(int tab, Vector2 screenSize) =>
        new(PanelLeft(screenSize) + TabWidth() * (tab + 0.5f),
            PanelTop + TitleHeight + TabHeight * 0.5f);

    public static float RowTop(int tabRow, Vector2 screenSize) =>
        ListTop(screenSize) + tabRow * RowHeight;

    public static Vector2 RowCentre(int tabRow, Vector2 screenSize) =>
        new(screenSize.X * 0.5f, RowTop(tabRow, screenSize) + RowHeight * 0.5f);

    /// <summary>Tab-relative position of an absolute row on its tab (-1 when off-tab).</summary>
    public static int TabRowOf(string tab, int absRow)
    {
        var rows = SettingsRows.RowsInTab(tab);
        for (var i = 0; i < rows.Length; i++)
            if (rows[i] == absRow)
                return i;
        return -1;
    }

    /// <summary>Slider track rectangle: between the label column and the value slot.</summary>
    public static (Vector2 Centre, Vector2 Size) TrackRect(int tabRow, Vector2 screenSize)
    {
        var left = PanelLeft(screenSize) + LabelWidth;
        var right = PanelLeft(screenSize) + PanelWidth - ValueWidth;
        var y = RowCentre(tabRow, screenSize).Y;
        return (new Vector2((left + right) * 0.5f, y), new Vector2(right - left, TrackHeight));
    }

    /// <summary>Right edge the slider/toggle value text aligns to.</summary>
    public static float ValueRight(Vector2 screenSize) =>
        PanelLeft(screenSize) + PanelWidth - 8f;

    public static Vector2 ToggleCentre(int tabRow, Vector2 screenSize) =>
        TrackRect(tabRow, screenSize).Centre;

    public static SettingsHit HitAt(Vector2 point, string tab, Vector2 screenSize)
    {
        if (Math.Abs(point.X - screenSize.X * 0.5f) <= PauseMenuLayout.ButtonWidth * 0.5f
            && Math.Abs(point.Y - BackCentre(screenSize).Y) <= BackHeight * 0.5f)
            return new SettingsHit(SettingsHitKind.Back);

        var tabTop = PanelTop + TitleHeight;
        if (point.Y >= tabTop && point.Y < tabTop + TabHeight
            && point.X >= PanelLeft(screenSize)
            && point.X < PanelLeft(screenSize) + PanelWidth)
        {
            var t = (int)((point.X - PanelLeft(screenSize)) / TabWidth());
            if (t >= 0 && t < SettingsRows.Tabs.Length)
                return new SettingsHit(SettingsHitKind.Tab, t);
        }

        var rows = SettingsRows.RowsInTab(tab);
        for (var r = 0; r < rows.Length; r++)
        {
            var top = RowTop(r, screenSize);
            if (point.Y < top || point.Y >= top + RowHeight) continue;
            if (point.X < PanelLeft(screenSize)
                || point.X > PanelLeft(screenSize) + PanelWidth)
                return new SettingsHit(SettingsHitKind.None);
            return SettingsRows.All[rows[r]] switch
            {
                ToggleRow => new SettingsHit(SettingsHitKind.Toggle, rows[r]),
                SliderRow => new SettingsHit(SettingsHitKind.Slider, rows[r],
                    SliderFraction(point.X, r, screenSize)),
                OptionsRow => new SettingsHit(SettingsHitKind.Options, rows[r]),
                _ => new SettingsHit(SettingsHitKind.None),
            };
        }
        return new SettingsHit(SettingsHitKind.None);
    }

    public static float SliderFraction(float x, int tabRow, Vector2 screenSize)
    {
        var (centre, size) = TrackRect(tabRow, screenSize);
        return MathUtils.Clamp01((x - (centre.X - size.X * 0.5f)) / size.X);
    }

    public static float SliderValue(SliderRow row, float fraction) =>
        row.Min + fraction * (row.Max - row.Min);
}
