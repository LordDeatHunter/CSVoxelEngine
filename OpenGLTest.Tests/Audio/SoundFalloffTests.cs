using System.Numerics;
using VoxelEngine.Audio;
using Xunit;

namespace VoxelEngine.Tests.Audio;

/// <summary>
/// How far a positional sound carries (<see cref="SoundFalloff"/>): full volume close by, quieter the farther it is and
/// silent from the range on, with the earshot a server sends sounds within ending exactly where the gain does. And what
/// a 3D voice plays: a stereo clip folded to mono, each frame its two channels' average.
/// </summary>
public sealed class SoundFalloffTests
{
    [Fact]
    public void ASoundGetsQuieterWithDistanceUntilItIsSilentOutOfRange()
    {
        Assert.Equal(1f, SoundFalloff.Gain(0f));
        Assert.Equal(1f, SoundFalloff.Gain(AudioConfig.SfxFullVolumeBlocks));
        var previous = 1f;
        for (var d = AudioConfig.SfxFullVolumeBlocks + 1f; d < AudioConfig.SfxRangeBlocks; d += 1f)
        {
            var gain = SoundFalloff.Gain(d);
            Assert.True(gain < previous, $"not quieter at {d} blocks");
            Assert.True(gain > 0f, $"silent inside the range at {d} blocks");
            previous = gain;
        }
        Assert.Equal(0f, SoundFalloff.Gain(AudioConfig.SfxRangeBlocks));
        Assert.Equal(0f, SoundFalloff.Gain(AudioConfig.SfxRangeBlocks * 3f));
    }

    [Fact]
    public void TheEarshotEndsWhereTheGainFallsSilent()
    {
        var eye = new Vector3(10f, 70f, -4f);
        Assert.True(SoundFalloff.InRange(eye, eye + new Vector3(0f, 0f, AudioConfig.SfxRangeBlocks - 1f)));
        Assert.True(SoundFalloff.Gain(AudioConfig.SfxRangeBlocks - 1f) > 0f);
        Assert.False(SoundFalloff.InRange(eye, eye + new Vector3(0f, AudioConfig.SfxRangeBlocks + 1f, 0f)));
    }

    [Fact]
    public void AStereoClipFoldsToTheAverageOfEachFrame()
    {
        short[] stereo = [1000, 3000, -2000, 2000, short.MaxValue, short.MaxValue];
        Assert.Equal([2000, 0, short.MaxValue], SfxManager.Downmix(stereo));
    }
}
