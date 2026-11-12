namespace VoxelEngine.Config;

/// <summary>
/// Tuning for the giant scorpion (<c>Scorpion</c>) of the desert and its caves: its body and two attacks, and
/// where it spawns. How it clings and steps is shared with the other crawlers (<c>CrawlerConfig</c>). Speeds are
/// blocks per second, lengths blocks.
/// </summary>
public static class ScorpionConfig
{
    // The body is a cube of BodySize (it fits a one-block crawlspace; the tail does not collide).
    public const float BodySize = 0.9f;
    public const float CrawlSpeed = 3f;
    public const float ShoveSpeed = 1.5f;

    // In AttackReach of its target's chest it stings when the sting is off its cooldown (the tail whips over its
    // head: StingDamage and venom for StingVenomScale of the effect's duration), else pinches when the pinch is
    // (the claws snap shut: PinchDamage and a hard shove). Each lands at its clip's impact event.
    public const float AttackReach = 2.1f;
    public const int PinchDamage = 5;
    public const float PinchCooldownSeconds = 1.1f;
    public const float PinchKnockbackSpeed = 6f;
    public const float PinchKnockbackLift = 2.5f;
    public const int StingDamage = 8;
    public const float StingCooldownSeconds = 4f;
    public const float StingKnockbackSpeed = 2f;
    public const float StingKnockbackLift = 1f;
    public const float StingVenomScale = 1.4f;

    // --- Spawning (CaveMobManager) ---
    // Scorpions take CaveSpawnChance of the spawn floors found in the desert's echo caves (the rest are rolled as
    // in any plain cave). On the desert's surface one try runs every SurfaceSpawnIntervalSeconds around a target
    // not underground, kept only on a desert column, always at night and with DaySpawnChance by day.
    public const float CaveSpawnChance = 0.6f;
    public const int CavePackMaxSize = 2;
    public const float SurfaceSpawnIntervalSeconds = 16f;
    public const float DaySpawnChance = 0.3f;
    public const float SurfaceSpawnMinRadius = 24f;
    public const float SurfaceSpawnMaxRadius = 44f;
    public const int SurfaceSpawnTries = 8;
}
