using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// An animal of the sea (<see cref="Fish"/>, <see cref="Shark"/>, <see cref="Jellyfish"/>): a flyer whose medium is
/// water (<see cref="Mob.Swims"/>). In water it steers like any flyer (<see cref="SwimToward"/>, through
/// <see cref="RiggedMob.Fly"/>), kept under the surface and off the shore; with no fluid in the cell of its body it
/// is beached: a walker falling under gravity that plays its <see cref="BeachedClip"/> (a fish flops, hopping about)
/// and loses <see cref="WildlifeConfig.BeachedDamage"/> every <see cref="WildlifeConfig.BeachedDamageIntervalSeconds"/>
/// until it is in water again. A dying swimmer is a walker too, so it floats belly-up where it died. Since a client
/// cannot tell a darting body from a cruising one, nor a beached one from a hovering one, both ride the animal's
/// <see cref="Posture"/> to its mirror. Main thread only.
/// </summary>
public abstract class AquaticAnimal : Animal
{
    private const byte DartingBit = 1;
    private const byte BeachedBit = 2;

    private bool _darting;
    private bool _beached;
    private float _flopTimer;
    private float _beachedTimer;

    protected AquaticAnimal(EntityType type, Vector3 position, FlightTuning swim, bool spawnIn)
        : base(type, position, swim.CruiseSpeed, spawnIn, swim)
    {
    }

    /// <inheritdoc />
    public override bool Swims => true;

    /// <inheritdoc />
    protected override string MoveClip => EntityAnimator.SwimClip;

    // A swimmer's walk slots only fill the walker's contract: it only ever walks when it lies beached
    /// <inheritdoc />
    protected override float WalkSpeed => Flight!.Value.CruiseSpeed;

    /// <inheritdoc />
    protected override float JumpSpeed => 0f;

    /// <summary>Whether it lies out of water.</summary>
    public bool IsBeached => _beached;

    /// <summary>The loop it plays beached (a fish's <c>flop</c>), or null for the idle clip.</summary>
    protected virtual string? BeachedClip => null;

    /// <summary>The loop it plays in place of <c>swim</c> while <see cref="Darting"/> (a fish's <c>dart</c>), or
    /// null for none.</summary>
    protected virtual string? DartClip => null;

    /// <summary>Whether it is in its dart gait (fleeing or charging): the fast locomotion clip, authored at
    /// <see cref="DartSpeed"/>. Changing it re-picks the gait.</summary>
    protected bool Darting
    {
        get => _darting;
        set
        {
            if (_darting == value) return;
            _darting = value;
            ApplyGait();
        }
    }

    /// <summary>The speed <see cref="DartClip"/> is authored at.</summary>
    protected virtual float DartSpeed => Flight!.Value.CruiseSpeed;

    /// <summary>One frame of swimming in water toward <paramref name="target"/> (the one it heeds) or
    /// <paramref name="wander"/> (null: it holds where it is): the kind calls <see cref="Swim"/> with its goal.</summary>
    protected abstract void SwimToward(IMobTarget? target, Vector3? wander, float deltaTime, ChunkManager chunks);

    /// <summary>Flies one frame through water toward <paramref name="goal"/> at <paramref name="speedFactor"/> of
    /// its cruise (a status effect's slowing applied).</summary>
    protected void Swim(Vector3? goal, bool chasing, float speedFactor, float deltaTime, ChunkManager chunks) =>
        Fly(goal, chasing, speedFactor * MoveFactor, deltaTime, chunks);

    /// <inheritdoc />
    public override byte Posture => (byte)((_darting ? DartingBit : 0) | (_beached ? BeachedBit : 0));

    /// <inheritdoc />
    public override void MirrorPosture(byte posture)
    {
        var darting = (posture & DartingBit) != 0;
        var beached = (posture & BeachedBit) != 0;
        if (darting == _darting && beached == _beached) return;
        _darting = darting;
        _beached = beached;
        ApplyGait();
    }

    /// <inheritdoc />
    protected override void Locomote(Vector3 move, IMobTarget? target, Vector3? wander, float deltaTime,
        ChunkManager chunks)
    {
        if (IsDying)
        {
            // Dead in the water it floats (the walker's buoyancy), belly-up as its death clip lies
            Walk(Vector3.Zero, null, 0f, 0f, 0f, deltaTime, chunks);
            return;
        }

        var (x, y, z) = Position.BlockCell();
        SetBeached(!BlockType.IsFluid(chunks.GetBlockGlobal(x, y, z)));
        if (_beached)
        {
            TickBeached(deltaTime);
            Walk(Vector3.Zero, null, 0f, 0f, 0f, deltaTime, chunks);
            return;
        }

        SwimToward(target, wander, deltaTime, chunks);
    }

    private void SetBeached(bool beached)
    {
        if (beached == _beached) return;
        _beached = beached;
        _flopTimer = 0f;
        _beachedTimer = 0f;
        ApplyGait();
    }

    // Out of water: a hop now and then (a fish flopping about), and a little damage as it suffocates
    private void TickBeached(float deltaTime)
    {
        _beachedTimer += deltaTime;
        while (_beachedTimer >= WildlifeConfig.BeachedDamageIntervalSeconds && !IsDead)
        {
            _beachedTimer -= WildlifeConfig.BeachedDamageIntervalSeconds;
            TakeDamage(WildlifeConfig.BeachedDamage);
        }

        if (BeachedClip == null) return;
        _flopTimer -= deltaTime;
        if (_flopTimer > 0f || !IsGrounded) return;
        _flopTimer = Roll(WildlifeConfig.FlopMinSeconds, WildlifeConfig.FlopMaxSeconds);
        var side = WildlifeConfig.FlopSideSpeed;
        SetVelocity(new Vector3(Roll(-side, side), WildlifeConfig.FlopHopSpeed, Roll(-side, side)));
        IsGrounded = false;
    }

    // The locomotion pair follows the state: flopping out of water, else swim (or dart) over idle
    private void ApplyGait()
    {
        var cruise = Flight!.Value.CruiseSpeed;
        if (_beached && BeachedClip is { } flop) Animation.SetGait(flop, flop, cruise);
        else if (_darting && DartClip is { } dart) Animation.SetGait(EntityAnimator.IdleClip, dart, DartSpeed);
        else Animation.SetGait(EntityAnimator.IdleClip, EntityAnimator.SwimClip, cruise);
    }

    /// <summary>The swimming rig a sea animal's model needs: <paramref name="parts"/> and the idle and swim
    /// clips (throws, naming the model file, otherwise).</summary>
    protected static void RequireSwimmer(EntityType type, string kind, string[] parts)
    {
        EntityContract.RequireParts(type, kind, parts);
        EntityContract.RequireClip(type, kind, EntityAnimator.IdleClip);
        EntityContract.RequireClip(type, kind, EntityAnimator.SwimClip);
    }

    /// <summary>The unit 3D direction from <paramref name="from"/> to <paramref name="to"/>, or zero when they
    /// coincide.</summary>
    protected static Vector3 DirectionTo(Vector3 from, Vector3 to)
    {
        var d = to - from;
        var length = d.Length();
        return length > 1e-4f ? d / length : Vector3.Zero;
    }
}
