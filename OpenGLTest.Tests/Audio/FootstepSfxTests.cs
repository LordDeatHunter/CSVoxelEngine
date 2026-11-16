using VoxelEngine.Audio;
using VoxelEngine.Content.Blocks;
using Xunit;

namespace VoxelEngine.Tests.Audio;

public class FootstepSfxTests
{
    [Fact]
    public void OnlyAWalkOnTheGroundIsHeard()
    {
        var speed = AudioConfig.FootstepMinSpeed + 1f;
        Assert.True(FootstepSfx.Audible(true, false, false, false, speed));
        Assert.False(FootstepSfx.Audible(false, false, false, false, speed));
        Assert.False(FootstepSfx.Audible(true, true, false, false, speed));
        Assert.False(FootstepSfx.Audible(true, false, true, false, speed));
        Assert.False(FootstepSfx.Audible(true, false, false, true, speed));
        Assert.False(FootstepSfx.Audible(true, false, false, false, AudioConfig.FootstepMinSpeed * 0.5f));
    }

    [Fact]
    public void AStrideTakesOneFootfallPerStrideWalkedAndNeverBanksMore()
    {
        var acc = 0f;
        var steps = 0;
        const int frames = 600;
        var perFrame = AudioConfig.MobStrideBlocks / 10f;
        for (var i = 0; i < frames; i++)
            if (FootstepSfx.Stride(ref acc, perFrame)) steps++;
        Assert.InRange(steps, frames / 10 - 1, frames / 10);

        acc = 0f;
        Assert.True(FootstepSfx.Stride(ref acc, AudioConfig.MobStrideBlocks * 5f));
        Assert.False(FootstepSfx.Stride(ref acc, 0f));
    }

    [Fact]
    public void AnUnsetOrUnknownStepGroupIsSilent()
    {
        Assert.Null(BlockLoader.ResolveStepSfx("test.json", null));
        Assert.Null(BlockLoader.ResolveStepSfx("test.json", " "));
        Assert.Null(BlockLoader.ResolveStepSfx("test.json", "not_a_group"));
    }
}
