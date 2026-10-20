namespace VoxelEngine.Player.Posing;

/// <summary>The slice swing's phase shapes: the arm and body sweeps, the stroke, the hand's diagonal and the main-cut weight, as pure functions of the slice clock.</summary>
internal static class SliceMotion
{
    // The wind-up cocks the arm further out than the strike carries it back, so the swing reads
    // as a wide arc; SliceSweepRadians is the follow-through past the aim.
    internal const float SliceWindupRadians = 1.0f;
    internal const float SliceSweepRadians = 0.8f;
    // Wind-up (0..WindupEnd) reaches the start side; the strike (..StrikeEnd) crosses to the far
    // side; the rest is the recovery back to the idle pose.
    //
    // The phases are uneven on purpose, with the same total duration: a slower wind-up that coils the
    // body (to 0.26), a short, sharp strike that is centered on the impact (0.26..0.49, about twice as
    // fast as before) and a long, eased recovery, so the contrast reads as a snap.
    internal const float SliceWindupEnd = 0.26f;
    internal const float SliceStrikeEnd = 0.49f;

    // The slice is a whole-body motion: the arm sweep is only part of it. The entire body turns about
    // the feet (SliceBodyYaw, the legs counter-turning so the feet stay planted), the shoulders turn
    // further (SliceTorsoYaw), the weight drops into the strike, and the hand travels a small
    // diagonal from well above the chest line (SliceStartRaiseBlocks) down to just below it (SliceEndDropBlocks) on every slice,
    // whichever way it swings.
    internal const float SliceBodyYaw = 0.42f;
    internal const float SliceTorsoYaw = 0.5f;
    internal const float SliceLegCounter = 0.6f;
    internal const float SliceStartRaiseBlocks = 0.28f;
    internal const float SliceEndDropBlocks = 0.10f;
    internal const float SliceBodyWindup = 1.0f;
    internal const float SliceBodyFar = 0.85f;

    // Signed yaw the right arm sweeps through: 0 at rest, out to the swing's start side on the
    // wind-up, across the aim to the far side on the strike, then eased back to 0 on the recovery
    // so the arm never snaps back to its idle pose. The sign follows the swing's direction, so
    // the return mirrors the first swing.
    internal static float SliceSweep(float sliceT, int sliceDir) => SlicePhase(sliceT, sliceDir, SliceWindupRadians, SliceSweepRadians);

    // The whole body's turn through the same phases, normalized: about 1 at the wind-up (toward the
    // swing's start side) and -0.85 at the follow-through, with the sign of the swing's direction.
    internal static float SliceBodySweep(float sliceT, int sliceDir) => SlicePhase(sliceT, sliceDir, SliceBodyWindup, SliceBodyFar);

    private static float SlicePhase(float sliceT, int sliceDir, float windupAmplitude, float farAmplitude)
    {
        var p = MathUtils.Clamp01(sliceT);
        if (p <= 0f || p >= 1f) return 0f;

        var startSide = -windupAmplitude * sliceDir;
        var farSide = farAmplitude * sliceDir;
        if (p < SliceWindupEnd)
            return MathUtils.Lerp(0f, startSide, MathUtils.SmoothStep(p / SliceWindupEnd));
        // The strike accelerates hard through the middle (a steeper curve than the other phases).
        if (p < SliceStrikeEnd)
            return MathUtils.Lerp(startSide, farSide,
                MathUtils.EaseInOutCubic((p - SliceWindupEnd) / (SliceStrikeEnd - SliceWindupEnd)));
        return MathUtils.Lerp(farSide, 0f,
            MathUtils.SmoothStep((p - SliceStrikeEnd) / (1f - SliceStrikeEnd)));
    }

    // 0 -> 1 across the wind-up, 1 through the strike, back to 0 over the recovery: it drives the arm
    // lift and the blade roll, which are raised before the strike starts and held through it. It is 0
    // at both ends, so the swing never jumps when it starts or finishes.
    internal static float SliceStroke(float sliceT)
    {
        var p = MathUtils.Clamp01(sliceT);
        if (p <= 0f || p >= 1f) return 0f;
        if (p < SliceWindupEnd) return MathUtils.SmoothStep(p / SliceWindupEnd);
        if (p < SliceStrikeEnd) return 1f;
        return 1f - MathUtils.SmoothStep((p - SliceStrikeEnd) / (1f - SliceStrikeEnd));
    }

    // The small diagonal: the hand rises a little in the wind-up, is carried from above the chest line
    // to below it through the strike and returns over the recovery. The same for either direction, so
    // every slice cuts from the top toward the bottom.
    internal static float SliceDiagonal(float sliceT)
    {
        var p = MathUtils.Clamp01(sliceT);
        if (p <= 0f || p >= 1f) return 0f;
        if (p < SliceWindupEnd)
            return SliceStartRaiseBlocks * MathUtils.SmoothStep(p / SliceWindupEnd);
        if (p < SliceStrikeEnd)
            return MathUtils.Lerp(SliceStartRaiseBlocks, -SliceEndDropBlocks,
                MathUtils.EaseInOutCubic((p - SliceWindupEnd) / (SliceStrikeEnd - SliceWindupEnd)));
        return -SliceEndDropBlocks * (1f - MathUtils.SmoothStep((p - SliceStrikeEnd) / (1f - SliceStrikeEnd)));
    }

    // The hand's offset from where the extended arm puts it: the diagonal alone (it starts above that height and
    // finishes below it), through the same stroke so it is back at the idle pose by the end (no snap).
    internal static float SliceDrop(float sliceT) => SliceDiagonal(sliceT);

    // The main part of the cut, as a weight: 0 until the wind-up is nearly over, full through the strike and faded out
    // again over the start of the recovery, so what rides it (the blade's turn, its display offset) comes into the cut
    // and goes back out of it, and the rest frames are untouched.
    internal static float SliceMainWeight(float sliceT)
    {
        var p = MathUtils.Clamp01(sliceT);
        if (p <= 0f || p >= 1f) return 0f;
        if (p < SliceWindupEnd) return MathUtils.SmoothStep(0.15f, SliceWindupEnd, p);
        if (p < SliceStrikeEnd) return 1f;
        return 1f - MathUtils.SmoothStep(SliceStrikeEnd, 0.65f, p);
    }
}
