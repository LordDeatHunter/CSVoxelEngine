using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Player.Posing;
using Xunit;

namespace VoxelEngine.Tests.Particles;

/// <summary>
/// The slice trail's samples, fed from a real <see cref="PlayerModel"/> swinging a real blade the way
/// the world feeds it: the head stays on the drawn tip, the ring is bounded and covers the trail's life
/// at any update rate, a still or held tip commits nothing, and the samples ride the owner frame.
/// </summary>
public sealed class SliceTrailTests
{
    public SliceTrailTests() => TestContent.EnsureLoaded();

    private static Item Blade => ItemRegistry.TryGet(ContentRegistry.RequireItem("obsidian_blade").Id)!;
    private static Item Sword => ItemRegistry.TryGet(ContentRegistry.RequireItem("iron_sword").Id)!;

    // Drives one player model and its trail the way the world does: the model updates, then the trail
    // samples the drawn blade.
    private sealed class Driver(Item item, double hz)
    {
        public readonly PlayerModel Player = new();
        public readonly SliceTrail Trail = new();
        public readonly double Dt = 1.0 / hz;
        public Vector3 Eye = new(3f, 20f, -7f);
        public float Yaw = -90f;
        public double Now = 10.0;

        public Matrix4x4 BladeMatrix => HeldItemPose.SpriteMatrix(Player, item, Eye, Yaw, 0f, 0f);

        public Vector3 DrawnTip => Vector3.Transform(SliceTrail.BladePoint(ParticleConfig.SliceTrailTipReach), BladeMatrix);

        public void Step()
        {
            Now += Dt;
            Player.Update((float)Dt, 0f);
            Feed(BladeMatrix);
        }

        public void Feed(Matrix4x4 blade)
        {
            var owner = SliceTrail.OwnerFrameAt(Eye, Player.DrawnBodyYaw(Yaw));
            Trail.Update(Now, SliceTrail.IsBladeSwing(Player), SliceTrail.SwingProgress(Player),
                Player.IsHitStopped, blade, owner);
        }

        public void StepUntil(Func<bool> done, int limit = 100_000)
        {
            for (var i = 0; i < limit && !done(); i++)
                Step();
        }

        public Vector3 Head => Trail.TipAt(Trail.Count - 1);

        public Vector3 HeadWorld => Vector3.Transform(Head, Trail.OwnerFrame);

        public double OldestAge => Now - Trail.TimeAt(0);

        // The time of the newest committed sample: it moves exactly when a sample is committed
        public double NewestCommitTime => Trail.CommittedCount > 0 ? Trail.TimeAt(Trail.CommittedCount - 1) : -1.0;

        public float NewestCommitStrength => Trail.StrengthAt(Trail.CommittedCount - 1);
    }

    [Fact]
    public void NoSamplesWithoutASwing()
    {
        var d = new Driver(Blade, 60);
        for (var i = 0; i < 120; i++)
        {
            d.Step();
            Assert.Equal(0, d.Trail.Count);
        }
    }

    [Theory]
    [InlineData(60)]
    [InlineData(1000)]
    public void TheHeadSitsOnTheDrawnTip(int hz)
    {
        var d = new Driver(Blade, hz);
        d.Player.StartSlice(1);
        d.Step();
        while (d.Player.IsSlicing)
        {
            Assert.True(Vector3.Distance(d.HeadWorld, d.DrawnTip) < 1e-3f,
                $"head {d.HeadWorld} off the tip {d.DrawnTip} at progress {d.Player.SliceProgress}");
            d.Step();
        }
    }

    [Fact]
    public void CapacityIsBounded()
    {
        var d = new Driver(Blade, 1000);
        var next = 0;
        for (var i = 0; i < 2000; i++)
        {
            if (!SliceTrail.IsBladeSwing(d.Player))
            {
                switch (next++ % 5)
                {
                    case 0: d.Player.StartSlice(1); break;
                    case 1: d.Player.StartSlice(-1); break;
                    case 2: d.Player.StartSword(SwordSwing.Cut); break;
                    case 3: d.Player.StartSword(SwordSwing.BackCut); break;
                    default: d.Player.StartSword(SwordSwing.Chop); break;
                }
            }
            d.Step();
            Assert.InRange(d.Trail.Count, 0, ParticleConfig.SliceTrailMaxSamples + 1);
        }
    }

    [Fact]
    public void TheTrailCoversItsLifeAtAnyUpdateRate()
    {
        double AgeAtStrikeEnd(int hz)
        {
            var d = new Driver(Blade, hz);
            d.Player.StartSlice(1);
            d.StepUntil(() => d.Player.SliceProgress >= SliceMotion.SliceStrikeEnd);
            return d.OldestAge;
        }

        var slow = AgeAtStrikeEnd(60);
        var fast = AgeAtStrikeEnd(1000);

        Assert.True(Math.Abs(fast - slow) <= 1.0 / 60.0 + ParticleConfig.SliceTrailMinSampleSeconds,
            $"oldest sample {fast:F4}s old at 1000 Hz but {slow:F4}s at 60 Hz");
    }

