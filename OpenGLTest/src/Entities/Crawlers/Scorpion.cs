using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.CaveMobs;

namespace VoxelEngine.Entities.Crawlers;

/// <summary>
/// The giant scorpion of the desert and its caves: a <see cref="CrawlerMob"/> with two attacks. In reach of its
/// target it stings when the sting is off its cooldown (the tail whips over its head, venom), else pinches when
/// the pinch is (the claws snap shut, a hard shove). Each lands at its clip's <see cref="ImpactEvent"/>
/// (<see cref="TryTakeStrike"/>), where the manager bills whoever is still in reach. Main thread only.
/// </summary>
public sealed class Scorpion : CrawlerMob
{
    /// <summary>The entity file name (<c>assets/data/entities/scorpion.json</c>).</summary>
    public const string EntityName = "scorpion";

    public const string PinchClip = "pinch";
    public const string StingClip = "sting";

    /// <summary>Timeline event on either attack clip at which the blow connects.</summary>
    public const string ImpactEvent = "impact";

    // The attack clips by variant (RealmMob.StartAttack), which a client's mirror replays by this index
    private const int PinchAttack = 0;
    private const int StingAttack = 1;

    private float _pinchCooldown;
    private float _stingCooldown;
    private bool _stinging;

    public Scorpion(EntityType type, Vector3 position, int tier, bool spawnIn = true)
        : base(type, position, tier, ScorpionConfig.BodySize, ScorpionConfig.CrawlSpeed, spawnIn)
    {
    }

    /// <inheritdoc />
    public override string AttackClip => PinchClip;

    /// <inheritdoc />
    protected override int AttackVariants => 2;

    /// <inheritdoc />
    protected override string AttackClipOf(int variant) => variant == StingAttack ? StingClip : PinchClip;

    /// <inheritdoc />
    public override float ShoveSpeed => ScorpionConfig.ShoveSpeed;

    /// <summary>The damage a pinch deals at this scorpion's tier.</summary>
    public int PinchDamage => CaveDifficulty.Scale(ScorpionConfig.PinchDamage, Tier);

    /// <summary>The damage a sting deals at this scorpion's tier.</summary>
    public int StingDamage => CaveDifficulty.Scale(ScorpionConfig.StingDamage, Tier);

    /// <summary>Whether a scorpion centered at <paramref name="center"/> can strike a player whose eye is at
    /// <paramref name="eye"/>: the chest within its attack reach, from any side.</summary>
    public static bool InAttackReach(Vector3 center, Vector3 eye) =>
        ReachesChest(center, eye, ScorpionConfig.AttackReach);

    /// <summary>Throws, naming the model file, unless the model has the body, claw and tail groups, the eight
    /// two-segment legs (<see cref="CrawlerRig"/>), idle and walk clips and the pinch and sting clips, each
    /// carrying the impact event.</summary>
    public static void ValidateContent(EntityType type) =>
        RequireCrawler(type, "Scorpion", ["Body", "ClawL", "ClawR", "Tail1", "Stinger"], [PinchClip, StingClip],
            ImpactEvent);

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta)
    {
        _pinchCooldown = MathF.Max(0f, _pinchCooldown - scaledDelta);
        _stingCooldown = MathF.Max(0f, _stingCooldown - scaledDelta);
    }

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (!InAttackReach(Position, target.Eye)) return Approach(toward, target, ScorpionConfig.AttackReach);

        if (_stingCooldown <= 0f)
        {
            _stinging = true;
            _stingCooldown = ScorpionConfig.StingCooldownSeconds;
            StartAttack(StingAttack);
        }
        else if (_pinchCooldown <= 0f)
        {
            _stinging = false;
            _pinchCooldown = ScorpionConfig.PinchCooldownSeconds;
            StartAttack(PinchAttack);
        }

        return Vector3.Zero;
    }

    /// <summary>True once per attack, at its clip's impact event, with whether it was the sting; the manager
    /// decides who is still in reach.</summary>
    public bool TryTakeStrike(out bool sting)
    {
        sting = _stinging;
        return Animation.ConsumeEvent(ImpactEvent);
    }
}
