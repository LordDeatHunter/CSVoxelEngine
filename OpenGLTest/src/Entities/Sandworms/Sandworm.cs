using System;
using System.Numerics;

namespace VoxelEngine.Entities.Sandworms;

/// <summary>
/// The desert boss: one <see cref="Mob"/> whose position is its head and whose body is a
/// <see cref="SandwormChain"/> of rigid segments sharing the one health pool, steered by a
/// <see cref="SandwormBrain"/>. It burrows virtually (no block is ever edited): a segment under the sand surface
/// is simply not exposed, so it can neither be hit nor hit anyone, except that a breaching head bites from under
/// its target. The body hit tests (<see cref="OverlapsBox"/>, <see cref="AimPointFrom"/>) and the damage
/// split (head full, body <see cref="SandwormConfig.BodyDamageShare"/>) read the segment boxes. Main thread only.
/// </summary>
public sealed class Sandworm : Mob
{
    /// <summary>The entity file name.</summary>
    public const string EntityName = "sandworm";

    private readonly bool[] _exposed = new bool[SandwormConfig.SegmentCount];
    private readonly float[] _ground = new float[SandwormConfig.SegmentCount];
    private float _contactCooldown;

    /// <param name="type">The sandworm entity type.</param>
    /// <param name="head">The head's starting center (under the sand for a fresh worm).</param>
    /// <param name="heading">Initial yaw in radians.</param>
    /// <param name="roll">The 0..1 source of the dive-or-slide pick (a test seam; random by default).</param>
    public Sandworm(EntityType type, Vector3 head, float heading = 0f, Func<float>? roll = null) : base(type, head)
    {
        Facing = heading;
        Brain = new SandwormBrain(heading, roll ?? Random.Shared.NextSingle);
        Chain = new SandwormChain(head, new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading)));
    }

    /// <summary>The chain of segments (segment 0 is the head, at <see cref="Mob.Position"/>).</summary>
    public SandwormChain Chain { get; }

    /// <summary>The brain driving the head.</summary>
    public SandwormBrain Brain { get; }

    /// <summary>The current phase.</summary>
    public SandwormPhase Phase => Brain.Phase;

    /// <inheritdoc />
    public override float BodyHalfExtent => SandwormConfig.HeadRadiusBlocks;

    /// <inheritdoc />
    public override float BodyHalfHeight => SandwormConfig.HeadRadiusBlocks;

    /// <inheritdoc />
    public override float HeadAboveCenter => SandwormConfig.HeadRadiusBlocks + 1f;

    /// <inheritdoc />
    public override float DeathSeconds => SandwormConfig.DeathSeconds;

    /// <inheritdoc />
    public override Vector3 Velocity => Brain.Velocity;

    /// <inheritdoc />
    protected override void SetVelocity(Vector3 velocity)
    {
        // The brain owns the head's motion; a restore only puts back health and facing
    }

    /// <summary>A boss is never knocked about by a blow.</summary>
    public override void Shove(Vector3 direction, float speed)
    {
    }

    /// <summary>The half extent of segment <paramref name="index"/>'s hit box.</summary>
    public static float SegmentHalfExtent(int index) => SandwormConfig.HeadRadiusBlocks * SandwormChain.ScaleOf(index);

    /// <summary>Whether segment <paramref name="index"/> stood above the sand last update, and so can be hit and drawn.</summary>
    public bool IsExposed(int index) => _exposed[index];

    /// <summary>True when the contact cooldown has run out, so the next touch may land.</summary>
    public bool CanHit => _contactCooldown <= 0f;

    /// <summary>Starts the cooldown after a hit landed.</summary>
    public void NoteHit() => _contactCooldown = SandwormConfig.ContactCooldownSeconds;

    /// <summary>
    /// Advances the worm: ticks its life clocks, lets the brain steer the head toward
    /// <paramref name="target"/> (null for none) over the sand <paramref name="groundAt"/> describes (surface
    /// height and whether it is sand under a world x and z), moves the head, drags the chain after it and
    /// refreshes which segments are exposed. A dying worm only plays its death.
    /// </summary>
    public void Update(float deltaTime, Vector3? target, Func<float, float, (float SurfaceY, bool OnSand)> groundAt)
    {
        AnimateMirror(deltaTime);
        _contactCooldown = MathF.Max(0f, _contactCooldown - deltaTime);
        if (!IsDying)
        {
            var (surface, onSand) = groundAt(Position.X, Position.Z);
            Brain.Update(deltaTime, new SandwormSenses(Position, target, surface, onSand));
            Position += Brain.Velocity * deltaTime;
            Facing = Brain.Heading;
            Chain.Follow(Position);
        }

        RefreshExposure(groundAt);
    }

    /// <summary>
    /// Whether the worm's current contact touches the body of a player whose eye is at <paramref name="eye"/>:
    /// the head (even still under the sand, since it bites from below) while it chomps, any exposed segment while
    /// it slides. <paramref name="from"/> is the touching segment's center.
    /// </summary>
    public bool Touches(Vector3 eye, out Vector3 from)
    {
        from = Position;
        switch (Brain.Contact)
        {
            case SandwormContact.Chomp:
                return MobTargets.BodyTouches(eye, Position, SegmentHalfExtent(0));
            case SandwormContact.Slide:
                for (var i = 0; i < Chain.Count; i++)
                {
                    if (!_exposed[i] || !MobTargets.BodyTouches(eye, Chain[i], SegmentHalfExtent(i))) continue;
                    from = Chain[i];
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    /// <inheritdoc />
    public override bool OverlapsBox(Vector3 center, float halfExtent)
    {
        for (var i = 0; i < Chain.Count; i++)
        {
            if (!_exposed[i]) continue;
            var d = center - Chain[i];
            var reach = halfExtent + SegmentHalfExtent(i) + 0.05f;
            if (MathF.Abs(d.X) <= reach && MathF.Abs(d.Y) <= reach && MathF.Abs(d.Z) <= reach) return true;
        }
        return false;
    }

    /// <inheritdoc />
    public override Vector3? AimPointFrom(Vector3 from)
    {
        var index = NearestExposed(from);
        if (index < 0) return null;
        // A segment half under the sand is aimed at over its top, so the line of sight clears the sand
        var center = Chain[index];
        var lift = MathF.Max(center.Y + SegmentHalfExtent(index) * 0.4f, _ground[index] + 0.3f);
        return new Vector3(center.X, lift, center.Z);
    }

    /// <inheritdoc />
    public override int ScaleDamage(int amount, Vector3 from)
    {
        var index = NearestExposed(from);
        return index <= 0 ? amount : Math.Max(1, (int)MathF.Round(amount * SandwormConfig.BodyDamageShare));
    }

    private int NearestExposed(Vector3 from)
    {
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < Chain.Count; i++)
        {
            if (!_exposed[i]) continue;
            var distance = Vector3.DistanceSquared(from, Chain[i]);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    private void RefreshExposure(Func<float, float, (float SurfaceY, bool OnSand)> groundAt)
    {
        for (var i = 0; i < Chain.Count; i++)
        {
            var center = Chain[i];
            var surface = groundAt(center.X, center.Z).SurfaceY;
            _ground[i] = surface;
            // At least a quarter of the segment above the sand
            _exposed[i] = center.Y >= surface - SegmentHalfExtent(i) * 0.5f;
        }
    }
}
