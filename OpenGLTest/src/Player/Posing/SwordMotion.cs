using System.Numerics;
using VoxelEngine.Interaction;

namespace VoxelEngine.Player.Posing;

/// <summary>
/// The sword's three swings as pure functions of their clock (<see cref="SwordSwing"/>): each is a
/// wind-up, a strike and a recovery like the slice, but a long blade is swung from the shoulder, not
/// carried across at chest height. The cut winds the arm high over the right shoulder and brings it
/// down across to low on the left, the body turning hard with it; the back cut starts low on the
/// left and rises to high on the right; the chop raises the arm straight overhead with the blade
/// cocked behind the head, holds a beat, and comes straight down with the torso folding over the
/// blow and the weight dropped, then recovers slowly. Everything is a <see cref="BladeSwingFrame"/>,
/// which is all the body code reads.
/// </summary>
internal static class SwordMotion
{
    // One swing's timing and shape. The phases are shares of the swing: the wind-up ends at WindupEnd
    // (the arm reaching its wound pose at WindupReach of it and holding the rest, the chop's anticipation),
    // the strike runs to StrikeEnd and is centered on Impact (where the damage lands), the far pose is
    // held until HoldEnd (the chop rests at the bottom of its blow), and the rest is the recovery.
    private readonly record struct Shape(
        float Duration, float WindupEnd, float WindupReach, float Impact, float StrikeEnd, float HoldEnd,
        int Dir,
        float YawWindup, float YawFar,
        float ShoulderWindup, float ShoulderFar,
        float ElbowWindup, float ElbowFar,
        float BodyWindup, float BodyFar, float BodyYaw, float TorsoYaw, float Roll,
        float LeanWindup, float LeanFar, float DipDepth, bool DipHeld,
        Vector3 PullWindup, Vector3 PullFar,
        float BladeRoll, float BladeCant, float BladeYaw,
        float DisplayYaw, float DisplayRoll);

    // The far poses are placed for the first-person view as much as the third: the eye sits a little
    // above and behind the shoulder, so a hand that ends more than about 50 degrees below the eye line, or
    // more than about 50 degrees off the look, leaves the screen. Two things pull a hand out of it: a
    // forward fold of the torso tips the whole arm down with it (a joint angle is relative to the torso,
    // so a folded chop needs its arm held higher to end in view), and a turn of the body toward the sword
    // side swings that shoulder back behind the eye. A wind-up may leave the screen (the blade rising out
    // of the frame is the anticipation) and a follow-through may exit it; the impact must be in it.

    // The cut: high right to low left. The wind-up lifts the arm well above the shoulder, out to the
    // right, the body coiled toward it; the strike sweeps across and down to below the chest, the blade
    // ending forward and down on the left; a long recovery.
    private static readonly Shape Cut = new(
        Duration: 0.85f, WindupEnd: 0.32f, WindupReach: 1f, Impact: 0.42f, StrikeEnd: 0.52f, HoldEnd: 0.52f,
        Dir: 1,
        YawWindup: 0.8f, YawFar: 0.45f,
        ShoulderWindup: 2.2f, ShoulderFar: 1.45f,
        ElbowWindup: 0.95f, ElbowFar: 0.25f,
        BodyWindup: 1f, BodyFar: 0.85f, BodyYaw: 0.3f, TorsoYaw: 0.45f, Roll: 0.1f,
        LeanWindup: 0.08f, LeanFar: -0.22f, DipDepth: 1.2f, DipHeld: false,
        PullWindup: new Vector3(0.04f, 0.03f, 0.02f), PullFar: new Vector3(-0.04f, -0.01f, -0.03f),
        BladeRoll: 0.8f, BladeCant: 0.12f, BladeYaw: 0.785f,
        DisplayYaw: -55f, DisplayRoll: 25f);

    // The back cut: the mirror in time and side, low left rising to high right in front, a little quicker.
    private static readonly Shape BackCut = new(
        Duration: 0.75f, WindupEnd: 0.28f, WindupReach: 1f, Impact: 0.38f, StrikeEnd: 0.48f, HoldEnd: 0.48f,
        Dir: -1,
        YawWindup: 0.65f, YawFar: 0.55f,
        ShoulderWindup: 0.9f, ShoulderFar: 1.9f,
        ElbowWindup: 0.3f, ElbowFar: 0.45f,
        BodyWindup: 0.85f, BodyFar: 1f, BodyYaw: 0.3f, TorsoYaw: 0.45f, Roll: 0.1f,
        LeanWindup: -0.15f, LeanFar: 0.1f, DipDepth: 0.9f, DipHeld: false,
        PullWindup: new Vector3(-0.04f, -0.01f, -0.03f), PullFar: new Vector3(0.04f, 0.03f, 0.02f),
        BladeRoll: 0.8f, BladeCant: 0.12f, BladeYaw: 0.785f,
        DisplayYaw: -55f, DisplayRoll: 25f);

    // The chop: straight overhead and straight down. The arm reaches the top three quarters through the
    // wind-up and holds there (the anticipation), the blade cocked up behind the head by the bent elbow;
    // the strike whips the arm straight and down the front while the torso folds forward and the weight
    // drops, the blade ending pointed down and forward in front of the folded body; the blow is held at
    // the bottom, then the body unwinds slowly.
    private static readonly Shape Chop = new(
        Duration: 1.1f, WindupEnd: 0.42f, WindupReach: 0.75f, Impact: 0.5f, StrikeEnd: 0.56f, HoldEnd: 0.68f,
        Dir: 1,
        YawWindup: 0.12f, YawFar: 0.08f,
        ShoulderWindup: 2.95f, ShoulderFar: 1.6f,
        ElbowWindup: 0.9f, ElbowFar: 0.15f,
        BodyWindup: 0.15f, BodyFar: 0.2f, BodyYaw: 0.25f, TorsoYaw: 0.3f, Roll: 0f,
        LeanWindup: 0.2f, LeanFar: -0.25f, DipDepth: 1.5f, DipHeld: true,
        PullWindup: new Vector3(0f, 0.035f, 0.025f), PullFar: new Vector3(0f, -0.015f, -0.05f),
        BladeRoll: 0f, BladeCant: 0f, BladeYaw: 0f,
        DisplayYaw: 0f, DisplayRoll: 0f);

