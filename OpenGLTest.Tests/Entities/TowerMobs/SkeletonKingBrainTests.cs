using VoxelEngine.Entities.TowerMobs;
using Xunit;

namespace VoxelEngine.Tests.Entities.TowerMobs;

/// <summary>
/// The Skeleton King's decision rules, GL-free: the first health observation sets the phase silently, each later
/// phase rise owes exactly one summon taken before anything else, the phase never falls until a reset, the slam and
/// the volley wait for their cooldowns and their conditions, summons only come from phase 2 by the roll, and the
/// enrage brings attacks back sooner.
/// </summary>
public sealed class SkeletonKingBrainTests
{
    private const float Dt = 1f / 60f;

    // A share safely inside each phase
    private const float Phase1 = 1f;
    private static float Phase2 => (MobConfig.SkeletonKingPhase2Share + MobConfig.SkeletonKingPhase3Share) * 0.5f;
    private static float Phase3 => MobConfig.SkeletonKingPhase3Share * 0.5f;

    private static float VolleyBand => (MobConfig.SkeletonKingVolleyMinRange + MobConfig.SkeletonKingVolleyMaxRange) * 0.5f;

    // A distance outside the volley band and out of slam reach
    private static float Nowhere => MobConfig.SkeletonKingVolleyMaxRange * 2f;

    // Ticks the brain until every cooldown has run out
    private static void Rest(SkeletonKingBrain brain)
    {
        for (var i = 0; i < (int)(60f / Dt); i++) brain.Tick(Dt);
    }

    [Fact]
    public void TheFirstObservationIsSilent()
    {
        var brain = new SkeletonKingBrain(() => 1f);
        brain.Observe(Phase3);
        Assert.Equal(3, brain.Phase);
        Rest(brain);

        // Nothing owed: a target in reach is slammed, not summoned at
        Assert.Equal(SkeletonKingAttack.Slam, brain.Choose(1f, inSlamReach: true, seesTarget: true, grounded: true));
    }

    [Fact]
    public void EnteringAPhaseOwesOneSummon()
    {
        var brain = new SkeletonKingBrain(() => 1f);
        brain.Observe(Phase1);
        Rest(brain);
        brain.Observe(Phase2);
        Assert.Equal(2, brain.Phase);

        // The owed summon comes before a ready slam, and only once
        Assert.Equal(SkeletonKingAttack.Summon, brain.Choose(1f, inSlamReach: true, seesTarget: true, grounded: true));
        Assert.Equal(SkeletonKingAttack.Slam, brain.Choose(1f, inSlamReach: true, seesTarget: true, grounded: true));
        Rest(brain);
        Assert.Equal(SkeletonKingAttack.None, brain.Choose(Nowhere, inSlamReach: false, seesTarget: true, grounded: true));

        // Entering the enrage owes one more
        brain.Observe(Phase3);
        Assert.Equal(SkeletonKingAttack.Summon, brain.Choose(Nowhere, inSlamReach: false, seesTarget: true, grounded: true));
        Assert.Equal(SkeletonKingAttack.None, brain.Choose(Nowhere, inSlamReach: false, seesTarget: true, grounded: true));
    }

    [Fact]
    public void ThePhaseNeverFallsButResetDoes()
    {
        var brain = new SkeletonKingBrain(() => 1f);
        brain.Observe(Phase1);
        brain.Observe(Phase3);
        brain.Observe(Phase1);
        Assert.Equal(3, brain.Phase);
        Assert.True(brain.IsEnraged);

        brain.Reset();
        Assert.Equal(1, brain.Phase);
        Assert.False(brain.IsEnraged);

        // After a reset the next observation is silent again
        brain.Observe(Phase2);
        Rest(brain);
        Assert.Equal(SkeletonKingAttack.Slam, brain.Choose(1f, inSlamReach: true, seesTarget: true, grounded: true));
    }