    [Fact]
    public void SamplesExpireAfterTheirLife()
    {
        var d = new Driver(Blade, 120);
        d.Player.StartSlice(1);
        d.Step();
        while (d.Player.IsSlicing)
        {
            for (var i = 0; i < d.Trail.Count; i++)
                Assert.True(d.Now - d.Trail.TimeAt(i) <= ParticleConfig.SliceTrailLifeSeconds + 1e-9);
            d.Step();
        }
        Assert.True(d.Trail.Count > 0);

        var end = d.Now;
        d.StepUntil(() => d.Now - end > ParticleConfig.SliceTrailLifeSeconds + d.Dt);
        Assert.Equal(0, d.Trail.Count);
    }

    [Fact]
    public void AFrozenTipCommitsNothing()
    {
        var d = new Driver(Blade, 1000);
        d.Player.StartSlice(1);
        d.Step();
        var frozen = d.BladeMatrix;
        void FrozenStep()
        {
            d.Now += d.Dt;
            d.Player.Update((float)d.Dt, 0f);
            Assert.True(d.Player.IsSlicing);
            d.Feed(frozen);
        }
        FrozenStep();
        var committed = d.Trail.CommittedCount;
        var count = d.Trail.Count;
        var newest = d.NewestCommitTime;

        var start = d.Now;
        while (d.Now - start < 0.2)
        {
            FrozenStep();
            Assert.Equal(committed, d.Trail.CommittedCount);
            Assert.Equal(count, d.Trail.Count);
            Assert.Equal(newest, d.NewestCommitTime);
        }
    }

    [Fact]
    public void AHitStopLeavesNoWidthNotch()
    {
        // The same slice at the same rate, once plain and once held at its impact: the strength of the
        // first sample committed after the hold, against the last one before it and against the plain swing.
        var plain = new Driver(Blade, 240);
        var plainByProgress = new List<(float Progress, float Strength)>();
        plain.Player.StartSlice(1);
        plain.Step();
        while (plain.Player.IsSlicing)
        {
            var before = plain.NewestCommitTime;
            plain.Step();
            if (plain.Player.IsSlicing && plain.NewestCommitTime != before)
                plainByProgress.Add((plain.Player.SliceProgress, plain.NewestCommitStrength));
        }

        var hit = new Driver(Blade, 240);
        hit.Player.StartSlice(1);
        hit.Step();
        var requested = false;
        var held = false;
        float? lastBefore = null;
        float? firstAfter = null;
        var progressAfter = 0f;
        while (hit.Player.IsSlicing && firstAfter == null)
        {
            if (!requested && hit.Player.SliceProgress >= PlayerModel.SliceImpactProgress)
            {
                hit.Player.HitStop(0.1f);
                requested = true;
            }
            var before = hit.NewestCommitTime;
            hit.Step();
            held |= hit.Player.IsHitStopped;
            if (hit.NewestCommitTime == before)
                continue;
            // Nothing commits while the hold lasts
            Assert.False(hit.Player.IsHitStopped);
            if (!held)
            {
                lastBefore = hit.NewestCommitStrength;
                continue;
            }
            firstAfter = hit.NewestCommitStrength;
            progressAfter = hit.Player.SliceProgress;
        }

        Assert.True(held);
        Assert.NotNull(lastBefore);
        Assert.NotNull(firstAfter);
        var plainAtProgress = plainByProgress.MinBy(p => Math.Abs(p.Progress - progressAfter)).Strength;
        Assert.True(firstAfter >= lastBefore - 0.1f || firstAfter >= plainAtProgress - 0.1f,
            $"strength {firstAfter} after the hold, {lastBefore} before it, {plainAtProgress} unheld");
    }

    [Fact]
    public void ARestartedSwingStartsANewSegment()
    {
        var d = new Driver(Blade, 120);
        d.Player.StartSlice(1);
        d.StepUntil(() => d.Player.SliceProgress >= SliceMotion.SliceStrikeEnd);
        var first = d.Trail.SegmentAt(d.Trail.Count - 1);

        d.Player.StartSlice(-1);
        d.Step();
        var second = d.Trail.SegmentAt(d.Trail.Count - 1);

        Assert.NotEqual(first, second);
        Assert.Equal(first, d.Trail.SegmentAt(0));
        for (var i = 1; i < d.Trail.Count; i++)
            Assert.True(d.Trail.SegmentAt(i) >= d.Trail.SegmentAt(i - 1));
        Assert.Equal(0f, d.Trail.AlongAt(d.Trail.Count - 1));
    }

