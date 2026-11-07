using System;
using System.Numerics;
using VoxelEngine.Entities.Sandworm;
using Xunit;

namespace VoxelEngine.Tests.Entities.Sandworm;

public class SandwormBrainTests
{
    private const float Dt = 1f / 60f;

    // Drives a brain the way the manager will: senses in, velocity out, the head moved by it.
    private sealed class Sim
    {
        public Vector3 Head = new(0, 64 - SandwormConfig.BurrowDepthBlocks, 0);
        public Vector3? Target;
        public float Ground = 64f;
        public bool OnSand = true;
        public readonly SandwormBrain Brain;

        public Sim(float roll = 0.5f) => Brain = new SandwormBrain(0f, () => roll);

        public void Step()
        {
            Brain.Update(Dt, new SandwormSenses(Head, Target, Ground, OnSand));
            Head += Brain.Velocity * Dt;
        }

        public bool RunUntil(Func<bool> done, float maxSeconds)
        {
            for (var t = 0f; t < maxSeconds; t += Dt)
            {
                if (done()) return true;
                Step();
            }
            return done();
        }

        public bool RunUntilPhase(SandwormPhase phase, float maxSeconds = 30f) =>
            RunUntil(() => Brain.Phase == phase, maxSeconds);
    }

    private static Vector3 TargetAt(float x, float z) => new(x, 65f, z);

    [Fact]
    public void ThumpHoldsStillThenStartsDigging()
    {
        var sim = new Sim { Target = TargetAt(40, 0) };
        var start = sim.Head;
        sim.RunUntil(() => sim.Brain.PhaseSeconds > SandwormConfig.ThumpSeconds * 0.5f, 10f);
        Assert.Equal(SandwormPhase.Thump, sim.Brain.Phase);
        Assert.Equal(start, sim.Head);

        Assert.True(sim.RunUntilPhase(SandwormPhase.Dig, 10f));
    }

    [Fact]
    public void DigClosesInOnTheTargetFromUnderTheSand()
    {
        var sim = new Sim { Target = TargetAt(40, 0) };
        sim.RunUntilPhase(SandwormPhase.Dig);
        var firstDistance = Vector3.Distance(sim.Head * new Vector3(1, 0, 1), sim.Target!.Value * new Vector3(1, 0, 1));

        var surfacedBeforeBreach = false;
        var telegraphAtStart = -1f;
        var peakShake = 0f;
        sim.RunUntil(() =>
        {
            if (sim.Brain.Phase != SandwormPhase.Dig) return true;
            if (telegraphAtStart < 0) telegraphAtStart = sim.Brain.Telegraph;
            peakShake = MathF.Max(peakShake, sim.Brain.Shake);
            surfacedBeforeBreach |= sim.Head.Y >= sim.Ground;
            return false;
        }, 30f);

        Assert.Equal(SandwormPhase.Breach, sim.Brain.Phase);
        Assert.False(surfacedBeforeBreach);
        Assert.True(peakShake > telegraphAtStart);
        Assert.True(firstDistance > SandwormConfig.BreachTriggerBlocks);
    }

    [Fact]
    public void TheTelegraphAndTheShakeGrowAsTheWormGetsCloser()
    {
        var far = new Sim { Target = TargetAt(300, 0) };
        far.RunUntilPhase(SandwormPhase.Dig);
        far.Step();

        var near = new Sim { Target = TargetAt(15, 0) };
        near.RunUntilPhase(SandwormPhase.Dig);
        near.Step();

        Assert.True(near.Brain.Shake > far.Brain.Shake);
        Assert.True(near.Brain.Telegraph > far.Brain.Telegraph);
        Assert.Equal(0f, far.Brain.Shake);
    }

    [Fact]
    public void ABreachGoesToTheLockedPointEvenIfTheTargetDodges()
    {
        var sim = new Sim { Target = TargetAt(40, 0) };
        sim.RunUntilPhase(SandwormPhase.Dig);
        sim.RunUntil(() => sim.Brain.HasLockedPoint, 30f);
        Assert.True(sim.Brain.HasLockedPoint);
        var locked = sim.Brain.LockedPoint;

        // The target runs off once the point is locked.
        sim.Target = TargetAt(locked.X, locked.Z + 30f);
        sim.RunUntilPhase(SandwormPhase.Breach);

        Assert.Equal(SandwormPhase.Breach, sim.Brain.Phase);
        var offset = new Vector2(sim.Head.X - locked.X, sim.Head.Z - locked.Z).Length();
        Assert.True(offset <= SandwormConfig.BreachTriggerBlocks + 0.5f);
    }

    [Fact]
    public void TheHeadChompsWhileItBreachesAndFliesAndSlidesOnTheDash()
    {
        var sim = new Sim(roll: 0f) { Target = TargetAt(40, 0) };
        sim.RunUntilPhase(SandwormPhase.Dig);
        Assert.Equal(SandwormContact.None, sim.Brain.Contact);
        sim.RunUntilPhase(SandwormPhase.Breach);
        Assert.Equal(SandwormContact.Chomp, sim.Brain.Contact);
        sim.RunUntilPhase(SandwormPhase.Air);
        Assert.Equal(SandwormContact.Chomp, sim.Brain.Contact);
        sim.RunUntilPhase(SandwormPhase.Dash);
        Assert.Equal(SandwormContact.Slide, sim.Brain.Contact);
    }