    [Fact]
    public void SlamWaitsForItsCooldown()
    {
        var brain = new SkeletonKingBrain(() => 1f);
        brain.Observe(Phase1);
        Assert.Equal(SkeletonKingAttack.Slam, brain.Choose(1f, inSlamReach: true, seesTarget: true, grounded: false));
        Assert.Equal(SkeletonKingAttack.None, brain.Choose(1f, inSlamReach: true, seesTarget: true, grounded: false));

        var frames = 0;
        while (brain.Choose(1f, inSlamReach: true, seesTarget: true, grounded: false) != SkeletonKingAttack.Slam)
        {
            brain.Tick(Dt);
            frames++;
            Assert.True(frames < 10_000, "the slam never came back");
        }
        Assert.True(frames > 1, "the slam came back at once");
    }

    [Fact]
    public void VolleyNeedsSightBandAndGround()
    {
        var brain = new SkeletonKingBrain(() => 1f);
        brain.Observe(Phase1);
        Rest(brain);

        Assert.Equal(SkeletonKingAttack.None, brain.Choose(VolleyBand, inSlamReach: false, seesTarget: false, grounded: true));
        Assert.Equal(SkeletonKingAttack.None, brain.Choose(VolleyBand, inSlamReach: false, seesTarget: true, grounded: false));
        Assert.Equal(SkeletonKingAttack.None,
            brain.Choose(MobConfig.SkeletonKingVolleyMinRange * 0.5f, inSlamReach: false, seesTarget: true, grounded: true));
        Assert.Equal(SkeletonKingAttack.None, brain.Choose(Nowhere, inSlamReach: false, seesTarget: true, grounded: true));

        Assert.Equal(SkeletonKingAttack.Volley, brain.Choose(VolleyBand, inSlamReach: false, seesTarget: true, grounded: true));
        // Then it waits for its cooldown
        Assert.Equal(SkeletonKingAttack.None, brain.Choose(VolleyBand, inSlamReach: false, seesTarget: true, grounded: true));

        // A fresh king waits before its first volley
        var fresh = new SkeletonKingBrain(() => 1f);
        fresh.Observe(Phase1);
        Assert.Equal(SkeletonKingAttack.None, fresh.Choose(VolleyBand, inSlamReach: false, seesTarget: true, grounded: true));
    }

    [Fact]
    public void SummonOnlyFromPhaseTwoByRoll()
    {
        var roll = 0f;
        var brain = new SkeletonKingBrain(() => roll);
        brain.Observe(Phase1);
        Rest(brain);
        Assert.Equal(SkeletonKingAttack.None, brain.Choose(Nowhere, inSlamReach: false, seesTarget: true, grounded: true));

        var phase2 = new SkeletonKingBrain(() => roll);
        phase2.Observe(Phase2); // silent: nothing owed
        Rest(phase2);
        roll = 1f;
        Assert.Equal(SkeletonKingAttack.None, phase2.Choose(Nowhere, inSlamReach: false, seesTarget: true, grounded: true));
        roll = 0f;
        Assert.Equal(SkeletonKingAttack.Summon, phase2.Choose(Nowhere, inSlamReach: false, seesTarget: true, grounded: true));
        // The summon then waits for its cooldown, whatever the roll
        Assert.Equal(SkeletonKingAttack.None, phase2.Choose(Nowhere, inSlamReach: false, seesTarget: true, grounded: true));
    }

    [Fact]
    public void EnrageShortensCooldowns()
    {
        static int FramesUntilTheSlamReturns(float share)
        {
            var brain = new SkeletonKingBrain(() => 1f);
            brain.Observe(share);
            Rest(brain);
            Assert.Equal(SkeletonKingAttack.Slam, brain.Choose(1f, inSlamReach: true, seesTarget: true, grounded: false));
            var frames = 0;
            while (brain.Choose(1f, inSlamReach: true, seesTarget: true, grounded: false) != SkeletonKingAttack.Slam)
            {
                brain.Tick(Dt);
                frames++;
            }
            return frames;
        }

        Assert.True(FramesUntilTheSlamReturns(Phase3) < FramesUntilTheSlamReturns(Phase1));

        var calm = new SkeletonKingBrain(() => 1f);
        calm.Observe(Phase1);
        var enraged = new SkeletonKingBrain(() => 1f);
        enraged.Observe(Phase3);
        Assert.True(enraged.SpeedFactor > calm.SpeedFactor);
        Assert.True(enraged.CooldownScale < calm.CooldownScale);
    }
}
