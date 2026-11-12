namespace VoxelEngine.Config;

/// <summary>
/// Tuning shared by the mobs that walk on every surface on solved legs (<c>CrawlerMob</c>: the spiders, the
/// scorpion): how a body clings to and crosses surfaces (<c>SurfaceCrawler</c>) and how its legs step
/// (<c>CrawlerLegs</c>). Each kind's own numbers are in <c>SpiderConfig</c> and <c>ScorpionConfig</c>. Speeds are
/// blocks per second, lengths blocks.
/// </summary>
public static class CrawlerConfig
{
    // A crawler stops this far short of its target's chest (as a share of its attack reach) instead of walking
    // into it
    public const float StandoffShare = 0.55f;

    // --- Clinging (SurfaceCrawler) ---
    // A crawler holds onto the surface within ClingReachBlocks under its body and is pulled onto it at
    // ClingSpeed. Walking into a wall that carries at least ClimbShare of its heading it climbs it; walking off an
    // edge it wraps onto the face it left, keeps the carried heading for CommitSeconds and may hang unsupported
    // for WrapGraceSeconds while it rounds the corner (moving at least WrapMinSpeed), after which it lets go and
    // falls. A goal straight through or away from the surface (its share along the surface under
    // MinTangentShare) keeps the carried heading, which is what takes a crawler up a wall toward a target beyond it.
    public const float ClingReachBlocks = 0.5f;
    public const float ClingSpeed = 9f;
    public const float ClimbShare = 0.5f;
    public const float EdgeDropBlocks = 0.25f;
    public const float CommitSeconds = 0.45f;
    public const float WrapGraceSeconds = 0.35f;
    public const float WrapMinSpeed = 1.6f;
    public const float MinTangentShare = 0.3f;
    public const float MoveAccel = 12f;
    public const float TurnRate = 9f;
    // A crawler on a ceiling lets go once its target stands under it within this horizontal distance
    public const float CeilingDropRadiusBlocks = 0.9f;
    // A floating crawler paddles at this share of its crawl speed
    public const float SwimSpeedFactor = 0.5f;

    // --- Getting unstuck (CrawlerNavigator, CrawlerPathfinder, CrawlerMob's leap) ---
    // A chasing crawler that has not come StallProgressBlocks closer to its target's chest for StallSeconds is stuck:
    // it hovers level with a target off the face it holds, or presses into a dead end. It then leaps when it can: off
    // LeapCooldownSeconds, holding a surface, the target's feet LeapMinBlocks..LeapMaxBlocks away in a clear line, on
    // an arc that covers the horizontal distance at LeapHorizontalSpeed (LeapMinSeconds..LeapMaxSeconds in the air)
    // and launches no faster than LeapMaxSpeed, away from the face it holds. Else it plans a path over the block
    // faces (A* over at most PathMaxNodes footholds, falls of at most PathMaxDropBlocks, one plan every
    // PathRetrySeconds) and follows it, giving it up when it reaches no further foothold for PathStallSeconds or the
    // target moves PathRetargetBlocks from where it was planned to.
    public const float StallSeconds = 1f;
    public const float StallProgressBlocks = 0.5f;
    public const float LeapCooldownSeconds = 3f;
    public const float LeapMinBlocks = 2f;
    public const float LeapMaxBlocks = 7f;
    public const float LeapHorizontalSpeed = 8f;
    public const float LeapMinSeconds = 0.35f;
    public const float LeapMaxSeconds = 0.8f;
    public const float LeapMaxSpeed = 16f;
    public const int PathMaxNodes = 1500;
    public const int PathMaxDropBlocks = 12;
    public const float PathRetrySeconds = 1f;
    public const float PathStallSeconds = 1.5f;
    public const float PathRetargetBlocks = 3f;

    // --- Legs (CrawlerLegs, LegIk) ---
    // Each foot rests LegStanceShare of the leg's length out from its hip, the four legs of a side fanned at
    // LegAzimuthDegrees (forward of straight out, front leg first). A planted foot steps once it is LegStepShare
    // of the leg's length from its rest point (or out of reach), taking LegStepSeconds and lifting
    // LegStepLiftShare of the leg's length; the two alternating groups of four never step at once unless a foot
    // is LegUrgentShare away. The step lands LegLeadShare of the leg's length ahead along the body's motion (plus
    // what the body covers meanwhile). A hold is probed LegProbeDepthShare of the leg's length under the rest
    // point; a foot with nothing to hold hangs LegDangleShare under it. The body's up eases toward the plane of
    // the planted feet (FeetNormalWeight of it, the rest the clung surface) at BodyTurnRate.
    public const float LegStanceShare = 0.62f;
    public static readonly float[] LegAzimuthDegrees = [52f, 18f, -16f, -50f];
    public const float LegStepShare = 0.24f;
    public const float LegUrgentShare = 0.6f;
    public const float LegStepSeconds = 0.12f;
    public const float LegStepLiftShare = 0.2f;
    public const float LegLeadShare = 0.2f;
    public const float LegProbeDepthShare = 0.55f;
    public const float LegDangleShare = 0.12f;
    public const float FeetNormalWeight = 0.6f;
    public const float BodyTurnRate = 10f;
}