    [Fact]
    public void AtTheApexItEitherDivesBackIntoTheSandOrSlidesOnIt()
    {
        var slide = new Sim(roll: 0f) { Target = TargetAt(40, 0) };
        slide.RunUntilPhase(SandwormPhase.Air);
        slide.RunUntilPhase(SandwormPhase.Dash);
        Assert.True(slide.Brain.PlansSlide);
        Assert.InRange(slide.Head.Y, slide.Ground - 0.5f, slide.Ground + 2f);

        var dive = new Sim(roll: 0.99f) { Target = TargetAt(40, 0) };
        dive.RunUntilPhase(SandwormPhase.Air);
        var dug = dive.RunUntil(() => dive.Brain.Phase == SandwormPhase.Dig, 30f);
        Assert.True(dug);
        Assert.False(dive.Brain.PlansSlide);
        Assert.True(dive.Head.Y < dive.Ground);
    }

    [Fact]
    public void RepeatingThePreviousPickIsLessLikelyThanSwitching()
    {
        Assert.True(SandwormBrain.SlideChanceAfter(true) < SandwormConfig.SlideChance);
        Assert.True(SandwormBrain.SlideChanceAfter(false) > SandwormConfig.SlideChance);
        Assert.True(SandwormBrain.SlideChanceAfter(false) > SandwormBrain.SlideChanceAfter(true));
    }

    [Fact]
    public void TheDashTurnsOnlyAsFastAsItsTurnRateAllows()
    {
        var sim = new Sim(roll: 0f) { Target = TargetAt(40, 0) };
        sim.RunUntilPhase(SandwormPhase.Dash);

        // The target is suddenly behind it.
        var heading = sim.Brain.Heading;
        sim.Target = sim.Head - new Vector3(MathF.Sin(heading), 0, MathF.Cos(heading)) * 20f;
        var start = heading;
        for (var step = 0; step < 60 && sim.Brain.Phase == SandwormPhase.Dash; step++)
        {
            sim.Target = sim.Head - new Vector3(MathF.Sin(start), 0, MathF.Cos(start)) * 20f;
            sim.Step();
            var delta = MathF.Abs(sim.Brain.Heading - heading);
            delta = MathF.Min(delta, 2 * MathF.PI - delta);
            Assert.True(delta <= SandwormConfig.DashTurnDegreesPerSecond * MathF.PI / 180f * Dt + 1e-4f);
            heading = sim.Brain.Heading;
        }

        // One second in, it has turned only a limited way round.
        var turned = MathF.Abs(sim.Brain.Heading - start);
        turned = MathF.Min(turned, 2 * MathF.PI - turned);
        Assert.True(turned < MathF.PI * 0.5f);
    }

    [Fact]
    public void TheDashEndsWhenItRunsOutOfSand()
    {
        var sim = new Sim(roll: 0f) { Target = TargetAt(40, 0) };
        sim.RunUntilPhase(SandwormPhase.Dash);
        sim.OnSand = false;
        sim.Step();
        Assert.Equal(SandwormPhase.Dig, sim.Brain.Phase);
    }

    [Fact]
    public void TheDashEndsOnItsTimeCapEvenOverOpenSand()
    {
        var sim = new Sim(roll: 0f) { Target = TargetAt(4000, 0) };
        sim.RunUntilPhase(SandwormPhase.Dash);
        Assert.True(sim.RunUntil(() => sim.Brain.Phase != SandwormPhase.Dash, SandwormConfig.DashMaxSeconds + 1f));
        Assert.Equal(SandwormPhase.Dig, sim.Brain.Phase);
    }

    [Fact]
    public void WithNoTargetItEventuallyRetreatsAndFinishes()
    {
        var sim = new Sim();
        sim.RunUntilPhase(SandwormPhase.Dig);
        Assert.True(sim.RunUntilPhase(SandwormPhase.Retreat, SandwormConfig.RetreatAfterSeconds + 2f));
        Assert.False(sim.Brain.IsFinished);
        Assert.True(sim.RunUntil(() => sim.Brain.IsFinished, 10f));
    }

    [Fact]
    public void ATargetThatReturnsInTimeCancelsTheRetreatClock()
    {
        var sim = new Sim();
        sim.RunUntilPhase(SandwormPhase.Dig);
        sim.RunUntil(() => false, SandwormConfig.RetreatAfterSeconds * 0.8f);
        sim.Target = TargetAt(60, 0);
        sim.RunUntil(() => false, 2f);
        sim.Target = null;
        sim.RunUntil(() => false, SandwormConfig.RetreatAfterSeconds * 0.8f);
        Assert.NotEqual(SandwormPhase.Retreat, sim.Brain.Phase);
    }
}
