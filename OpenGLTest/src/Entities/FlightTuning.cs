using System;

namespace VoxelEngine.Entities;

/// <summary>
/// How one flying mob kind flies (<see cref="RiggedMob.Fly"/>, <see cref="FlightSteering"/>): a mob
/// built with one is a flyer (<see cref="Mob.Flies"/>), derived from its kind, so nothing about it
/// rides the wire. Speeds are in blocks per second, heights in blocks.
/// </summary>
/// <param name="CruiseSpeed">The horizontal speed it flies toward a goal at.</param>
/// <param name="VerticalSpeed">The fastest it climbs or dives.</param>
/// <param name="Acceleration"><see cref="MathUtils.Damp"/> rate of the velocity toward the steered one.</param>
/// <param name="HoverMinBlocks">The low end of its hover band: above a chased target's eye, or above the
/// floor while idle.</param>
/// <param name="HoverMaxBlocks">The high end of its hover band.</param>
/// <param name="MinClearanceBlocks">The least gap it keeps between its body and the floor below while it
/// chases (at most <paramref name="HoverMinBlocks"/>).</param>
/// <param name="BobBlocks">How far its gentle hover bob swings above and below the goal height.</param>
/// <param name="BobHz">How many bobs it makes a second.</param>
public readonly record struct FlightTuning(
    float CruiseSpeed,
    float VerticalSpeed,
    float Acceleration,
    float HoverMinBlocks,
    float HoverMaxBlocks,
    float MinClearanceBlocks,
    float BobBlocks,
    float BobHz)
{
    /// <summary>Throws, naming the broken rule, unless the speeds are positive and the clearance, the
    /// hover band and the bob are ordered (0 &lt;= clearance &lt;= hover min &lt;= hover max).</summary>
    public void Validate()
    {
        if (CruiseSpeed <= 0f || VerticalSpeed <= 0f || Acceleration <= 0f)
            throw new ArgumentException("A flight needs a positive cruise speed, vertical speed and acceleration.");
        if (MinClearanceBlocks < 0f || HoverMinBlocks < MinClearanceBlocks || HoverMaxBlocks < HoverMinBlocks)
            throw new ArgumentException("A flight's clearance and hover band must be ordered: 0 <= clearance <= min <= max.");
        if (BobBlocks < 0f || BobHz < 0f)
            throw new ArgumentException("A flight's bob cannot be negative.");
    }
}
