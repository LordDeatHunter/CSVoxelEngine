using System;

namespace VoxelEngine.Interaction;

/// <summary>
/// GL-free rules of each sword swing (<see cref="SwordSwing"/>): where in the chain it sits, how
/// many enemies it reaches and through which shape, how hard it hits and shoves, and how heavy the
/// hit-stop of a connecting one is. The numbers describe the weapon's feel, so the slower, bigger
/// swings pay off: the chop is the chain's reward. Shared by the attack loop, the server replay
/// and the tests; the animation itself is <c>SwordMotion</c>.
/// </summary>
public static class SwordSwings
{
    /// <summary>How many enemies a sweeping swing (the cut and the back cut) hits at most: the
    /// nearest ones in the forward hemisphere.</summary>
    public const int SweepTargets = 3;

    /// <summary>The swing that follows <paramref name="swing"/> in the chain; the chop wraps back to the cut.</summary>
    public static SwordSwing Next(SwordSwing swing) =>
        swing == SwordSwing.Chop ? SwordSwing.Cut : (SwordSwing)((byte)swing + 1);

    /// <summary>The share of the held item's <c>entityDamage</c> the swing deals: the cut in full,
    /// the quicker back cut less, the chop the most.</summary>
    public static float Power(SwordSwing swing) => swing switch
    {
        SwordSwing.BackCut => 0.75f,
        SwordSwing.Chop => 1.75f,
        _ => 1f,
    };

    /// <summary>How many enemies the swing hits at most, nearest first.</summary>
    public static int MaxTargets(SwordSwing swing) => swing == SwordSwing.Chop ? 1 : SweepTargets;

    /// <summary>Whether the swing reaches only the narrow punch cone under the aim (the chop) rather
    /// than the whole forward hemisphere (the sweeps). See <see cref="Combat.InPunchCone"/> and
    /// <see cref="Combat.InFrontHemisphere"/>.</summary>
    public static bool IsCone(SwordSwing swing) => swing == SwordSwing.Chop;

    /// <summary>What the swing's knockback is, as a multiple of a punch's shove: the chop throws its
    /// target back hardest.</summary>
    public static float ShoveFactor(SwordSwing swing) => swing == SwordSwing.Chop ? 2f : 1f;

    /// <summary>The weight of the hit-stop a connecting swing asks for (0 a light swing, 1 the
    /// heaviest; see <see cref="Juice.SwingHitStop.SecondsFor"/>): the chop lands like the slowest tool.</summary>
    public static float HitStopWeight(SwordSwing swing) => swing == SwordSwing.Chop ? 1f : 0.4f;

    /// <summary>Every swing, in chain order.</summary>
    public static ReadOnlySpan<SwordSwing> All => [SwordSwing.Cut, SwordSwing.BackCut, SwordSwing.Chop];
}
