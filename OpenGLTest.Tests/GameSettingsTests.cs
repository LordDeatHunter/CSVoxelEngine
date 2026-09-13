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
        Assert.Equal(WorldGenerationConfig.LeafSwayStrength, GameSettings.LeafSway);
        Assert.Equal(WorldGenerationConfig.SunAngularRadiusDeg, GameSettings.SunSize);
        Assert.Equal(WorldGenerationConfig.SunGlowIntensity, GameSettings.SunGlow);
        Assert.Equal(WorldGenerationConfig.CloudOpacity, GameSettings.CloudOpacity);
        Assert.Equal(WorldGenerationConfig.WaterNormalScale, GameSettings.WaterWaves);
        Assert.Equal(WorldGenerationConfig.WaterRefractStrength, GameSettings.WaterRefraction);
        Assert.Equal(WorldGenerationConfig.WaterCausticStrength, GameSettings.WaterCaustics);
        Assert.Equal(WorldGenerationConfig.WaterRayStrength, GameSettings.WaterSunShafts);
        Assert.Equal(WorldGenerationConfig.AmbienceVolume, GameSettings.AmbienceVolume);
        Assert.Equal(WorldGenerationConfig.SfxVolume, GameSettings.SfxVolume);
    }

    [Fact]
    public void SaveLoadRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            GameSettings.ResetToDefaults();
            GameSettings.WaterWaves = 3.5f;
            GameSettings.Shadows = !GameSettings.Shadows;
            GameSettings.SunGlow = 1.25f;
            GameSettings.ResolutionIndex = 0;
            GameSettings.MouseSensitivity = 0.42f;
            GameSettings.SfxVolume = 0.31f;
            GameSettings.Save(path);

            GameSettings.ResetToDefaults();
            Assert.Equal(WorldGenerationConfig.WaterNormalScale, GameSettings.WaterWaves);
            GameSettings.Load(path);

            Assert.Equal(3.5f, GameSettings.WaterWaves);
            Assert.Equal(!WorldGenerationConfig.ShadowsEnabled, GameSettings.Shadows);
            Assert.Equal(1.25f, GameSettings.SunGlow);
            Assert.Equal(0, GameSettings.ResolutionIndex);
            Assert.Equal(0.42f, GameSettings.MouseSensitivity);
            Assert.Equal(0.31f, GameSettings.SfxVolume);
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
            GameSettings.WaterCaustics = 0.9f;
            GameSettings.Load(path);
            Assert.Equal(0.9f, GameSettings.WaterCaustics);

            GameSettings.Load(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json"));
            Assert.Equal(0.9f, GameSettings.WaterCaustics);
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
            GameSettings.SunSize = -1000f;
            GameSettings.WaterCaustics = 1e9f;
            GameSettings.ResolutionIndex = 99;
            GameSettings.Save(path);

            GameSettings.ResetToDefaults();
            GameSettings.Load(path);

            Assert.Equal(SettingsRows.SunSizeMin, GameSettings.SunSize);
            Assert.Equal(SettingsRows.WaterCausticsMax, GameSettings.WaterCaustics);
            Assert.Equal(GameSettings.Resolutions.Length - 1, GameSettings.ResolutionIndex);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            GameSettings.ResetToDefaults();
        }
    }

    [Fact]
    public void MissingFieldsKeepCurrentValues()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{ \"VSync\": true }");
            GameSettings.ResetToDefaults();
            GameSettings.Load(path);

            Assert.True(GameSettings.VSync);
            Assert.Equal(WorldGenerationConfig.AmbienceVolume, GameSettings.AmbienceVolume);
            Assert.Equal(WorldGenerationConfig.SfxVolume, GameSettings.SfxVolume);
            Assert.Equal(WorldGenerationConfig.CloudOpacity, GameSettings.CloudOpacity);
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
    public void LayoutHitsBackTabsAndRows()
    {
        GameSettings.ResetToDefaults();
        var screen = new Vector2(1920f, 1080f);

        Assert.Equal(5, SettingsRows.Tabs.Length);
        Assert.Equal(3, SettingsRows.RowsInTab("Display").Length);
        Assert.Equal(6, SettingsRows.RowsInTab("Water").Length);

        var back = SettingsLayout.HitAt(SettingsLayout.BackCentre(screen), "Display", screen);
        Assert.Equal(SettingsHitKind.Back, back.Kind);

        var tab = SettingsLayout.HitAt(SettingsLayout.TabCentre(2, screen), "Display", screen);
        Assert.Equal(SettingsHitKind.Tab, tab.Kind);
        Assert.Equal(2, tab.Row);
        Assert.Equal("Water", SettingsRows.Tabs[tab.Row]);

        // Display tab row 0 is the VSync toggle; Graphics tab row 1 is the Leaf Sway slider.
        var toggle = SettingsLayout.HitAt(SettingsLayout.RowCentre(0, screen), "Display", screen);
        Assert.Equal(SettingsHitKind.Toggle, toggle.Kind);
        Assert.Equal(SettingsRows.RowsInTab("Display")[0], toggle.Row);

        var slider = SettingsLayout.HitAt(SettingsLayout.RowCentre(1, screen), "Graphics", screen);
        Assert.Equal(SettingsHitKind.Slider, slider.Kind);
        var abs = SettingsRows.RowsInTab("Graphics")[1];
        Assert.Equal(abs, slider.Row);
        Assert.Equal(1, SettingsLayout.TabRowOf("Graphics", abs));

        var (centre, size) = SettingsLayout.TrackRect(1, screen);
        Assert.Equal(0f, SettingsLayout.SliderFraction(centre.X - size.X, 1, screen));
        Assert.Equal(1f, SettingsLayout.SliderFraction(centre.X + size.X, 1, screen));
        if (SettingsRows.All[abs] is SliderRow sway)
            Assert.Equal(sway.Min, SettingsLayout.SliderValue(sway, 0f));

        var miss = SettingsLayout.HitAt(new Vector2(5f, 5f), "Display", screen);
        Assert.Equal(SettingsHitKind.None, miss.Kind);
    }
}
