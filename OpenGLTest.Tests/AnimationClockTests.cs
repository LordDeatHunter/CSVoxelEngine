using System;
using System.Collections.Generic;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The item debugger's timeline: speed x0..x4 in tenths, fixed-step stepping, loop on/off, and scrubbing that lands on
/// exactly the frames playing would. A fake driver pins the clock's own rules; the real preview states are checked
/// for being drivable (a duration, a restart that reproduces the same frames, replay handed over to the clock).
/// </summary>
public sealed class AnimationClockTests
{
    private const float Dt = 1f / 60f;

    private sealed class FakeDriver(float duration, bool continuous) : IPreviewDriver
    {
        public float Duration { get; set; } = duration;
        public bool IsContinuous { get; } = continuous;
        public bool Replay { get; set; } = true;
        public int Restarts;
        public readonly List<float> Steps = [];
        public float Total;

        public void Restart() { Restarts++; Total = 0f; Steps.Clear(); }

        public void Update(float deltaTime)
        {
            Steps.Add(deltaTime);
            Total += deltaTime;
        }
    }

    private static (AnimationClock Clock, FakeDriver Driver) Make(float duration = 1f, bool continuous = false)
    {
        var driver = new FakeDriver(duration, continuous);
        var clock = new AnimationClock();
        clock.Attach(driver);
        return (clock, driver);
    }

    private static void Play(AnimationClock clock, float seconds, float frame = Dt)
    {
        for (var t = 0f; t < seconds - 1e-5f; t += frame) clock.Update(frame);
    }

    [Fact]
    public void SpeedIsClampedToZeroToFourInTenths()
    {
        var clock = new AnimationClock();
        clock.Speed = 0.34f;
        Assert.Equal(0.3f, clock.Speed, 3);
        clock.Speed = 7f;
        Assert.Equal(AnimationClock.MaxSpeed, clock.Speed);
        clock.Speed = -2f;
        Assert.Equal(0f, clock.Speed);
        clock.Speed = 2.5f;
        Assert.Equal(2.5f, clock.Speed, 3);
    }

    [Fact]
    public void TheDriverIsOnlyEverSteppedInFixedSlicesAndSpeedScalesHowManyPerSecond()
    {
        var (clock, driver) = Make(duration: 100f, continuous: true);

        // A hitchy, uneven frame rate still gives the driver whole fixed slices.
        foreach (var frame in new[] { 0.016f, 0.05f, 0.007f, 0.033f, 0.02f })
            clock.Update(frame);
        Assert.NotEmpty(driver.Steps);
        Assert.All(driver.Steps, s => Assert.Equal(AnimationClock.Step, s, 5));

        var (fast, fastDriver) = Make(duration: 100f, continuous: true);
        fast.Speed = 2f;
        Play(fast, 1f);
        Assert.InRange(fastDriver.Total, 1.95f, 2.05f);

        var (slow, slowDriver) = Make(duration: 100f, continuous: true);
        slow.Speed = 0.5f;
        Play(slow, 1f);
        Assert.InRange(slowDriver.Total, 0.45f, 0.55f);
    }

    [Fact]
    public void SpeedZeroAndScrubbingStopTheClock()
    {
        var (clock, driver) = Make(duration: 100f, continuous: true);
        clock.Speed = 0f;
        Play(clock, 1f);
        Assert.Equal(0f, driver.Total);

        clock.Speed = 1f;
        clock.Scrubbing = true;
        Play(clock, 1f);
        Assert.Equal(0f, driver.Total);

        clock.Scrubbing = false;
        Play(clock, 0.5f);
        Assert.True(driver.Total > 0.4f);
    }

    [Fact]
    public void LoopingARestartsAOneShotAndWrapsAContinuousPoseWithoutRestarting()
    {
        var (oneShot, oneDriver) = Make(duration: 0.5f, continuous: false);
        var before = oneDriver.Restarts;
        Play(oneShot, 1.6f);
        Assert.True(oneDriver.Restarts >= before + 2, "A looping one-shot never started over.");

        var (pose, poseDriver) = Make(duration: 0.5f, continuous: true);
        var restarts = poseDriver.Restarts;
        Play(pose, 1.6f);
        Assert.Equal(restarts, poseDriver.Restarts);
        // Time wrapped: it never runs past the cycle.
        Assert.True(pose.Time <= pose.Duration + 1e-3f);
    }

    [Fact]
    public void WithoutLoopingThePlayHoldsItsLastFrame()
    {
        var (clock, driver) = Make(duration: 0.5f);
        clock.Loop = false;
        Play(clock, 2f);

        Assert.True(clock.Finished);
        Assert.Equal(clock.Duration, clock.Time, 2);
        var held = driver.Total;
        Play(clock, 1f);
        Assert.Equal(held, driver.Total);
        Assert.InRange(driver.Total, 0.45f, 0.55f);
    }

    [Fact]
    public void SeekingRestartsAndStepsUpToTheTimeSoItReproducesThePlayedFrames()
    {
        var (clock, driver) = Make(duration: 2f);
        Play(clock, 0.7f);
        var played = driver.Total;

        clock.Seek(1.25f);
        Assert.InRange(clock.Time, 1.25f - AnimationClock.Step, 1.25f);
        Assert.Equal(clock.Time, driver.Total, 3);

        clock.Seek(0.7f);
        Assert.Equal(played, driver.Total, 2);

        clock.Seek(99f);
        Assert.InRange(clock.Time, clock.Duration - AnimationClock.Step, clock.Duration);
        clock.Seek(-5f);
        Assert.Equal(0f, clock.Time);
    }

    [Fact]
    public void AttachingTakesReplayAwayFromTheDriver()
    {
        var (_, driver) = Make();
        Assert.False(driver.Replay);
    }

