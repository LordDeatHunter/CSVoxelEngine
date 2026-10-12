using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using Xunit;

namespace VoxelEngine.Tests.Entities;

public sealed class AnimationClipTests
{
    private const string Bone = "Torso";

    private static AnimationClip PositionClip(AnimationClip.Interpolation mode, AnimationClip.LoopMode loop,
        float length, params (float Time, float Y)[] keys)
    {
        var frames = new AnimationClip.Keyframe[keys.Length];
        for (var i = 0; i < keys.Length; i++)
            frames[i] = new AnimationClip.Keyframe(keys[i].Time, new Vector3(0f, keys[i].Y, 0f), mode);
        var tracks = new Dictionary<string, AnimationClip.BoneTrack>
        {
            [Bone] = new AnimationClip.BoneTrack { Position = frames },
        };
        return new AnimationClip("test", length, loop, tracks, Array.Empty<AnimationClip.Event>());
    }

    private static float Y(AnimationClip clip, float t) => clip.Sample(Bone, t).Translation.Y;

    [Fact]
    public void LinearSegmentIsProportional()
    {
        var clip = PositionClip(AnimationClip.Interpolation.Linear, AnimationClip.LoopMode.Once, 1f, (0f, 0f), (1f, 2f));
        Assert.Equal(0.25f * Y(clip, 1f), Y(clip, 0.25f), 1e-4f);
    }

    [Fact]
    public void StepHoldsTheEarlierKeyUntilTheNext()
    {
        var clip = PositionClip(AnimationClip.Interpolation.Step, AnimationClip.LoopMode.Once, 1f, (0f, 0f), (1f, 2f));
        Assert.Equal(Y(clip, 0f), Y(clip, 0.99f), 1e-6f);
        Assert.NotEqual(Y(clip, 0f), Y(clip, 1f));
        Assert.Equal(2f, Y(clip, 1f), 1e-6f);
    }

    [Fact]
    public void CatmullRomPassesThroughKeysAndOvershootsWhereLinearDoesNot()
    {
        (float, float)[] keys = [(0f, 0f), (1f, 1f), (2f, 1f), (3f, 0f)];
        var spline = PositionClip(AnimationClip.Interpolation.CatmullRom, AnimationClip.LoopMode.Once, 3f, keys);
        var linear = PositionClip(AnimationClip.Interpolation.Linear, AnimationClip.LoopMode.Once, 3f, keys);

        foreach (var (t, y) in keys)
            Assert.Equal(y, Y(spline, t), 1e-5f);
        Assert.True(Y(spline, 1.5f) > 1f);
        Assert.Equal(1f, Y(linear, 1.5f), 1e-6f);
    }

    [Fact]
    public void LoopWrapsAndHoldClamps()
    {
        (float, float)[] keys = [(0f, 0f), (2f, 4f)];
        var loop = PositionClip(AnimationClip.Interpolation.Linear, AnimationClip.LoopMode.Loop, 2f, keys);
        var hold = PositionClip(AnimationClip.Interpolation.Linear, AnimationClip.LoopMode.Hold, 2f, keys);
        var once = PositionClip(AnimationClip.Interpolation.Linear, AnimationClip.LoopMode.Once, 2f, keys);

        Assert.Equal(Y(loop, 0.5f), Y(loop, 2.5f), 1e-5f);
        Assert.Equal(Y(hold, 2f), Y(hold, 2.5f), 1e-5f);
        Assert.Equal(Y(once, 2f), Y(once, 2.5f), 1e-5f);
        Assert.NotEqual(Y(hold, 0.5f), Y(hold, 2.5f));
    }

    [Fact]
    public void UnkeyedBoneSamplesIdentity()
    {
        var clip = PositionClip(AnimationClip.Interpolation.Linear, AnimationClip.LoopMode.Loop, 1f, (0f, 0f), (1f, 2f));
        Assert.False(clip.Keys("Head"));
        Assert.Equal(AnimationClip.BoneTransform.Identity, clip.Sample("Head", 0.5f));

        // A keyed bone's unkeyed channels keep their identity values too.
        var torso = clip.Sample(Bone, 0.5f);
        Assert.Equal(Quaternion.Identity, torso.Rotation);
        Assert.Equal(Vector3.One, torso.Scale);
    }

    [Fact]
    public void RotationAppliesXThenYThenZ()
    {
        var hanging = new Vector3(0f, -1f, 0f);

        // X alone raises a hanging arm forward (-Z is the model's front).
        var raised = Vector3.Transform(hanging, AnimationClip.RotationFromDegrees(new Vector3(90f, 0f, 0f)));
        AssertNear(new Vector3(0f, 0f, -1f), raised);

        // X first, then Y: the raised arm then turns about the vertical axis.
        var turned = Vector3.Transform(hanging, AnimationClip.RotationFromDegrees(new Vector3(90f, 90f, 0f)));
        AssertNear(new Vector3(-1f, 0f, 0f), turned);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");
    }
}
