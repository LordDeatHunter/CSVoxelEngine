using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The server's movement rubberband: judges the position one <see cref="NetMessage.PlayerState"/>
/// reports against the player's last accepted one, by the rules the client's own physics
/// (<see cref="PlayerPhysics"/>) moves the body under. The server never simulates a player's body;
/// it only refuses the positions that body could not have reached.
/// <list type="number">
/// <item>The speed clamp (<see cref="NetValidation.AcceptMove"/>) runs first and keeps its
/// converging behaviour: a too-fast move is <see cref="MoveVerdict.Clamped"/>, never corrected, and
/// the rules below judge the clamped point.</item>
/// <item>Without the flight flag (flight is noclip), a move whose body would overlap a solid cell
/// (<see cref="PlayerPhysics.BodyCollides"/>, the client's collision rule) is
/// <see cref="MoveVerdict.Rejected"/>, unless the body already overlapped one where it was: a
/// player buried by a block written over it may always leave.</item>
/// <item>Without the flight flag, and only over a short state gap
/// (<see cref="WorldGenerationConfig.NetMaxJudgedStateGapSeconds"/>), a climb faster than
/// <see cref="WorldGenerationConfig.NetMaxRiseBlocksPerSecond"/> plus
/// <see cref="WorldGenerationConfig.NetRiseSlackBlocks"/> is rejected, and so is a body that has
/// stayed airborne, out of water and not sinking for longer than
/// <see cref="WorldGenerationConfig.NetHoverGraceSeconds"/> (<see cref="MovementState"/>).</item>
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

    /// <summary>
    /// Judges a move from the accepted eye <paramref name="from"/> to the reported eye
    /// <paramref name="to"/>, <paramref name="delta"/> server seconds after the previous state, by a
    /// player whose server-granted flight flag is <paramref name="flying"/>.
    /// <paramref name="accepted"/> is the eye the player stands at afterwards: <paramref name="to"/>
    /// when accepted, the clamped point when clamped, and <paramref name="from"/> when rejected.
    /// <paramref name="state"/> carries the hover time across calls.
    /// </summary>
    public static MoveVerdict Judge(Vector3 from, Vector3 to, double delta, bool flying, ChunkManager chunks,
        MovementState state, out Vector3 accepted)
    {
        var verdict = NetValidation.AcceptMove(from, to, delta, WorldGenerationConfig.NetMaxMoveBlocksPerSecond,
            out var bounded)
            ? MoveVerdict.Accepted
            : MoveVerdict.Clamped;
        accepted = bounded;

        // Flight is noclip and may climb and hover at will; a body over unloaded cells cannot be
        // judged at all. Neither counts toward a hover.
        if (flying || !PlayerBodyProbe.IsKnown(bounded, chunks))
        {
            state.AirborneStillSeconds = 0;
            return verdict;
        }

        if (PlayerPhysics.BodyCollides(bounded, chunks) && !PlayerPhysics.BodyCollides(from, chunks))
        {
            accepted = from;
            return MoveVerdict.Rejected;
        }

        // After a lag spike the climb and the airborne time between two states say nothing
        if (delta > WorldGenerationConfig.NetMaxJudgedStateGapSeconds)
        {
            state.AirborneStillSeconds = 0;
            return verdict;
        }

        if (bounded.Y - from.Y > WorldGenerationConfig.NetMaxRiseBlocksPerSecond * (float)delta
            + WorldGenerationConfig.NetRiseSlackBlocks)
        {
            accepted = from;
            return MoveVerdict.Rejected;
        }

        var hovering = !PlayerBodyProbe.Grounded(bounded, chunks)
                       && !PlayerBodyProbe.InWater(bounded, chunks)
                       && bounded.Y >= from.Y - HoverSinkToleranceBlocks;
        state.AirborneStillSeconds = hovering ? state.AirborneStillSeconds + delta : 0;
        if (state.AirborneStillSeconds > WorldGenerationConfig.NetHoverGraceSeconds)
        {
            state.AirborneStillSeconds = 0;
            accepted = from;
            return MoveVerdict.Rejected;
        }

        return verdict;
    }
}
