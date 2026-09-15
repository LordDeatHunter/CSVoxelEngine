using System.IO;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class GameSettingsTests
{
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
}