    // ---- the real previews ----

    private static float[] PlayerSnapshot(PlayerModel model)
    {
        var pose = model.GetPose();
        return
        [
            pose.LeftHip, pose.RightHip, pose.LeftKnee, pose.RightKnee, pose.LeftShoulder, pose.RightShoulder,
            pose.LeftElbow, pose.RightElbow, pose.TorsoPitch, pose.Bob, model.DeathFall, model.SwimStrokeProgress,
            model.Flinch, model.LandingDip, model.MineT, model.SliceProgress,
        ];
    }

    [Fact]
    public void EveryPlayerModeHasADurationAndSeekingReproducesPlaying()
    {
        foreach (var mode in PlayerPreviewState.AllModes)
        {
            var state = new PlayerPreviewState(new PlayerModel());
            Assert.True(state.SetMode(mode));
            Assert.True(state.Duration > 0.2f, mode);

            var played = new AnimationClock();
            played.Attach(state);
            var target = state.Duration * 0.6f;
            Play(played, target);
            var expected = PlayerSnapshot(state.Model);

            var sought = new PlayerPreviewState(new PlayerModel());
            Assert.True(sought.SetMode(mode));
            var seekClock = new AnimationClock();
            seekClock.Attach(sought);
            seekClock.Seek(played.Time);
            var actual = PlayerSnapshot(sought.Model);

            for (var i = 0; i < expected.Length; i++)
                Assert.True(MathF.Abs(expected[i] - actual[i]) < 1e-3f,
                    $"{mode}: value {i} differs between playing and seeking ({expected[i]} vs {actual[i]}).");
        }
    }

    [Fact]
    public void AOneShotPlaysOnceWhenNotLoopingAndAgainWhenLooping()
    {
        foreach (var mode in new[] { PlayerPreviewState.Slice, PlayerPreviewState.Punch, PlayerPreviewState.Throw,
                     PlayerPreviewState.Hurt, PlayerPreviewState.Jump })
        {
            int Activations(bool loop)
            {
                var state = new PlayerPreviewState(new PlayerModel());
                Assert.True(state.SetMode(mode));
                var clock = new AnimationClock { Loop = loop };
                clock.Attach(state);
                var count = 0;
                var active = false;
                for (var i = 0; i < (int)(state.Duration * 3.5f / Dt); i++)
                {
                    clock.Update(Dt);
                    var m = state.Model;
                    var now = mode switch
                    {
                        PlayerPreviewState.Slice => m.IsSlicing,
                        PlayerPreviewState.Punch => m.MineT < 1f,
                        PlayerPreviewState.Throw => m.IsThrowing || m.ThrowCharge.HasValue,
                        PlayerPreviewState.Hurt => MathF.Abs(m.Flinch) > 0.05f,
                        _ => m.AirBlend > 0.7f,
                    };
                    if (now && !active) count++;
                    active = now;
                }
                return count;
            }

            Assert.Equal(1, Activations(loop: false));
            Assert.True(Activations(loop: true) >= 2, $"'{mode}' did not play again when looping.");
        }
    }

    [Fact]
    public void EveryEntityModeHasADurationAndARestartReproducesTheFrames()
    {
        TestContent.EnsureLoaded();
        foreach (var name in new[] { "goblin", "slime" })
        {
            var state = new EntityPreviewState();
            Assert.True(state.SelectByName(name));
            foreach (var mode in state.Modes)
            {
                Assert.True(state.SetMode(mode));
                Assert.True(state.Duration > 0.1f, $"{name} {mode}");

                var clock = new AnimationClock();
                clock.Attach(state);
                Play(clock, state.Duration * 0.7f);
                var first = state.BodyEffect();
                var firstHit = state.HitFlash;

                clock.Seek(clock.Time);
                var second = state.BodyEffect();
                Assert.True(System.Numerics.Matrix4x4.Equals(first, second) || MaxDifference(first, second) < 1e-3f,
                    $"{name} {mode}: seeking did not reproduce the played frame.");
                Assert.Equal(firstHit, state.HitFlash, 3);
            }
        }
    }

    private static float MaxDifference(System.Numerics.Matrix4x4 a, System.Numerics.Matrix4x4 b)
    {
        var m = 0f;
        m = MathF.Max(m, MathF.Abs(a.M11 - b.M11)); m = MathF.Max(m, MathF.Abs(a.M12 - b.M12)); m = MathF.Max(m, MathF.Abs(a.M13 - b.M13));
        m = MathF.Max(m, MathF.Abs(a.M21 - b.M21)); m = MathF.Max(m, MathF.Abs(a.M22 - b.M22)); m = MathF.Max(m, MathF.Abs(a.M23 - b.M23));
        m = MathF.Max(m, MathF.Abs(a.M31 - b.M31)); m = MathF.Max(m, MathF.Abs(a.M32 - b.M32)); m = MathF.Max(m, MathF.Abs(a.M33 - b.M33));
        m = MathF.Max(m, MathF.Abs(a.M41 - b.M41)); m = MathF.Max(m, MathF.Abs(a.M42 - b.M42)); m = MathF.Max(m, MathF.Abs(a.M43 - b.M43));
        return m;
    }

    [Fact]
    public void ADeathHoldsWhenNotLoopingInsteadOfStartingOver()
    {
        TestContent.EnsureLoaded();
        var state = new EntityPreviewState();
        Assert.True(state.SelectByName("goblin"));
        Assert.True(state.SetMode(EntityPreviewState.DeathMode));
        var clock = new AnimationClock { Loop = false };
        clock.Attach(state);

        Play(clock, state.Duration * 3f);

        Assert.True(clock.Finished);
        Assert.True(state.PreviewMob!.IsDying);
    }
}
