using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// An animal that flies but also comes down to rest (<see cref="Crow"/>, <see cref="Dragonfly"/>). In the air
/// it steers like any flyer (<see cref="FlyToward"/>); every so often, with nobody to react to and solid
/// ground under it, it lands (its <c>land</c> clip) and from then on is a walker standing still, held down by
/// gravity, playing its ground clips. It takes off again (its <c>takeoff</c> clip, with a kick upward) when
/// its rest is over, when it is hurt, or when it heeds somebody, unless its species would rather stay down
/// for that target (<see cref="StaysPerchedNear"/>). The locomotion clips follow the state
/// (<see cref="EntityAnimator.SetGait"/>), and since a client cannot tell a hovering body from a sitting one,
/// the state rides the animal's <see cref="Posture"/> to its mirror. Main thread only.
/// </summary>
public abstract class FlyingAnimal : Animal
{
    private const byte PerchedBit = 1;
    private const byte ChasingBit = 2;

    private bool _perched;
    private bool _chasing;
    private float _postureTimer;

    protected FlyingAnimal(EntityType type, Vector3 position, FlightTuning flight, bool spawnIn)
        : base(type, position, flight.CruiseSpeed, spawnIn, flight)
    {
        _postureTimer = Roll(WildlifeConfig.FlyerAirMinSeconds, WildlifeConfig.FlyerAirMaxSeconds);
        ApplyGait();
    }

    /// <summary>Whether it sits on the ground rather than flies.</summary>
    public bool IsPerched => _perched;

    /// <summary>The idle and moving clips it plays on the ground.</summary>
    protected abstract (string Idle, string Move) GroundClips { get; }

    /// <summary>The idle and moving clips it plays in the air, <paramref name="chasing"/> somebody or not.</summary>
    protected abstract (string Idle, string Move) AirClips(bool chasing);

    /// <summary>The counted action (<see cref="RealmMob.StartAttack(int)"/>) that plays its take-off clip.</summary>
    protected abstract int TakeoffAction { get; }

    /// <summary>The counted action that plays its landing clip.</summary>
    protected abstract int LandAction { get; }

    /// <summary>Whether, sitting on the ground, it stays there for <paramref name="target"/> instead of taking
    /// off (a crow pecking at the ankles beside it). False by default.</summary>
    protected virtual bool StaysPerchedNear(IMobTarget target) => false;

    // A flyer's walk slots only fill the walker's contract: it never walks anywhere
    /// <inheritdoc />
    protected override float WalkSpeed => Flight!.Value.CruiseSpeed;

    /// <inheritdoc />
    protected override float JumpSpeed => 0f;

    /// <inheritdoc />
    public override byte Posture => (byte)((_perched ? PerchedBit : 0) | (_chasing ? ChasingBit : 0));

    /// <inheritdoc />
    public override void MirrorPosture(byte posture)
    {
        var perched = (posture & PerchedBit) != 0;
        var chasing = (posture & ChasingBit) != 0;
        if (perched == _perched && chasing == _chasing) return;
        _perched = perched;
        _chasing = chasing;
        ApplyGait();
    }

    /// <inheritdoc />
    public override void TakeDamage(int amount)
    {
        base.TakeDamage(amount);
        if (amount > 0 && _perched && !IsDying) TakeOff();
    }

    /// <inheritdoc />
    protected override void Locomote(Vector3 move, IMobTarget? target, Vector3? wander, float deltaTime,
        ChunkManager chunks)
    {
        if (!IsDying)
        {
            if (target == null) IdleTick(deltaTime);
            TickPosture(target, deltaTime, chunks);
            if (target != null != _chasing)
            {
                _chasing = target != null;
                ApplyGait();
            }
        }

        if (_perched && !IsDying)
        {
            // On the ground it is a walker standing still: gravity settles it there and holds it
            Walk(Vector3.Zero, null, 0f, 0f, 0f, deltaTime, chunks);
            return;
        }

        FlyToward(target, wander, deltaTime, chunks);
    }

    /// <summary>One frame of flight: toward its place over <paramref name="target"/> (or at it, mid-attack),
    /// to <paramref name="wander"/>, or hovering where it is.</summary>
    protected abstract void FlyToward(IMobTarget? target, Vector3? wander, float deltaTime, ChunkManager chunks);

    // Lands when its time in the air is up and there is ground to land on; takes off when its rest is over or
    // somebody it will not sit still for comes near. Never in the middle of a clip.
    private void TickPosture(IMobTarget? target, float deltaTime, ChunkManager chunks)
    {
        _postureTimer -= deltaTime;
        if (Animation.IsActionPlaying) return;

        if (_perched)
        {
            if (target != null ? !StaysPerchedNear(target) : _postureTimer <= 0f) TakeOff();
            return;
        }

        if (target == null && _postureTimer <= 0f && GroundToLandOn(chunks)) Land();
    }

    // Whether solid ground (never water, which the probe also stops at) lies within the landing drop below
    private bool GroundToLandOn(ChunkManager chunks)
    {
        var bottom = Position.Y - BodyHeight * 0.5f;
        if (FlightSteering.GroundBelow(chunks, Position, bottom, WildlifeConfig.FlyerLandDropBlocks) is not { } depth)
            return false;
        var (x, y, z) = new Vector3(Position.X, bottom - depth - 0.5f, Position.Z).BlockCell();
        return BlockType.IsSolid(chunks.GetBlockGlobal(x, y, z));
    }

    private void Land()
    {
        _perched = true;
        _postureTimer = Roll(WildlifeConfig.FlyerPerchMinSeconds, WildlifeConfig.FlyerPerchMaxSeconds);
        StartAttack(LandAction);
        ApplyGait();
    }

    private void TakeOff()
    {
        _perched = false;
        _postureTimer = Roll(WildlifeConfig.FlyerAirMinSeconds, WildlifeConfig.FlyerAirMaxSeconds);
        StartAttack(TakeoffAction);
        SetVelocity(Velocity with { Y = Flight!.Value.VerticalSpeed });
        ApplyGait();
    }

    private void ApplyGait()
    {
        var (idle, move) = _perched ? GroundClips : AirClips(_chasing);
        Animation.SetGait(idle, move, Flight!.Value.CruiseSpeed);
    }
}