    private static Shape Of(SwordSwing swing) => swing switch
    {
        SwordSwing.BackCut => BackCut,
        SwordSwing.Chop => Chop,
        _ => Cut,
    };

    /// <summary>Seconds one swing of <paramref name="swing"/> lasts.</summary>
    internal static float Duration(SwordSwing swing) => Of(swing).Duration;

    /// <summary>Progress through <paramref name="swing"/> at which its damage lands (the middle of its strike).</summary>
    internal static float ImpactProgress(SwordSwing swing) => Of(swing).Impact;

    /// <summary>The frame of <paramref name="swing"/> at <paramref name="t"/> (0..1): every term the body reads, zero
    /// outside the swing.</summary>
    internal static BladeSwingFrame FrameAt(float t, SwordSwing swing)
    {
        if (t <= 0f || t >= 1f) return BladeSwingFrame.None;
        var s = Of(swing);
        var stroke = Stroke(t, s);
        var main = MainWeight(t, s);
        var body = Keyed(t, s, -s.BodyWindup * s.Dir, s.BodyFar * s.Dir);
        return new BladeSwingFrame
        {
            Stroke = stroke,
            ArmYaw = Keyed(t, s, -s.YawWindup * s.Dir, s.YawFar * s.Dir),
            Shoulder = Target(t, s, s.ShoulderWindup, s.ShoulderFar),
            Elbow = Target(t, s, s.ElbowWindup, s.ElbowFar),
            RootYaw = body * s.BodyYaw,
            TorsoTwist = body * s.TorsoYaw,
            TorsoRoll = s.Dir * s.Roll * stroke,
            Dip = s.DipHeld ? HeldDip(t, s) : s.DipDepth * MathUtils.Pulse(t, s.Impact),
            Lean = Keyed(t, s, s.LeanWindup, s.LeanFar),
            ShoulderPull = Vector3.Lerp(s.PullWindup, s.PullFar, StrikeShare(t, s)) * stroke,
            BladeRoll = s.BladeRoll * s.Dir,
            BladeCant = s.BladeCant,
            BladeYaw = s.BladeYaw * s.Dir * main,
            DisplayOffset = new Vector3(0f, s.DisplayYaw * s.Dir, s.DisplayRoll) * main,
        };
    }

    // 0 -> 1 across the wind-up (reached at its WindupReach share, then held), 1 through the strike and
    // the hold, back to 0 over the recovery. 0 at both ends, so the swing never jumps.
    private static float Stroke(float t, in Shape s)
    {
        if (t < s.WindupEnd) return MathUtils.SmoothStep(MathUtils.Clamp01(t / (s.WindupEnd * s.WindupReach)));
        if (t < s.HoldEnd) return 1f;
        return 1f - MathUtils.SmoothStep((t - s.HoldEnd) / (1f - s.HoldEnd));
    }

    // Where the strike is, 0 at its start (and through the wind-up) to 1 at its end (and after), with the hard
    // acceleration through the middle every blade strike has.
    private static float StrikeShare(float t, in Shape s)
    {
        if (t < s.WindupEnd) return 0f;
        if (t >= s.StrikeEnd) return 1f;
        return MathUtils.EaseInOutCubic((t - s.WindupEnd) / (s.StrikeEnd - s.WindupEnd));
    }

    // An additive term: eased from 0 to its wind-up value as the arm winds (held once reached), carried to
    // its far value through the strike, held, and eased back to 0 over the recovery.
    private static float Keyed(float t, in Shape s, float windup, float far)
    {
        if (t < s.WindupEnd) return windup * Stroke(t, s);
        if (t < s.StrikeEnd) return MathUtils.Lerp(windup, far, StrikeShare(t, s));
        return far * Stroke(t, s);
    }

    // A dip that stays down: nothing through the wind-up, the full depth reached by the impact (the body has
    // dropped when the blow lands, not after it), held through the hold, and eased out with the recovery.
    private static float HeldDip(float t, in Shape s)
    {
        if (t < s.WindupEnd) return 0f;
        if (t < s.StrikeEnd)
            return s.DipDepth * MathUtils.Clamp01(StrikeShare(t, s) / StrikeShare(s.Impact, s));
        return s.DipDepth * Stroke(t, s);
    }

    // A joint target the arm is eased to by the stroke: the wound value until the strike, carried to the far
    // value through it, and the far value through the hold and the recovery (the stroke fades it back out).
    private static float Target(float t, in Shape s, float windup, float far) =>
        MathUtils.Lerp(windup, far, StrikeShare(t, s));

    // The main part of the swing, as a weight: in over the last of the wind-up, full through the strike and the
    // hold, out over the start of the recovery; what rides it (the blade's turn, its display offset) comes into the
    // blow and goes back out of it.
    private static float MainWeight(float t, in Shape s)
    {
        if (t < s.WindupEnd) return MathUtils.SmoothStep(s.WindupEnd * 0.6f, s.WindupEnd, t);
        if (t < s.HoldEnd) return 1f;
        return 1f - MathUtils.SmoothStep(s.HoldEnd, s.HoldEnd + (1f - s.HoldEnd) * 0.35f, t);
    }
}
