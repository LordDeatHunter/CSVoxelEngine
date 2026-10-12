using System;
using System.IO;
using VoxelEngine.Audio;
using VoxelEngine.UI;
using Xunit;

namespace VoxelEngine.Tests.Audio;

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

    [Fact]
    public void UpdateTracksTheAmbienceVolumeSetting()
    {
        var path = FindSfxAsset("ambience/forest_ambience.mp3");
        if (path == null) return;
        using var ambience = new AmbienceManager(path, path);
        if (!ambience.Enabled) return;

        try
        {
            GameSettings.ResetToDefaults();
            GameSettings.AmbienceVolume = 1f;
            for (var i = 0; i < 400; i++) ambience.Update(1f, 0.016f);
            var loud = ambience.DayVolume;

            GameSettings.AmbienceVolume = 0.05f;
            for (var i = 0; i < 400; i++) ambience.Update(1f, 0.016f);
            var quiet = ambience.DayVolume;

            Assert.True(quiet < loud * 0.5f, $"day volume {loud} -> {quiet}");
        }
        finally
        {
            GameSettings.ResetToDefaults();
        }
    }

    [Fact]
    public void MasterVolumeChangeIsInstant()
    {
        var path = FindSfxAsset("ambience/forest_ambience.mp3");
        if (path == null) return;
        using var ambience = new AmbienceManager(path, path);
        if (!ambience.Enabled) return;

        try
        {
            GameSettings.ResetToDefaults();
            GameSettings.AmbienceVolume = 1f;
            // Past the 8 s startup fade, so the master scale is exactly 1.
            for (var i = 0; i < 600; i++) ambience.Update(1f, 0.016f);
            Assert.Equal(1f, ambience.DayVolume, 3);

            GameSettings.AmbienceVolume = 0f;
            ambience.Update(1f, 0.016f);
            Assert.Equal(0f, ambience.DayVolume, 3);
        }
        finally
        {
            GameSettings.ResetToDefaults();
        }
    }

    [Fact]
    public void VolumeStageScalesAndSilencesTheLoop()
    {
        var path = FindSfxAsset("ambience/forest_ambience.mp3");
        if (path == null) return;
        var (voice, volume) = AmbienceManager.CreateLoopSource(path);
        using (voice)
        {
            volume.Volume = 1f;
            var loud = new float[8192];
            var read = volume.Read(loud, 0, loud.Length);
            Assert.True(read > 0);
            Assert.Contains(loud, sample => sample != 0f);

            volume.Volume = 0f;
            var silent = new float[8192];
            read = volume.Read(silent, 0, silent.Length);
            Assert.True(read > 0);
            Assert.All(silent, sample => Assert.Equal(0f, sample));
        }
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
