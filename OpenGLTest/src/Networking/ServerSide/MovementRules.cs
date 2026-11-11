using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Player;

namespace VoxelEngine.Networking.ServerSide;

/// <summary>
/// The server's movement rubberband: judges the position one <see cref="NetMessage.PlayerState"/>
/// reports against the player's last accepted one, by the rules the client's own physics
/// (<see cref="PlayerPhysics"/>) moves the body under. The server never simulates a player's body;
/// it only refuses the positions that body could not have reached.
/// <list type="number">
/// <item>The speed clamp (<see cref="NetValidation.AcceptMove"/>) runs first and keeps its
/// converging behavior: a too-fast move is <see cref="MoveVerdict.Clamped"/>, never corrected, and
/// the rules below judge the clamped point.</item>
/// <item>Without the flight flag (flight is noclip), a move whose body would overlap a solid cell
/// (<see cref="BodyCollision.BodyCollides"/>, the client's collision rule) is
/// <see cref="MoveVerdict.Rejected"/>, unless the body already overlapped one where it was: a
/// player buried by a block written over it may always leave.</item>
/// <item>Without the flight flag, and only over a short state gap
/// (<see cref="NetworkConfig.NetMaxJudgedStateGapSeconds"/>), a climb faster than
/// <see cref="NetworkConfig.NetMaxRiseBlocksPerSecond"/> plus
/// <see cref="NetworkConfig.NetRiseSlackBlocks"/> is rejected, and so is a body that has
/// stayed airborne, out of water and not sinking for longer than
/// <see cref="NetworkConfig.NetHoverGraceSeconds"/> (<see cref="MovementState"/>).</item>
/// <item>Over the same short gaps, the airtime rise budget (<see cref="AirRiseBudget"/>): the
/// climb summed since the body was last supported may not pass one jump's apex per jump the player
/// has (the ground jump plus its gear's air jumps) plus <see cref="NetworkConfig.NetAirRiseSlackBlocks"/>,
/// so a second air hop is rejected, and so is every further rise until the body lands. A body
/// grounded or in water starts a fresh airtime; so does a descent turning into a rise with ground
/// within reach below the low state (<see cref="PlayerBodyProbe.GroundedWithin"/>: the landing
/// fell between two states). An airtime begun off a bounce block (<see cref="PlayerBodyProbe.BounceWithin"/>)
/// may climb a bounce's apex more, and a queued knockback lift (<see cref="MovementState.GrantLift"/>)
/// its own climb, for <see cref="NetworkConfig.NetLiftCreditSeconds"/>.</item>
/// </list>
/// A body whose cells are not loaded on the server (<see cref="PlayerBodyProbe.IsKnown"/>) is judged
/// by the speed clamp alone, since an unloaded cell would read as air. A rejected move leaves the
/// player where it was; the caller sends the client a <see cref="NetMessage.PlayerCorrection"/>
/// there. GL-free; main thread only, since it reads the live chunk map.
/// </summary>
public static class MovementRules
{
    // How much lower than the last state a body may report and still count as not sinking: the
    // states around a jump's apex, and float jitter, are not a descent
    private const float HoverSinkToleranceBlocks = 0.01f;

    /// <summary>How far one jump climbs: <see cref="PlayerPhysics.JumpSpeed"/> against its gravity.</summary>
    internal const float JumpApexBlocks = PlayerPhysics.JumpSpeed * PlayerPhysics.JumpSpeed / (2f * PlayerPhysics.Gravity);

    /// <summary>The highest bounce the rise cap lets through: a launch at <see cref="NetworkConfig.NetMaxRiseBlocksPerSecond"/>.</summary>
    internal const float BounceApexBlocks = NetworkConfig.NetMaxRiseBlocksPerSecond
        * NetworkConfig.NetMaxRiseBlocksPerSecond / (2f * PlayerPhysics.Gravity);