    [Fact]
    public void TheArcLengthIsStableAsTheTailExpires()
    {
        var d = new Driver(Sword, 120);
        d.Player.StartSword(SwordSwing.Chop);
        d.StepUntil(() => d.Player.SwordProgress >= 0.4f);
        var snapshot = new Dictionary<double, float>();
        for (var i = 0; i < d.Trail.CommittedCount; i++)
            snapshot[d.Trail.TimeAt(i)] = d.Trail.AlongAt(i);
        var oldest = d.Trail.TimeAt(0);

        var matched = 0;
        for (var step = 0; step < 12; step++)
        {
            d.Step();
            for (var i = 0; i < d.Trail.Count; i++)
            {
                if (i > 0 && d.Trail.SegmentAt(i) == d.Trail.SegmentAt(i - 1))
                    Assert.True(d.Trail.AlongAt(i) >= d.Trail.AlongAt(i - 1));
                if (i >= d.Trail.CommittedCount || !snapshot.TryGetValue(d.Trail.TimeAt(i), out var along))
                    continue;
                Assert.Equal(along, d.Trail.AlongAt(i));
                matched++;
            }
        }

        Assert.True(d.Trail.TimeAt(0) > oldest, "no sample expired");
        Assert.True(matched > 0);
    }

    [Fact]
    public void SamplesRideTheOwnerFrame()
    {
        var a = new Driver(Blade, 120);
        var b = new Driver(Blade, 120) { Eye = new Vector3(8f, 18f, -2f), Yaw = 35f };
        a.Player.StartSlice(1);
        b.Player.StartSlice(1);
        for (var step = 0; step < 60; step++)
        {
            a.Step();
            b.Step();
            Assert.Equal(a.Trail.Count, b.Trail.Count);
            for (var i = 0; i < a.Trail.Count; i++)
            {
                Assert.True(Vector3.Distance(a.Trail.TipAt(i), b.Trail.TipAt(i)) < 1e-3f);
                Assert.True(Vector3.Distance(a.Trail.InnerAt(i), b.Trail.InnerAt(i)) < 1e-3f);
            }
        }
    }

    [Fact]
    public void ALaggingBodyKeepsTheHeadOnTheBlade()
    {
        var d = new Driver(Blade, 120);
        d.Player.BodyYawLags = true;
        d.Player.SetLook(d.Yaw, 0f);
        d.Player.StartSlice(1);
        var step = 0;
        while (d.Player.IsSlicing)
        {
            // The look swings away mid-swing; the body trails it
            if (step++ > 20)
                d.Yaw += 3f;
            d.Player.SetLook(d.Yaw, 0f);
            d.Step();
            if (d.Player.IsSlicing)
                Assert.True(Vector3.Distance(d.HeadWorld, d.DrawnTip) < 1e-3f,
                    $"step {step}: head {d.HeadWorld} off the tip {d.DrawnTip}");
        }
        Assert.NotEqual(d.Yaw, d.Player.DrawnBodyYaw(d.Yaw));
    }

    // The owner-frame head X across a swing's strike, from its start to its end.
    private static float StrikeTravelX(int direction)
    {
        var d = new Driver(Blade, 240);
        d.Player.StartSlice(direction);
        d.StepUntil(() => d.Player.SliceProgress >= SliceMotion.SliceWindupEnd);
        var start = d.Head.X;
        d.StepUntil(() => d.Player.SliceProgress >= SliceMotion.SliceStrikeEnd);
        return d.Head.X - start;
    }

    [Fact]
    public void TheReturnSliceSweepsTheOtherWay()
    {
        var slice = StrikeTravelX(1);
        var back = StrikeTravelX(-1);

        Assert.NotEqual(0f, slice);
        Assert.Equal(-MathF.Sign(slice), MathF.Sign(back));
    }

    [Fact]
    public void TheChopTrailIsVertical()
    {
        var d = new Driver(Sword, 240);
        d.Player.StartSword(SwordSwing.Chop);
        d.StepUntil(() => d.Player.SwordProgress >= PlayerModel.SwordImpactProgress(SwordSwing.Chop));
        var segment = d.Trail.SegmentAt(d.Trail.Count - 1);
        var tips = Enumerable.Range(0, d.Trail.Count)
            .Where(i => d.Trail.SegmentAt(i) == segment)
            .Select(d.Trail.TipAt)
            .ToList();

        var ySpan = tips.Max(t => t.Y) - tips.Min(t => t.Y);
        var xSpan = tips.Max(t => t.X) - tips.Min(t => t.X);
        Assert.True(ySpan > xSpan, $"chop trail spans {ySpan} up but {xSpan} across");
    }
}
