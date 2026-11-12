using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.CaveMobs;

namespace VoxelEngine.Entities.Crawlers;

/// <summary>
/// The giant spider, and its oversized broodmother (a miniboss): a <see cref="CrawlerMob"/> of every cave. In
/// reach and off its cooldown it plays its bite, which lands at the clip's <see cref="ImpactEvent"/>
/// (<see cref="TryTakeImpact"/>) with venom. Out of reach, with a clear line and off its own cooldown, it shoots a
/// web that stings lightly and slows. It dies on its back. Main thread only.
/// </summary>
public sealed class Spider : CrawlerMob
{
    /// <summary>The entity file names (<c>assets/data/entities/spider.json</c>, <c>spider_broodmother.json</c>).</summary>
    public const string EntityName = "spider";
    public const string BroodmotherEntityName = "spider_broodmother";

    public const string BiteClip = "bite";

    /// <summary>Timeline event on the bite clip at which the fangs close.</summary>
    public const string ImpactEvent = "impact";

    // The attacks by variant (RealmMob.StartAttack), which a client's mirror replays by this index. The web shot
    // rears up on the bite's clip until it has one of its own.
    private const int BiteAttack = 0;
    private const int WebAttack = 1;

    private readonly Tuning _tuning;
    private float _biteCooldown;
    private float _webCooldown = SpiderConfig.WebFirstDelaySeconds;
    private bool _webbing;

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
    protected override int AttackVariants => 2;

    /// <inheritdoc />
    public override float ShoveSpeed => _tuning.ShoveSpeed;

    /// <inheritdoc />
    protected override float MeleeReach => _tuning.BiteReach;

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

    /// <summary>The damage one web shot carries: a light, random sting (<see cref="SpiderConfig.WebMinDamage"/>..
    /// <see cref="SpiderConfig.WebMaxDamage"/>), the slow being the point.</summary>
    public static int RollWebDamage() => Random.Shared.Next(SpiderConfig.WebMinDamage, SpiderConfig.WebMaxDamage + 1);

    /// <inheritdoc />
    protected override void TickAttackCooldowns(float scaledDelta)
    {
        _biteCooldown = MathF.Max(0f, _biteCooldown - scaledDelta);
        _webCooldown = MathF.Max(0f, _webCooldown - scaledDelta);
    }

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (!InBiteReach(Position, target.Eye))
        {
            if (_webCooldown > 0f
                || !TryAimShot(target, chunks, SpiderConfig.WebMinRange, SpiderConfig.WebMaxRange))
                return Approach(toward, target, _tuning.BiteReach);
            _webbing = true;
            StartAttack(WebAttack);
            _webCooldown = Jittered(SpiderConfig.WebCooldownSeconds, SpiderConfig.WebCooldownJitter);
            return Vector3.Zero;
        }

        if (_biteCooldown > 0f) return Vector3.Zero;
        _webbing = false;
        StartAttack(BiteAttack);
        _biteCooldown = _tuning.BiteCooldown;
        return Vector3.Zero;
    }

    /// <summary>True once per attack, at its clip's impact event, with whether it was a web shot (then the manager
    /// launches the web, <see cref="CrawlerMob.ReleaseShot"/>) or a bite (the manager decides who is still in
    /// reach).</summary>
    public bool TryTakeImpact(out bool web)
    {
        web = _webbing;
        return Animation.ConsumeEvent(ImpactEvent);
    }
}
