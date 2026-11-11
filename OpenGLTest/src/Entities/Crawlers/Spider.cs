using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.CaveMobs;

namespace VoxelEngine.Entities.Crawlers;

/// <summary>
/// The giant spider, and its oversized broodmother (a miniboss): a <see cref="CrawlerMob"/> of every cave. In
/// reach and off its cooldown it plays its bite, which lands at the clip's <see cref="ImpactEvent"/>
/// (<see cref="TryTakeBiteImpact"/>) with venom. It dies on its back. Main thread only.
/// </summary>
public sealed class Spider : CrawlerMob
{
    /// <summary>The entity file names (<c>assets/data/entities/spider.json</c>, <c>spider_broodmother.json</c>).</summary>
    public const string EntityName = "spider";
    public const string BroodmotherEntityName = "spider_broodmother";

    public const string BiteClip = "bite";

    /// <summary>Timeline event on the bite clip at which the fangs close.</summary>
    public const string ImpactEvent = "impact";

    private readonly Tuning _tuning;
    private float _biteCooldown;

    private readonly record struct Tuning(float BodySize, float CrawlSpeed, float BiteReach, int BiteDamage,
        float BiteCooldown, float KnockbackSpeed, float KnockbackLift, float VenomScale, float ShoveSpeed);

    private static readonly Tuning Regular = new(SpiderConfig.SpiderBodySize, SpiderConfig.SpiderCrawlSpeed,
        SpiderConfig.SpiderBiteReach, SpiderConfig.SpiderBiteDamage, SpiderConfig.SpiderBiteCooldownSeconds,
        SpiderConfig.SpiderKnockbackSpeed, SpiderConfig.SpiderKnockbackLift, SpiderConfig.SpiderVenomScale,
        SpiderConfig.SpiderShoveSpeed);

    private static readonly Tuning Broodmother = new(SpiderConfig.BroodmotherBodySize,
        SpiderConfig.BroodmotherCrawlSpeed, SpiderConfig.BroodmotherBiteReach, SpiderConfig.BroodmotherBiteDamage,
        SpiderConfig.BroodmotherBiteCooldownSeconds, SpiderConfig.BroodmotherKnockbackSpeed,
        SpiderConfig.BroodmotherKnockbackLift, SpiderConfig.BroodmotherVenomScale, SpiderConfig.BroodmotherShoveSpeed);

    public Spider(EntityType type, Vector3 position, int tier, bool broodmother, bool spawnIn = true)
        : base(type, position, tier, (broodmother ? Broodmother : Regular).BodySize,
            (broodmother ? Broodmother : Regular).CrawlSpeed, spawnIn)
    {
        IsBroodmother = broodmother;
        _tuning = broodmother ? Broodmother : Regular;
    }

    /// <summary>Whether this is the broodmother, the spider den's miniboss.</summary>
    public bool IsBroodmother { get; }

    /// <summary>The edge of the named kind's cube body (where a scenario or a spawn puts its center above the
    /// floor), or 0 for a kind that is no spider.</summary>
    public static float BodySizeOf(string kind) =>
        string.Equals(kind, EntityName, StringComparison.OrdinalIgnoreCase) ? Regular.BodySize
        : string.Equals(kind, BroodmotherEntityName, StringComparison.OrdinalIgnoreCase) ? Broodmother.BodySize
        : 0f;

    /// <inheritdoc />
    public override string AttackClip => BiteClip;

    /// <inheritdoc />
    public override float ShoveSpeed => _tuning.ShoveSpeed;

    /// <inheritdoc />
    protected override float DeathRollRadians => MathF.PI;

    /// <summary>The damage one bite deals at this spider's tier.</summary>
    public int BiteDamage => CaveDifficulty.Scale(_tuning.BiteDamage, Tier);

    /// <summary>How hard a bite throws its target back, and up.</summary>
    public float KnockbackSpeed => _tuning.KnockbackSpeed;

    /// <inheritdoc cref="KnockbackSpeed"/>
    public float KnockbackLift => _tuning.KnockbackLift;

    /// <summary>The share of the venom effect's duration a bite applies.</summary>
    public float VenomScale => _tuning.VenomScale;

    /// <summary>Whether a spider of this kind centered at <paramref name="center"/> can sink its fangs into a
    /// player whose eye is at <paramref name="eye"/>: the chest within its bite reach, from any side.</summary>
    public bool InBiteReach(Vector3 center, Vector3 eye) => ReachesChest(center, eye, _tuning.BiteReach);

    /// <summary>Throws, naming the model file, unless the model has the body groups, the eight two-segment legs
    /// (<see cref="CrawlerRig"/>), idle and walk clips and a bite clip carrying the impact event.</summary>
    public static void ValidateContent(EntityType type) =>
        RequireCrawler(type, "Spider", ["Body", "Head", "Abdomen"], [BiteClip], ImpactEvent);

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta) =>
        _biteCooldown = MathF.Max(0f, _biteCooldown - scaledDelta);

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (!InBiteReach(Position, target.Eye)) return Approach(toward, target, _tuning.BiteReach);
        if (_biteCooldown > 0f) return Vector3.Zero;
        StartAttack();
        _biteCooldown = _tuning.BiteCooldown;
        return Vector3.Zero;
    }

    /// <summary>True once per bite, at the bite clip's impact event; the manager decides who is still in reach.</summary>
    public bool TryTakeBiteImpact() => Animation.ConsumeEvent(ImpactEvent);
}
