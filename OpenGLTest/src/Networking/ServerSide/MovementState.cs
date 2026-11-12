using System;
using System.Numerics;
using VoxelEngine.Player;

namespace VoxelEngine.Networking.ServerSide;

/// <summary>
/// What <see cref="MovementRules"/> remembers about one connected player between its states (held
/// on <see cref="ServerPlayer.Movement"/>). GL-free; main thread only, like the rest of the
/// server's player state.
/// </summary>
public sealed class MovementState
{
    /// <summary>
    /// Server seconds the player has spent airborne, out of water and not sinking, over its last
    /// consecutive judged states; anything that lands, swims, falls, flies or lags resets it. Past
    /// <see cref="NetworkConfig.NetHoverGraceSeconds"/> the player is hovering.
    /// </summary>
    public double AirborneStillSeconds { get; set; }

    /// <summary>
    /// Upward distance summed over the positive steps since the body was last supported (grounded
    /// or in water): what one airtime has climbed, judged against
    /// <see cref="MovementRules.AirRiseBudget"/>.
    /// </summary>
    public float AirRiseBlocks { get; set; }

    /// <summary>How far the previous judged step descended; 0 after a rise.</summary>
    public float LastDescentBlocks { get; set; }

    /// <summary>
    /// Where the body last stood supported. It is checked once, at the first rise after it, for a
    /// bounce block (the takeoff), then cleared.
    /// </summary>
    public Vector3? LastSupportEye { get; set; }

    /// <summary>This airtime began off a bounce block, which may fling the body higher than a jump.</summary>
    public bool BounceCredit { get; set; }

    /// <summary>
    /// Extra climb a queued knockback lift allows the current airtime
    /// (<see cref="GrantLift"/>), while <see cref="LiftCreditSeconds"/> runs.
    /// </summary>
    public float LiftCreditBlocks { get; set; }

    /// <summary>Server seconds the lift credit still holds; 0 means none.</summary>
    public double LiftCreditSeconds { get; set; }

    /// <summary>The body stands supported at <paramref name="eye"/>: a fresh airtime starts from here.</summary>
    public void Support(Vector3 eye)
    {
        AirRiseBlocks = 0f;
        LastDescentBlocks = 0f;
        BounceCredit = false;
        LastSupportEye = eye;
    }

    /// <summary>Forgets the current airtime (flight, an unknown body, a lag gap, a teleport).</summary>
    public void ResetAirborne()
    {
        AirRiseBlocks = 0f;
        LastDescentBlocks = 0f;
        BounceCredit = false;
        LastSupportEye = null;
    }

    /// <summary>
    /// A mob hit or blast queued a lift of <paramref name="lift"/> blocks/s: the climb it gives
    /// (<c>lift^2 / 2g</c>) widens the airtime budget for <see cref="NetworkConfig.NetLiftCreditSeconds"/>.
    /// </summary>
    public void GrantLift(float lift)
    {
        LiftCreditBlocks = MathF.Max(LiftCreditSeconds > 0 ? LiftCreditBlocks : 0f,
            lift * lift / (2f * PlayerPhysics.Gravity));
        LiftCreditSeconds = NetworkConfig.NetLiftCreditSeconds;
    }

    /// <summary>Counts the lift credit down by <paramref name="delta"/> server seconds, clearing it at 0.</summary>
    public void TickLiftCredit(double delta)
    {
        if (LiftCreditSeconds <= 0) return;
        LiftCreditSeconds -= delta;
        if (LiftCreditSeconds > 0) return;
        LiftCreditSeconds = 0;
        LiftCreditBlocks = 0f;
    }

    /// <summary>Forgets everything: a teleport puts the body somewhere new.</summary>
    public void Reset()
    {
        ResetAirborne();
        AirborneStillSeconds = 0;
        LiftCreditBlocks = 0f;
        LiftCreditSeconds = 0;
    }
}
