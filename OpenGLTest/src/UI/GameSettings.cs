using System;
using System.IO;
using System.Text.Json;

namespace silkgltriangle;

/// <summary>
/// Mutable player-facing settings with JSON persistence, shadowing the
/// <see cref="WorldGenerationConfig"/> constants they override. Every value is read live
/// (per frame or per draw), so changing one applies instantly with no rebuild; the config
/// constants stay the factory defaults <see cref="ResetToDefaults"/> restores.
/// </summary>
public static class GameSettings
{
    // Display
    public static bool VSync { get; set; }
    public static int ResolutionIndex { get; set; }
    /// <summary>The GUI Scale row's pick: 0 = Auto, 1.. index <see cref="GuiScales"/> (1-based).</summary>
    public static int GuiScaleIndex { get; set; }
    public static int MaxFpsIndex { get; set; }

    // Graphics
    public static bool Shadows { get; set; }
    public static float LeafSway { get; set; }
    public static float SunSize { get; set; }
    public static float SunGlow { get; set; }
    public static float CloudOpacity { get; set; }

    // Water
    public static bool WaterReflections { get; set; }
    public static int ReflectionIndex { get; set; }
    public static float WaterWaves { get; set; }
    public static float WaterRefraction { get; set; }
    public static float WaterCaustics { get; set; }
    public static float WaterSunShafts { get; set; }

    // Audio
    public static float AmbienceVolume { get; set; }
    public static float SfxVolume { get; set; }

    // Controls
    public static float MouseSensitivity { get; set; }
    public static float FieldOfView { get; set; }

    /// <summary>Window sizes offered by the resolution option row.</summary>
    public static readonly (int W, int H)[] Resolutions =
        [(1280, 720), (1600, 900), (1920, 1080), (2560, 1440)];

