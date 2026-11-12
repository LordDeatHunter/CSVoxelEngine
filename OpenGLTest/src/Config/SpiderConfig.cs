namespace VoxelEngine.Config;

/// <summary>
/// Tuning for the giant spiders (<c>Spider</c>): the regular spider and the broodmother miniboss, and where they
/// spawn. How they cling and step is shared with the other crawlers (<c>CrawlerConfig</c>). Speeds are blocks per
/// second, lengths blocks.
/// </summary>
public static class SpiderConfig
{
    // --- The two kinds (Spider's tunings) ---
    // The body is a cube of BodySize, so it collides the same on a floor, a wall and a ceiling. The spider fits a
    // one-block crawlspace; the broodmother needs two.
    public const float SpiderBodySize = 0.64f;
    public const float SpiderCrawlSpeed = 3.4f;
    public const float SpiderBiteReach = 1.7f;
    public const int SpiderBiteDamage = 4;
    public const float SpiderBiteCooldownSeconds = 1.3f;
    public const float SpiderKnockbackSpeed = 3f;
    public const float SpiderKnockbackLift = 2f;
    public const float SpiderVenomScale = 0.6f;
    public const float SpiderShoveSpeed = 3.5f;

    public const float BroodmotherBodySize = 1.5f;
    public const float BroodmotherCrawlSpeed = 2.7f;
    public const float BroodmotherBiteReach = 2.9f;
    public const int BroodmotherBiteDamage = 11;
    public const float BroodmotherBiteCooldownSeconds = 1.7f;
    public const float BroodmotherKnockbackSpeed = 7f;
    public const float BroodmotherKnockbackLift = 4f;
    public const float BroodmotherVenomScale = 1f;
    public const float BroodmotherShoveSpeed = 0.8f;

    // --- The web shot (both kinds) ---
    // Out of bite reach, holding a surface, with the target's chest WebMinRange..WebMaxRange off in a clear line, a
    // spider shoots a web (first WebFirstDelaySeconds after it appears, then every WebCooldownSeconds give or take
    // WebCooldownJitter) flying at WebSpeed; it stings for WebMinDamage..WebMaxDamage with a WebKnockbackSpeed nudge
    // and slows the target (the slowness effect, WebSlownessScale of its duration).
    public const float WebMinRange = 3.5f;
    public const float WebMaxRange = 12f;
    public const float WebFirstDelaySeconds = 1f;
    public const float WebCooldownSeconds = 7f;
    public const float WebCooldownJitter = 1.5f;
    // Its level range v^2/g must clear WebMaxRange
    public const float WebSpeed = 19f;
    public const int WebMinDamage = 1;
    public const int WebMaxDamage = 3;
    public const float WebKnockbackSpeed = 0.5f;
    public const float WebSlownessScale = 1f;

    // --- Spawning (CaveMobManager) ---
    // Spiders share the cave mobs' ring search but take any cave biome: a found floor is kept for a spider with
    // CaveSpawnChance in a cave whose biome has no kind of its own and BiomeCaveSpawnChance where one lives. On the
    // surface they are rare: every SurfaceSpawnIntervalSeconds, at night only, one try with SurfaceSpawnChance. The
    // broodmother never spawns naturally: it is the spider den's miniboss (plans/TODO.md).
    public const float CaveSpawnChance = 0.75f;
    public const float BiomeCaveSpawnChance = 0.3f;
    public const int CavePackMaxSize = 3;
    public const float SurfaceSpawnIntervalSeconds = 20f;
    public const float SurfaceSpawnChance = 0.35f;
    public const float SurfaceSpawnMinRadius = 24f;
    public const float SurfaceSpawnMaxRadius = 44f;
    public const int SurfaceSpawnTries = 8;
}
