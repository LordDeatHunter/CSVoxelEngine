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

    // Controls
    public static float MouseSensitivity { get; set; }
    public static float FieldOfView { get; set; }

    /// <summary>Window sizes offered by the resolution option row.</summary>
    public static readonly (int W, int H)[] Resolutions =
        [(1280, 720), (1600, 900), (1920, 1080), (2560, 1440)];

    /// <summary>Frame-rate caps offered by the max-FPS option row; 0 means uncapped.</summary>
    public static readonly int[] MaxFpsOptions = [0, 30, 60, 120, 144, 240];

    /// <summary>Water reflection texture scales; the texture is this fraction of the viewport.</summary>
    public static readonly float[] ReflectionScales = [0.25f, 0.5f, 1f];

    public static void ResetToDefaults()
    {
        VSync = false;
        ResolutionIndex = 2;
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

        VSync = dto.VSync;
        ResolutionIndex = Math.Clamp(dto.ResolutionIndex, 0, Resolutions.Length - 1);
        MaxFpsIndex = Math.Clamp(dto.MaxFpsIndex, 0, MaxFpsOptions.Length - 1);
        Shadows = dto.Shadows;
        LeafSway = Math.Clamp(dto.LeafSway, 0f, SettingsRows.LeafSwayMax);
        SunSize = Math.Clamp(dto.SunSize, SettingsRows.SunSizeMin, SettingsRows.SunSizeMax);
        SunGlow = Math.Clamp(dto.SunGlow, 0f, SettingsRows.SunGlowMax);
        CloudOpacity = Math.Clamp(dto.CloudOpacity, 0f, 1f);
        WaterReflections = dto.WaterReflections;
        ReflectionIndex = Math.Clamp(dto.ReflectionIndex, 0, ReflectionScales.Length - 1);
        WaterWaves = Math.Clamp(dto.WaterWaves, 0f, SettingsRows.WaterWavesMax);
        WaterRefraction = Math.Clamp(dto.WaterRefraction, 0f, SettingsRows.WaterRefractionMax);
        WaterCaustics = Math.Clamp(dto.WaterCaustics, 0f, SettingsRows.WaterCausticsMax);
        WaterSunShafts = Math.Clamp(dto.WaterSunShafts, 0f, SettingsRows.WaterSunShaftsMax);
        AmbienceVolume = Math.Clamp(dto.AmbienceVolume, 0f, 1f);
        MouseSensitivity = Math.Clamp(dto.MouseSensitivity, SettingsRows.SensitivityMin, SettingsRows.SensitivityMax);
        FieldOfView = Math.Clamp(dto.FieldOfView, SettingsRows.FovMin, SettingsRows.FovMax);
    }

    public static void Save(string? path = null)
    {
        path ??= FilePath;
        var dto = new SettingsDto
        {
            VSync = VSync,
            ResolutionIndex = ResolutionIndex,
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
            Console.WriteLine($"Warning: settings not saved ({e.Message})");
        }
    }

    private sealed class SettingsDto
    {
        public bool VSync { get; set; }
        public int ResolutionIndex { get; set; }
        public int MaxFpsIndex { get; set; }
        public bool Shadows { get; set; }
        public float LeafSway { get; set; }
        public float SunSize { get; set; }
        public float SunGlow { get; set; }
        public float CloudOpacity { get; set; }
        public bool WaterReflections { get; set; }
        public int ReflectionIndex { get; set; }
        public float WaterWaves { get; set; }
        public float WaterRefraction { get; set; }
        public float WaterCaustics { get; set; }
        public float WaterSunShafts { get; set; }
        public float AmbienceVolume { get; set; }
        public float MouseSensitivity { get; set; }
        public float FieldOfView { get; set; }
    }
}