    /// <summary>Absolute multipliers of <see cref="UiCanvas.ReferenceSize"/> offered by the GUI Scale
    /// row, after its Auto entry.</summary>
    public static readonly float[] GuiScales = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f];

    /// <summary>The fixed GUI scale the player picked, or null for Auto (see
    /// <see cref="UiCanvas.EffectiveScale"/> for the fit cap).</summary>
    public static float? RequestedGuiScale =>
        GuiScaleIndex <= 0 ? null : GuiScales[Math.Clamp(GuiScaleIndex - 1, 0, GuiScales.Length - 1)];

    /// <summary>Frame-rate caps offered by the max-FPS option row; 0 means uncapped.</summary>
    public static readonly int[] MaxFpsOptions = [0, 30, 60, 120, 144, 240];

    /// <summary>Water reflection texture scales; the texture is this fraction of the viewport.</summary>
    public static readonly float[] ReflectionScales = [0.25f, 0.5f, 1f];

    public static void ResetToDefaults()
    {
        VSync = false;
        ResolutionIndex = 2;
        GuiScaleIndex = 0;
        MaxFpsIndex = 0;
        Shadows = WorldGenerationConfig.ShadowsEnabled;
        LeafSway = WorldGenerationConfig.LeafSwayStrength;
        SunSize = WorldGenerationConfig.SunAngularRadiusDeg;
        SunGlow = WorldGenerationConfig.SunGlowIntensity;
        CloudOpacity = WorldGenerationConfig.CloudOpacity;
        WaterReflections = true;
        ReflectionIndex = 1;
        WaterWaves = WorldGenerationConfig.WaterNormalScale;
        WaterRefraction = WorldGenerationConfig.WaterRefractStrength;
        WaterCaustics = WorldGenerationConfig.WaterCausticStrength;
        WaterSunShafts = WorldGenerationConfig.WaterRayStrength;
        AmbienceVolume = WorldGenerationConfig.AmbienceVolume;
        SfxVolume = WorldGenerationConfig.SfxVolume;
        MouseSensitivity = 0.2f;
        FieldOfView = 70f;
    }

    /// <summary>Where the JSON lives (outside the repo, so it never dirties the checkout).</summary>
    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CSVoxelEngine", "settings.json");

    /// <summary>Loads the JSON file, clamping every value into range; missing or broken files
    /// keep the current values (defaults at startup).</summary>
    public static void Load(string? path = null)
    {
        path ??= FilePath;
        SettingsDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return;
        }
        if (dto == null) return;

        if (dto.VSync is { } vSync) VSync = vSync;
        if (dto.ResolutionIndex is { } resolution) ResolutionIndex = Math.Clamp(resolution, 0, Resolutions.Length - 1);
        // Length, not Length - 1: index 0 is Auto and the fixed entries are 1..Length.
        if (dto.GuiScaleIndex is { } guiScale) GuiScaleIndex = Math.Clamp(guiScale, 0, GuiScales.Length);
        if (dto.MaxFpsIndex is { } maxFps) MaxFpsIndex = Math.Clamp(maxFps, 0, MaxFpsOptions.Length - 1);
        if (dto.Shadows is { } shadows) Shadows = shadows;
        if (dto.LeafSway is { } leafSway) LeafSway = Math.Clamp(leafSway, 0f, SettingsRows.LeafSwayMax);
        if (dto.SunSize is { } sunSize) SunSize = Math.Clamp(sunSize, SettingsRows.SunSizeMin, SettingsRows.SunSizeMax);
        if (dto.SunGlow is { } sunGlow) SunGlow = Math.Clamp(sunGlow, 0f, SettingsRows.SunGlowMax);
        if (dto.CloudOpacity is { } clouds) CloudOpacity = Math.Clamp(clouds, 0f, 1f);
        if (dto.WaterReflections is { } reflections) WaterReflections = reflections;
        if (dto.ReflectionIndex is { } reflection) ReflectionIndex = Math.Clamp(reflection, 0, ReflectionScales.Length - 1);
        if (dto.WaterWaves is { } waves) WaterWaves = Math.Clamp(waves, 0f, SettingsRows.WaterWavesMax);
        if (dto.WaterRefraction is { } refraction) WaterRefraction = Math.Clamp(refraction, 0f, SettingsRows.WaterRefractionMax);
        if (dto.WaterCaustics is { } caustics) WaterCaustics = Math.Clamp(caustics, 0f, SettingsRows.WaterCausticsMax);
        if (dto.WaterSunShafts is { } shafts) WaterSunShafts = Math.Clamp(shafts, 0f, SettingsRows.WaterSunShaftsMax);
        if (dto.AmbienceVolume is { } ambience) AmbienceVolume = Math.Clamp(ambience, 0f, 1f);
        if (dto.SfxVolume is { } sfx) SfxVolume = Math.Clamp(sfx, 0f, 1f);
        if (dto.MouseSensitivity is { } sensitivity) MouseSensitivity = Math.Clamp(sensitivity, SettingsRows.SensitivityMin, SettingsRows.SensitivityMax);
        if (dto.FieldOfView is { } fov) FieldOfView = Math.Clamp(fov, SettingsRows.FovMin, SettingsRows.FovMax);
    }

    public static void Save(string? path = null)
    {
        path ??= FilePath;
        var dto = new SettingsDto
        {
            VSync = VSync,
            ResolutionIndex = ResolutionIndex,
            GuiScaleIndex = GuiScaleIndex,
            MaxFpsIndex = MaxFpsIndex,
            Shadows = Shadows,
            LeafSway = LeafSway,
            SunSize = SunSize,
            SunGlow = SunGlow,
            CloudOpacity = CloudOpacity,
            WaterReflections = WaterReflections,
            ReflectionIndex = ReflectionIndex,
            WaterWaves = WaterWaves,
            WaterRefraction = WaterRefraction,
            WaterCaustics = WaterCaustics,
            WaterSunShafts = WaterSunShafts,
            AmbienceVolume = AmbienceVolume,
            SfxVolume = SfxVolume,
            MouseSensitivity = MouseSensitivity,
            FieldOfView = FieldOfView,
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.WriteLine($"Warning: settings not saved ({e.Message})");
        }
    }

    // Nullable so a settings file written before a field existed keeps that field's default
    // instead of clobbering it with 0/false (a missing SfxVolume must not mean "muted").
    private sealed class SettingsDto
    {
        public bool? VSync { get; set; }
        public int? ResolutionIndex { get; set; }
        public int? GuiScaleIndex { get; set; }
        public int? MaxFpsIndex { get; set; }
        public bool? Shadows { get; set; }
        public float? LeafSway { get; set; }
        public float? SunSize { get; set; }
        public float? SunGlow { get; set; }
        public float? CloudOpacity { get; set; }
        public bool? WaterReflections { get; set; }
        public int? ReflectionIndex { get; set; }
        public float? WaterWaves { get; set; }
        public float? WaterRefraction { get; set; }
        public float? WaterCaustics { get; set; }
        public float? WaterSunShafts { get; set; }
        public float? AmbienceVolume { get; set; }
        public float? SfxVolume { get; set; }
        public float? MouseSensitivity { get; set; }
        public float? FieldOfView { get; set; }
    }
}
