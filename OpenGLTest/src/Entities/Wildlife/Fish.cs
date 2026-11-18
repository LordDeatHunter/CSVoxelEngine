using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// A sea fish: the cod, the salmon and the tuna are one class over one model contract (<c>Body</c> &gt; <c>Head</c>
/// &gt; <c>Jaw</c>, the fins and a tail; the clips idle, swim, dart, flop, hurt and death) with their own
/// proportions and speeds (<see cref="WildlifeConfig"/>). Neutral: it wanders, in a school that follows its first
/// fish (<see cref="Follow"/>); a threat within <see cref="WildlifeConfig.FishDartRangeBlocks"/> (further for
/// <see cref="WildlifeConfig.FishAlarmSeconds"/> after a blow) sends it fleeing on its <c>dart</c> clip.
/// Out of water it flops (<see cref="AquaticAnimal"/>). Main thread only.
/// </summary>
public sealed class Fish : AquaticAnimal
{
    /// <summary>The entity file names (<c>assets/data/entities/&lt;name&gt;.json</c>).</summary>
    public const string CodName = "cod";
    public const string SalmonName = "salmon";
    public const string TunaName = "tuna";

    public const string DartClipName = "dart";
    public const string FlopClipName = "flop";

    private static readonly string[] Parts = ["Body", "Head", "Jaw", "Tail"];

    private readonly WildlifeKind _kind;
    private Fish? _leader;
    private Vector3 _schoolOffset;
    private float _alarmSeconds;

    public Fish(EntityType type, Vector3 position, WildlifeKind kind, bool spawnIn = true)
        : base(type, position, TuningOf(kind), spawnIn)
    {
        _kind = kind;
    }

    /// <summary>How the fish of <paramref name="kind"/> swims.</summary>
    public static FlightTuning TuningOf(WildlifeKind kind) => kind switch
    {
        WildlifeKind.Cod => Tuning(WildlifeConfig.CodCruiseSpeed, WildlifeConfig.CodHoverMinBlocks, WildlifeConfig.CodHoverMaxBlocks),
        WildlifeKind.Salmon => Tuning(WildlifeConfig.SalmonCruiseSpeed, WildlifeConfig.SalmonHoverMinBlocks, WildlifeConfig.SalmonHoverMaxBlocks),
        WildlifeKind.Tuna => Tuning(WildlifeConfig.TunaCruiseSpeed, WildlifeConfig.TunaHoverMinBlocks, WildlifeConfig.TunaHoverMaxBlocks),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a fish"),
    };

    private static FlightTuning Tuning(float cruise, float hoverMin, float hoverMax) => new(
        cruise, WildlifeConfig.FishVerticalSpeed, WildlifeConfig.FishAcceleration, hoverMin, hoverMax,
        WildlifeConfig.FishMinClearanceBlocks, WildlifeConfig.FishBobBlocks, WildlifeConfig.FishBobHz);

    /// <inheritdoc />
    public override WildlifeKind Kind => _kind;

    /// <inheritdoc />
    public override float BodyHeight => HeightOf(_kind);

    /// <inheritdoc />
    public override float BodyHalfWidth => HalfExtentOf(_kind);

    /// <inheritdoc />
    public override float ShoveSpeed => WildlifeConfig.FishShoveSpeed;

    /// <summary>The body height of the fish of <paramref name="kind"/>.</summary>
    public static float HeightOf(WildlifeKind kind) => kind switch
    {
        WildlifeKind.Cod => WildlifeConfig.CodHeight,
        WildlifeKind.Salmon => WildlifeConfig.SalmonHeight,
        _ => WildlifeConfig.TunaHeight,
    };

    /// <summary>Half the horizontal body edge of the fish of <paramref name="kind"/>.</summary>
    public static float HalfExtentOf(WildlifeKind kind) => kind switch
    {
        WildlifeKind.Cod => WildlifeConfig.CodHalfExtent,
        WildlifeKind.Salmon => WildlifeConfig.SalmonHalfExtent,
        _ => WildlifeConfig.TunaHalfExtent,
    };

    // A fish never attacks; its one counted action slot only fills the realm mob's contract
    /// <inheritdoc />
    public override string AttackClip => EntityAnimator.IdleClip;

    /// <inheritdoc />
    protected override string? BeachedClip => FlopClipName;

    /// <inheritdoc />
    protected override string? DartClip => DartClipName;

    /// <inheritdoc />
    protected override float DartSpeed => Flight!.Value.CruiseSpeed * WildlifeConfig.FishDartSpeedFactor;

    // Fleeing, it faces the way it swims
    /// <inheritdoc />
    protected override bool FacesTarget => false;

    /// <summary>Throws, naming the model file, unless the model has the fish rig and its idle, swim, dart and
    /// flop clips.</summary>
    public static void ValidateContent(EntityType type)
    {
        RequireSwimmer(type, "Fish", Parts);
        EntityContract.RequireClip(type, "Fish", DartClipName);
        EntityContract.RequireClip(type, "Fish", FlopClipName);
    }

    /// <summary>Joins the school of <paramref name="leader"/>: while it lives, a calm fish swims to its place
    /// beside it (a fixed offset within <see cref="WildlifeConfig.SchoolSpreadBlocks"/>) instead of wandering.</summary>
    public void Follow(Fish leader)
    {
        _leader = leader;
        var spread = WildlifeConfig.SchoolSpreadBlocks;
        _schoolOffset = new Vector3(Roll(-spread, spread), Roll(-spread * 0.4f, spread * 0.4f), Roll(-spread, spread));
    }

    /// <summary>The range inside which a threat sends a fish fleeing, <paramref name="alarmed"/> after a blow.</summary>
    public static float AlertRange(bool alarmed) =>
        WildlifeConfig.FishDartRangeBlocks * (alarmed ? WildlifeConfig.FishAlarmFactor : 1f);

    /// <inheritdoc />
    public override bool Heeds(IReadOnlyList<IMobTarget> targets)
    {
        var range = AlertRange(_alarmSeconds > 0f);
        return NearestThreatSquared(targets) <= range * range;
    }

    /// <inheritdoc />
    public override void TakeDamage(int amount)
    {
        base.TakeDamage(amount);
        if (amount > 0) _alarmSeconds = WildlifeConfig.FishAlarmSeconds;
    }

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta) => _alarmSeconds = MathF.Max(0f, _alarmSeconds - scaledDelta);

    // The move it flees along; it only sets the facing, the swim goal is picked in SwimToward
    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target) => -toward;

    /// <inheritdoc />
    protected override void SwimToward(IMobTarget? target, Vector3? wander, float deltaTime, ChunkManager chunks)
    {
        if (target != null)
        {
            Darting = true;
            var away = DirectionTo(target.Eye, Position);
            // Straight away from the threat, a little less steeply than it came
            away.Y *= 0.5f;
            Swim(Position + away * WildlifeConfig.FishFleeBlocks, chasing: true, WildlifeConfig.FishDartSpeedFactor,
                deltaTime, chunks);
            return;
        }

        Darting = false;
        if (_leader is { IsDying: false } leader)
        {
            Swim(leader.Position + _schoolOffset, chasing: false, MobConfig.MobWanderSpeedFactor * 1.5f, deltaTime, chunks);
            return;
        }

        Swim(wander, chasing: false, wander == null ? 1f : MobConfig.MobWanderSpeedFactor, deltaTime, chunks);
    }
}
