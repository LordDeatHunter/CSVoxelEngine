using System;

namespace silkgltriangle;

/// <summary>
/// The sprint footstep dust rule, GL-free: when a footfall happens (the gait clock crossing a foot
/// strike, <see cref="FootfallsBetween"/>) and whether it kicks up a puff (<see cref="Kicks"/>:
/// sprinting on foot, grounded, not sneaking, not in water, really moving, over a block flagged
/// <c>footstepDust</c>). <see cref="GroundDust"/> spawns the puffs.
/// </summary>
public static class FootstepDust
{
    /// <summary>
    /// How many foot strikes the gait clock passed going from <paramref name="previousPhase"/> to
    /// <paramref name="phase"/>: a strike is where the hip swing's sine peaks, at π/2 + kπ
    /// (<see cref="PlayerModel.WalkPhase"/>), so a full stride holds two. 0 when the clock did not
    /// move forward.
    /// </summary>
    public static int FootfallsBetween(float previousPhase, float phase)
    {
        if (!(phase > previousPhase)) return 0;
        return (int)(MathF.Floor((phase - MathF.PI / 2f) / MathF.PI)
                     - MathF.Floor((previousPhase - MathF.PI / 2f) / MathF.PI));
    }

    /// <summary>
    /// Whether a footfall right now puffs: the body sprints on foot (never flying, never sneaking),
    /// stands on the ground, is out of the water, covers at least
    /// <see cref="WorldGenerationConfig.FootstepDustMinSpeed"/> sideways, and the block underfoot
    /// (<paramref name="surfaceId"/>) is loose ground.
    /// </summary>
    public static bool Kicks(bool sprinting, bool grounded, bool flying, bool sneaking, bool inWater,
        float horizontalSpeed, byte surfaceId) =>
        BodyKicks(sprinting, grounded, flying, sneaking, inWater, horizontalSpeed)
        && BlockType.KicksFootstepDust(surfaceId);

    /// <summary>The body half of <see cref="Kicks"/>, whatever is underfoot, so the surface probe only
    /// runs for a footfall that could puff at all.</summary>
    public static bool BodyKicks(bool sprinting, bool grounded, bool flying, bool sneaking, bool inWater,
        float horizontalSpeed) =>
        sprinting && grounded && !flying && !sneaking && !inWater
        && horizontalSpeed >= WorldGenerationConfig.FootstepDustMinSpeed;
}