    /// <summary>
    /// Judges a move from the accepted eye <paramref name="from"/> to the reported eye
    /// <paramref name="to"/>, <paramref name="delta"/> server seconds after the previous state, by a
    /// player whose server-granted flight flag is <paramref name="flying"/>.
    /// <paramref name="accepted"/> is the eye the player stands at afterwards: <paramref name="to"/>
    /// when accepted, the clamped point when clamped, and <paramref name="from"/> when rejected.
    /// <paramref name="state"/> carries the hover time across calls. <paramref name="solids"/> (the
    /// server's icebergs) only count as ground for the hover rule and the airtime budget: a rider is
    /// never a hoverer. <paramref name="airJumps"/> is how many air jumps the player's worn gear
    /// grants per airtime (the cloud boots: 1), each widening the budget by one jump's climb.
    /// </summary>
    public static MoveVerdict Judge(Vector3 from, Vector3 to, double delta, bool flying, ChunkManager chunks,
        MovementState state, out Vector3 accepted, IBodySolids? solids = null, int airJumps = 0)
    {
        state.TickLiftCredit(delta);
        var verdict = NetValidation.AcceptMove(from, to, delta, NetworkConfig.NetMaxMoveBlocksPerSecond,
            out var bounded)
            ? MoveVerdict.Accepted
            : MoveVerdict.Clamped;
        accepted = bounded;

        // Flight is noclip and may climb and hover at will; a body over unloaded cells cannot be
        // judged at all. Neither counts toward a hover.
        if (flying || !PlayerBodyProbe.IsKnown(bounded, chunks))
        {
            state.AirborneStillSeconds = 0;
            state.ResetAirborne();
            return verdict;
        }

        // Blocks only: a client stands on its mirror of a berg, which lags the server's own by up to
        // a snapshot, so a body touching a berg cannot be judged here (a documented gap).
        if (BodyCollision.BodyCollides(bounded, chunks) && !BodyCollision.BodyCollides(from, chunks))
        {
            accepted = from;
            return MoveVerdict.Rejected;
        }

        // After a lag spike the climb and the airborne time between two states say nothing
        if (delta > NetworkConfig.NetMaxJudgedStateGapSeconds)
        {
            state.AirborneStillSeconds = 0;
            state.ResetAirborne();
            return verdict;
        }

        if (bounded.Y - from.Y > NetworkConfig.NetMaxRiseBlocksPerSecond * (float)delta
            + NetworkConfig.NetRiseSlackBlocks)
        {
            accepted = from;
            return MoveVerdict.Rejected;
        }

        var grounded = PlayerBodyProbe.Grounded(bounded, chunks, solids);
        var inWater = PlayerBodyProbe.InWater(bounded, chunks);
        var hovering = !grounded && !inWater && bounded.Y >= from.Y - HoverSinkToleranceBlocks;
        state.AirborneStillSeconds = hovering ? state.AirborneStillSeconds + delta : 0;
        if (state.AirborneStillSeconds > NetworkConfig.NetHoverGraceSeconds)
        {
            state.AirborneStillSeconds = 0;
            accepted = from;
            return MoveVerdict.Rejected;
        }

        if (!RiseWithinBudget(from, bounded, grounded || inWater, airJumps, chunks, state, solids))
        {
            accepted = from;
            return MoveVerdict.Rejected;
        }

        return verdict;
    }

    /// <summary>
    /// How far one airtime may climb, summed over its upward steps: one jump's apex per jump the
    /// player has (the ground jump plus <paramref name="airJumps"/>), the slack, and the credits
    /// <paramref name="state"/> holds (an airtime begun off a bounce block, a queued knockback lift).
    /// </summary>
    public static float AirRiseBudget(int airJumps, MovementState state) =>
        JumpApexBlocks * (1 + Math.Max(0, airJumps)) + NetworkConfig.NetAirRiseSlackBlocks
        + (state.BounceCredit ? BounceApexBlocks : 0f)
        + (state.LiftCreditSeconds > 0 ? state.LiftCreditBlocks : 0f);

    // The airtime rise budget: sums the climb since the body was last supported and refuses the step
    // that would take it past AirRiseBudget. The sum is kept on a refusal, so every further rise in
    // the same airtime is refused too until the body lands.
    private static bool RiseWithinBudget(Vector3 from, Vector3 to, bool supported, int airJumps,
        ChunkManager chunks, MovementState state, IBodySolids? solids)
    {
        if (supported)
        {
            state.Support(to);
            return true;
        }

        var step = to.Y - from.Y;
        if (step <= 0f)
        {
            if (step < 0f) state.LastDescentBlocks = -step;
            return true;
        }

        // The takeoff: an airtime begun off a bounce block may fling the body past a jump
        if (state.LastSupportEye is { } support)
        {
            state.BounceCredit = PlayerBodyProbe.BounceWithin(support, NetworkConfig.NetGroundProbeBlocks, chunks);
            state.LastSupportEye = null;
        }

        // A descent turning into a rise with ground within reach below the low state: the body
        // touched down between two states (a bunny hop, a bounce), so a fresh airtime starts
        if (state.LastDescentBlocks > 0f)
        {
            var reach = MathF.Max(state.LastDescentBlocks, step);
            if (PlayerBodyProbe.GroundedWithin(from, reach, chunks, solids))
            {
                state.AirRiseBlocks = 0f;
                state.BounceCredit = PlayerBodyProbe.BounceWithin(from, reach + NetworkConfig.NetGroundProbeBlocks, chunks);
            }
            state.LastDescentBlocks = 0f;
        }

        if (state.AirRiseBlocks + step > AirRiseBudget(airJumps, state)) return false;
        state.AirRiseBlocks += step;
        return true;
    }
}
