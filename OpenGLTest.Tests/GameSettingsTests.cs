using System.IO;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class GameSettingsTests
{
    [Fact]
    public void DefaultsMirrorWorldgenConfig()
    {
        GameSettings.ResetToDefaults();
        Assert.Equal(WorldGenerationConfig.ShadowsEnabled, GameSettings.Shadows);
        Assert.Equal(WorldGenerationConfig.FogNearBlocks, GameSettings.FogNear);
        Assert.Equal(WorldGenerationConfig.FogFarBlocks, GameSettings.FogFar);
        Assert.Equal(WorldGenerationConfig.DayLengthSeconds, GameSettings.DayLengthSeconds);
        Assert.Equal(WorldGenerationConfig.LeafSwayStrength, GameSettings.LeafSway);
        Assert.Equal(WorldGenerationConfig.SunAngularRadiusDeg, GameSettings.SunSize);
        Assert.Equal(WorldGenerationConfig.SunGlowIntensity, GameSettings.SunGlow);
        Assert.Equal(WorldGenerationConfig.CloudOpacity, GameSettings.CloudOpacity);
        Assert.Equal(WorldGenerationConfig.WaterNormalScale, GameSettings.WaterWaves);
        Assert.Equal(WorldGenerationConfig.WaterRefractStrength, GameSettings.WaterRefraction);
        Assert.Equal(WorldGenerationConfig.WaterCausticStrength, GameSettings.WaterCaustics);
        Assert.Equal(WorldGenerationConfig.WaterRayStrength, GameSettings.WaterSunShafts);
        Assert.Equal(WorldGenerationConfig.AmbienceVolume, GameSettings.AmbienceVolume);
    }

    [Fact]
    public void SaveLoadRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            GameSettings.ResetToDefaults();
            GameSettings.FogNear = 123f;
            GameSettings.Shadows = !GameSettings.Shadows;
            GameSettings.SunGlow = 1.25f;
            GameSettings.ResolutionIndex = 0;
            GameSettings.MouseSensitivity = 0.42f;
            GameSettings.Save(path);

            GameSettings.ResetToDefaults();
            Assert.Equal(WorldGenerationConfig.FogNearBlocks, GameSettings.FogNear);
            GameSettings.Load(path);

            Assert.Equal(123f, GameSettings.FogNear);
            Assert.Equal(!WorldGenerationConfig.ShadowsEnabled, GameSettings.Shadows);
            Assert.Equal(1.25f, GameSettings.SunGlow);
            Assert.Equal(0, GameSettings.ResolutionIndex);
            Assert.Equal(0.42f, GameSettings.MouseSensitivity);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            GameSettings.ResetToDefaults();
        }
    }

    [Fact]
    public void BrokenFileKeepsCurrentValues()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{ not json");
            GameSettings.ResetToDefaults();
            GameSettings.FogFar = 999f;
            GameSettings.Load(path);
            Assert.Equal(999f, GameSettings.FogFar);

            GameSettings.Load(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json"));
            Assert.Equal(999f, GameSettings.FogFar);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            GameSettings.ResetToDefaults();
        }
    }

    [Fact]
    public void OutOfRangeValuesClampOnLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            GameSettings.ResetToDefaults();
            GameSettings.FogNear = -1000f;
            GameSettings.FogFar = 1e9f;
            GameSettings.ResolutionIndex = 99;
            GameSettings.Save(path);

            GameSettings.ResetToDefaults();
            GameSettings.Load(path);

            Assert.Equal(SettingsRows.FogNearMin, GameSettings.FogNear);
            Assert.Equal(SettingsRows.FogFarMax, GameSettings.FogFar);
            Assert.Equal(GameSettings.Resolutions.Length - 1, GameSettings.ResolutionIndex);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            GameSettings.ResetToDefaults();
        }
    }

    [Fact]
    public void RowsAreWellFormed()
    {
        Assert.NotEmpty(SettingsRows.All);
        foreach (var row in SettingsRows.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Label));
            Assert.False(string.IsNullOrWhiteSpace(row.Section));
            switch (row)
            {
                case SliderRow s:
                    Assert.True(s.Min < s.Max);
                    break;
                case OptionsRow o:
                    Assert.NotEmpty(o.Options);
                    Assert.InRange(o.Get(), 0, o.Options.Length - 1);
                    break;
            }
            Assert.False(string.IsNullOrWhiteSpace(SettingsRows.FormatValue(row)));
        }
    }

    [Fact]
    public void LayoutHitsBackAndFirstRows()
    {
        GameSettings.ResetToDefaults();
        var screen = new Vector2(1920f, 1080f);
        Assert.True(SettingsLayout.MaxScroll(screen) > 0f);

        var back = SettingsLayout.HitAt(SettingsLayout.BackCentre(screen), 0f, screen);
        Assert.Equal(SettingsHitKind.Back, back.Kind);

        // Row 0 is the VSync toggle; row 4 the first slider (Fog Near).
        var toggle = SettingsLayout.HitAt(SettingsLayout.RowCentre(0, 0f, screen), 0f, screen);
        Assert.Equal(SettingsHitKind.Toggle, toggle.Kind);
        Assert.Equal(0, toggle.Row);

        var slider = SettingsLayout.HitAt(SettingsLayout.RowCentre(4, 0f, screen), 0f, screen);
        Assert.Equal(SettingsHitKind.Slider, slider.Kind);

        var (centre, size) = SettingsLayout.TrackRect(4, 0f, screen);
        Assert.Equal(0f, SettingsLayout.SliderFraction(centre.X - size.X, 4, 0f, screen));
        Assert.Equal(1f, SettingsLayout.SliderFraction(centre.X + size.X, 4, 0f, screen));
        if (SettingsRows.All[4] is SliderRow fog)
            Assert.Equal(fog.Min, SettingsLayout.SliderValue(fog, 0f));

        var miss = SettingsLayout.HitAt(new Vector2(5f, 5f), 0f, screen);
        Assert.Equal(SettingsHitKind.None, miss.Kind);
    }
}
