using System;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The fall-damage rule (<see cref="FallTracker"/>), driven by explicit feet sequences: a
/// landing bills the drop from the peak through <see cref="PlayerHealth.FallDamage"/>, water
/// and creative cushion it, flight and a teleport-sized step forget it, and a reset or a
/// plain jump lands free.
/// </summary>
public sealed class FallTrackerTests
{
    /// <summary>One observation's motion, well inside the teleport guard, like a frame's fall.</summary>
    private const float Step = 0.25f;

    private const float Max = FallTracker.DefaultMaxStepBlocks;
    private const float Ledge = 50f;

    [Fact]
    public void ALandingPastTheGraceHurtsByTheFallDamageCurve()
    {
        Assert.Equal(0, Drop(PlayerHealth.FallMinBlocks - 1f));

        var longDrop = PlayerHealth.FallMinBlocks + 3f;
        var damage = Drop(longDrop);
        Assert.True(damage > 0);
        Assert.Equal(PlayerHealth.FallDamage(longDrop), damage);
    }

    [Fact]
    public void WaterCreativeAndFlightCushionOrForgetTheFall()
    {
        var drop = PlayerHealth.FallMinBlocks + 3f;
        Assert.True(Drop(drop) > 0);
        Assert.Equal(0, Drop(drop, inWater: true));
        Assert.Equal(0, Drop(drop, creative: true));

        // A flying observation mid-fall forgets the peak: only the stretch below it counts,
        // and that stretch is shorter than the grace.
        var land = Ledge - drop;
        var hover = land + PlayerHealth.FallMinBlocks - 1f;
        var tracker = new FallTracker();
        Assert.Equal(0, Stand(tracker, Ledge));
        Assert.Equal(0, Airborne(tracker, Ledge, hover));
        Assert.Equal(0, tracker.Observe(hover, grounded: false, flying: true, inWater: false, creative: false, Max));
        Assert.Equal(0, Airborne(tracker, hover, land));
        Assert.Equal(0, Stand(tracker, land));
    }

    [Fact]
    public void AStepLargerThanMaxStepIsATeleportNotAFall()
    {
        // One step bigger than the guard and longer than the grace, then a short fall.
        var jump = MathF.Max(Max, PlayerHealth.FallMinBlocks) + 1f;
        var arrival = Ledge - jump;
        var land = arrival - 1f;

        int Run(float maxStep)
        {
            var tracker = new FallTracker();
            var damage = Stand(tracker, Ledge, maxStep: maxStep);
            damage += tracker.Observe(arrival, grounded: false, flying: false, inWater: false, creative: false, maxStep);
            damage += Airborne(tracker, arrival, land, maxStep: maxStep);
            return damage + Stand(tracker, land, maxStep: maxStep);
        }

        Assert.Equal(0, Run(Max));

        // The very same path read with a guard wider than the step is one long fall.
        var asFall = Run(jump + 1f);
        Assert.True(asFall > 0);
        Assert.Equal(PlayerHealth.FallDamage(Ledge - land), asFall);
    }

    [Fact]
    public void ResetAndAPlainJumpLandFree()
    {
        var land = Ledge - (PlayerHealth.FallMinBlocks + 3f);
        var resetAt = land + 1f;

        // A respawn mid-fall forgets the fall built up before it.
        var reset = new FallTracker();
        var kept = new FallTracker();
        foreach (var tracker in new[] { reset, kept })
        {
            Stand(tracker, Ledge);
            Airborne(tracker, Ledge, resetAt);
        }
        reset.Reset(resetAt, grounded: false);
        Assert.Equal(0, Stand(reset, land));
        Assert.True(Stand(kept, land) > 0);

        // A plain jump on flat ground lands free.
        const float apex = 1.25f;
        var jumper = new FallTracker();
        Assert.Equal(0, Stand(jumper, Ledge));
        Assert.Equal(0, Airborne(jumper, Ledge, Ledge + apex));
        Assert.Equal(0, Airborne(jumper, Ledge + apex, Ledge));
        Assert.Equal(0, Stand(jumper, Ledge));

        // But a jump's apex counts toward the drop: leaping off a ledge stepping off of which
        // is free bills the drop from the top of the jump.
        var lowLedge = PlayerHealth.FallMinBlocks - 0.5f;
        Assert.Equal(0, Drop(lowLedge));
        var leaper = new FallTracker();
        Stand(leaper, Ledge);
        Airborne(leaper, Ledge, Ledge + apex);
        Airborne(leaper, Ledge + apex, Ledge - lowLedge);
        var damage = Stand(leaper, Ledge - lowLedge);
        Assert.True(damage > 0);
        Assert.Equal(PlayerHealth.FallDamage(lowLedge + apex), damage);
    }

    [Fact]
    public void LandedDropReportsTheLandingsDropOnlyOnTheLandingObservation()
    {
        const float drop = 7f;
        foreach (var (inWater, creative) in new[] { (false, false), (true, false), (false, true) })
        {
            // Water and creative cushion the damage, never the landing itself
            var tracker = new FallTracker();
            Stand(tracker, Ledge);
            Airborne(tracker, Ledge, Ledge - drop);
            Assert.Equal(0f, tracker.LandedDrop);
            Stand(tracker, Ledge - drop, inWater, creative);
            Assert.Equal(drop, tracker.LandedDrop, 3);
            Stand(tracker, Ledge - drop);
            Assert.Equal(0f, tracker.LandedDrop);
        }

        // A teleport-sized step down onto the ground lands nothing
        var teleported = new FallTracker();
        Stand(teleported, Ledge);
        Stand(teleported, Ledge - (Max + 1f));
        Assert.Equal(0f, teleported.LandedDrop);
    }

    /// <summary>Stands on <see cref="Ledge"/>, falls <paramref name="drop"/> blocks and lands, returning the landing's damage.</summary>
    private static int Drop(float drop, bool inWater = false, bool creative = false)
    {
        var tracker = new FallTracker();
        Assert.Equal(0, Stand(tracker, Ledge));
        Assert.Equal(0, Airborne(tracker, Ledge, Ledge - drop));
        return Stand(tracker, Ledge - drop, inWater, creative);
    }

    private static int Stand(FallTracker tracker, float feet, bool inWater = false, bool creative = false,
        float maxStep = Max) =>
        tracker.Observe(feet, grounded: true, flying: false, inWater, creative, maxStep);

    /// <summary>
    /// Airborne observations from one <see cref="Step"/> past <paramref name="from"/> up to and
    /// including <paramref name="to"/>, returning their summed damage.
    /// </summary>
    private static int Airborne(FallTracker tracker, float from, float to, float maxStep = Max)
    {
        var steps = Math.Max(1, (int)MathF.Round(MathF.Abs(to - from) / Step));
        var direction = MathF.Sign(to - from);
        var damage = 0;
        for (var i = 1; i <= steps; i++)
        {
            var feet = i == steps ? to : from + direction * i * Step;
            damage += tracker.Observe(feet, grounded: false, flying: false, inWater: false, creative: false, maxStep);
        }
        return damage;
    }
}
