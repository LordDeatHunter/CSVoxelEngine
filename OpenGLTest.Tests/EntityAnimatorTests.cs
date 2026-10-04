using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class EntityAnimatorTests
{
    private const float Dt = 1f / 60f;
    private const float RefSpeed = 3f;

    private static AnimationClip.Keyframe[] Keys(AnimationClip.Interpolation mode, params (float Time, Vector3 Value)[] keys)
    {
        var frames = new AnimationClip.Keyframe[keys.Length];
        for (var i = 0; i < keys.Length; i++)
            frames[i] = new AnimationClip.Keyframe(keys[i].Time, keys[i].Value, mode);
        return frames;
    }

    private static AnimationClip Clip(string name, float length, AnimationClip.LoopMode loop,
        Dictionary<string, AnimationClip.BoneTrack> tracks, params AnimationClip.Event[] events) =>
        new(name, length, loop, tracks, events);

    private static AnimationClip ConstantPositionClip(string name, string bone, float y) =>
        Clip(name, 1f, AnimationClip.LoopMode.Loop, new Dictionary<string, AnimationClip.BoneTrack>
        {
            [bone] = new() { Position = Keys(AnimationClip.Interpolation.Linear, (0f, new Vector3(0f, y, 0f))) },
        });

    private static AnimationClip SwingClip(string name, params AnimationClip.Event[] events) =>
        Clip(name, 1f, AnimationClip.LoopMode.Once, new Dictionary<string, AnimationClip.BoneTrack>
        {
            ["ArmR"] = new()
            {
                Rotation = Keys(AnimationClip.Interpolation.Linear,
                    (0f, Vector3.Zero), (0.5f, new Vector3(90f, 0f, 0f)), (1f, Vector3.Zero)),
            },
        }, events);

    private static AnimationClip IdleRampClip() =>
        Clip(EntityAnimator.IdleClip, 2f, AnimationClip.LoopMode.Loop, new Dictionary<string, AnimationClip.BoneTrack>
        {
            ["Torso"] = new()
            {
                Position = Keys(AnimationClip.Interpolation.Linear,
                    (0f, Vector3.Zero), (1f, new Vector3(0f, 0.5f, 0f)), (2f, Vector3.Zero)),
            },
        });

    private static PlayerRig Rig()
    {
        var rig = new PlayerRig();
        var torso = new PlayerRig.RigPart { Pivot = new Vector3(0f, 1f, 0f) };
        torso.Cubes.Add(new PlayerRig.RigCube { Min = new Vector3(-0.25f, 0f, -0.1f), Max = new Vector3(0.25f, 0.5f, 0.1f) });
        rig.Parts["Torso"] = torso;
        rig.Parts["Head"] = new PlayerRig.RigPart { Pivot = new Vector3(0f, 2f, 0f), Parent = "Torso" };
        var arm = new PlayerRig.RigPart { Pivot = new Vector3(0.4f, 1.5f, 0f), Parent = "Torso" };
        arm.Cubes.Add(new PlayerRig.RigCube { Min = new Vector3(-0.1f, -0.3f, -0.1f), Max = new Vector3(0.1f, 0f, 0.1f) });
        arm.Cubes.Add(new PlayerRig.RigCube { Min = new Vector3(-0.05f, -0.6f, -0.05f), Max = new Vector3(0.05f, -0.3f, 0.05f) });
        rig.Parts["ArmR"] = arm;
        return rig;
    }

    private static void Run(EntityAnimator animator, float seconds, float speed)
    {
        var frames = (int)MathF.Round(seconds / Dt);
        for (var i = 0; i < frames; i++)
            animator.Update(Dt, speed);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.True(Vector3.Distance(expected, actual) <= tolerance,
            $"expected {expected}, got {actual}");
    }

    private static bool SameRotation(Quaternion a, Quaternion b, float tolerance) =>
        1f - MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))) <= tolerance;

    [Fact]
    public void WalkCrossfadesInWithSpeedAndBackOut()
    {
        var animator = new EntityAnimator(
            [ConstantPositionClip(EntityAnimator.IdleClip, "Torso", 0f), ConstantPositionClip(EntityAnimator.WalkClip, "Torso", 1f)],
            RefSpeed);

        Run(animator, 2f, RefSpeed);
        Assert.True(animator.LocalTransform("Torso").Translation.Y > 0.9f);

        Run(animator, 2f, 0f);
        Assert.True(animator.LocalTransform("Torso").Translation.Y < 0.05f);
    }

    [Fact]
    public void WalkPlaybackRateScalesWithSpeed()
    {
        var walk = Clip(EntityAnimator.WalkClip, 1f, AnimationClip.LoopMode.Loop, new Dictionary<string, AnimationClip.BoneTrack>
        {
            ["Torso"] = new()
            {
                Position = Keys(AnimationClip.Interpolation.Linear, (0f, Vector3.Zero), (1f, new Vector3(0f, 1f, 0f))),
            },
        });
        var a = new EntityAnimator([walk], RefSpeed);
        var b = new EntityAnimator([walk], RefSpeed);
        Run(a, 2f, RefSpeed);
        Run(b, 2f, RefSpeed);

        Run(a, 0.5f, RefSpeed);
        Run(b, 0.25f, RefSpeed * 2f);

        AssertNear(a.LocalTransform("Torso").Translation, b.LocalTransform("Torso").Translation, 1e-3f);
    }

    [Fact]
    public void ActionOverridesOnlyTheBonesItKeysAndReturnsToLocomotion()
    {
        AnimationClip[] clips = [IdleRampClip(), SwingClip("swing")];
        var acting = new EntityAnimator(clips, RefSpeed);
        var reference = new EntityAnimator(clips, RefSpeed);

        Run(acting, 0.3f, 0f);
        Run(reference, 0.3f, 0f);
        Assert.True(acting.Play("swing"));

        Run(acting, 0.5f, 0f);
        Run(reference, 0.5f, 0f);
        Assert.True(acting.IsActionPlaying);
        var actingTorso = acting.LocalTransform("Torso");
        var referenceTorso = reference.LocalTransform("Torso");
        AssertNear(referenceTorso.Translation, actingTorso.Translation, 1e-5f);
        Assert.True(SameRotation(referenceTorso.Rotation, actingTorso.Rotation, 1e-6f));
        Assert.False(SameRotation(Quaternion.Identity, acting.LocalTransform("ArmR").Rotation, 1e-3f));

        Run(acting, 0.6f, 0f);
        Assert.False(acting.IsActionPlaying);
        var arm = acting.LocalTransform("ArmR");
        Assert.True(SameRotation(Quaternion.Identity, arm.Rotation, 1e-3f));
        AssertNear(Vector3.Zero, arm.Translation, 1e-3f);
        AssertNear(Vector3.One, arm.Scale, 1e-3f);
    }

    [Fact]
    public void EventFiresExactlyOncePerPlay()
    {
        const float eventTime = 0.5f;
        var animator = new EntityAnimator([SwingClip("swing", new AnimationClip.Event(eventTime, "hit"))], RefSpeed);

        for (var play = 0; play < 2; play++)
        {
            Assert.True(animator.Play("swing"));
            var fired = 0;
            var elapsed = 0f;
            while (animator.IsActionPlaying && elapsed < 5f)
            {
                animator.Update(Dt, 0f);
                elapsed += Dt;
                if (!animator.ConsumeEvent("HIT")) continue;
                fired++;
                Assert.True(elapsed >= eventTime - 1e-4f, $"fired early at {elapsed}");
            }
            Assert.False(animator.IsActionPlaying);
            Assert.False(animator.ConsumeEvent("hit"));
            Assert.Equal(1, fired);
        }
    }

    [Fact]
    public void AnEventAuthoredAtTheStartFiresExactlyOncePerPlay()
    {
        var animator = new EntityAnimator([SwingClip("swing", new AnimationClip.Event(0f, "start"))], RefSpeed);

        for (var play = 0; play < 2; play++)
        {
            Assert.True(animator.Play("swing"));
            var fired = 0;
            var elapsed = 0f;
            while (animator.IsActionPlaying && elapsed < 5f)
            {
                animator.Update(Dt, 0f);
                elapsed += Dt;
                if (animator.ConsumeEvent("start")) fired++;
            }
            Assert.False(animator.IsActionPlaying);
            Assert.Equal(1, fired);
        }
    }

    [Fact]
    public void PlayIsIgnoredWhileTheSameActionPlays()
    {
        var animator = new EntityAnimator([SwingClip("swing")], RefSpeed);
        Assert.True(animator.Play("swing"));
        Assert.False(animator.Play("unknown"));

        var last = animator.ActionProgress;
        for (var i = 0; i < 40; i++)
        {
            animator.Update(Dt, 0f);
            if (i == 20) Assert.False(animator.Play("swing"));
            Assert.True(animator.ActionProgress >= last, "progress went backwards");
            last = animator.ActionProgress;
        }
        Assert.True(animator.IsPlaying("SWING"));
    }

    [Fact]
    public void ChildFollowsItsParentsRotation()
    {
        var idle = Clip(EntityAnimator.IdleClip, 1f, AnimationClip.LoopMode.Loop, new Dictionary<string, AnimationClip.BoneTrack>
        {
            ["Torso"] = new() { Rotation = Keys(AnimationClip.Interpolation.Linear, (0f, new Vector3(0f, 0f, 90f))) },
        });
        var animator = new EntityAnimator([idle], RefSpeed);

        var head = Vector3.Transform(Vector3.Zero, animator.GetPartMatrix("Head", Rig(), Vector3.Zero, 0f));

        AssertNear(new Vector3(-1f, 1f, 0f), head, 1e-4f);
    }

    [Fact]
    public void FacingAndFeetPlaceTheRoot()
    {
        var rig = Rig();
        var animator = new EntityAnimator(Array.Empty<AnimationClip>(), RefSpeed);
        var feet = new Vector3(10f, 64f, -3f);
        const float yaw = MathF.PI / 2f;

        var origin = Vector3.Transform(Vector3.Zero, animator.GetPartMatrix("Torso", rig, feet, yaw));

        var expected = feet + Vector3.Transform(rig.Parts["Torso"].Pivot, Matrix4x4.CreateRotationY(yaw));
        AssertNear(expected, origin, 1e-4f);
        Assert.Equal(Matrix4x4.Identity, animator.GetPartMatrix("NoSuchPart", rig, feet, yaw));
    }

    [Fact]
    public void HandAnchorSitsAtTheLowestCubeOfThePart()
    {
        var rig = Rig();
        var animator = new EntityAnimator(Array.Empty<AnimationClip>(), RefSpeed);
        var feet = new Vector3(2f, 5f, 7f);
        const float yaw = 0.7f;

        var lowest = float.MaxValue;
        foreach (var cube in rig.Parts["ArmR"].Cubes)
            lowest = MathF.Min(lowest, cube.Min.Y);

        var anchor = Vector3.Transform(Vector3.Zero, animator.HandAnchor("ArmR", rig, feet, yaw));
        var expected = Vector3.Transform(new Vector3(0f, lowest, 0f), animator.GetPartMatrix("ArmR", rig, feet, yaw));

        AssertNear(expected, anchor, 1e-4f);
    }
}
