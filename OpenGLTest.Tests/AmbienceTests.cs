using System;
using System.IO;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class AmbienceTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(0.25f)]
    public void CrossfadeTargetsSplitMasterVolume(float dayness)
    {
        const float master = 0.4f;
        var (day, night) = AmbienceManager.ComputeTargetVolumes(dayness, master);
        Assert.Equal(dayness * master, day, 5);
        Assert.Equal((1f - dayness) * master, night, 5);
        Assert.Equal(master, day + night, 5);
    }

    [Fact]
    public void MissingFilesDisableGracefully()
    {
        using var ambience = new AmbienceManager("nope-day.mp3", "nope-night.mp3");
        Assert.False(ambience.Enabled);
        ambience.Update(1f, 0.016f);
        Assert.Equal(0f, ambience.DayVolume);
        Assert.Equal(0f, ambience.NightVolume);
    }

    [Theory]
    [InlineData("ambience/forest_ambience.mp3")]
    [InlineData("ambience/night_ambience.mp3")]
    public void RealMp3sDecodeWithoutOsCodecs(string fileName)
    {
        var path = FindSfxAsset(fileName);
        if (path == null)
            return;
        var (voice, _) = AmbienceManager.CreateLoopSource(path);
        using (voice)
        {
            var buffer = new byte[8192];
            var total = 0;
            int read;
            do
            {
                read = voice.ReadLoop(buffer, 0, buffer.Length);
                total += read;
            } while (read > 0 && total < 65536);
            Assert.True(total > 0);
        }
    }

    [Theory]
    [InlineData("ambience/forest_ambience.mp3")]
    [InlineData("ambience/night_ambience.mp3")]
    public void LoopWrapsIntoSecondPlaythrough(string fileName)
    {
        var path = FindSfxAsset(fileName);
        if (path == null)
            return;
        var (voice, _) = AmbienceManager.CreateLoopSource(path);
        using (voice)
        {
            var buffer = new byte[8192];
            var target = 2 * voice.SourceLength + buffer.Length;
            var cap = target / buffer.Length + 100;
            long total = 0;
            long secondHalfNonZero = 0;
            for (var i = 0; i < cap && total < target; i++)
            {
                var read = voice.ReadLoop(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                if (total >= voice.SourceLength)
                    foreach (var b in buffer[..read])
                        secondHalfNonZero += b != 0 ? 1 : 0;
                total += read;
            }
            Assert.True(total >= target);
            Assert.True(secondHalfNonZero > 0);
        }
    }

    [Fact]
    public void StartupScaleRisesFromSilenceToFull()
    {
        Assert.Equal(0f, AmbienceManager.StartupScale(0f));
        var mid = AmbienceManager.StartupScale(WorldGenerationConfig.AmbienceStartupFadeSeconds / 2f);
        Assert.InRange(mid, 0f, 1f);
        Assert.Equal(1f, AmbienceManager.StartupScale(WorldGenerationConfig.AmbienceStartupFadeSeconds));
        Assert.Equal(1f, AmbienceManager.StartupScale(float.MaxValue));
    }

    private static string? FindSfxAsset(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "OpenGLTest", "assets", "sfx", fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
