using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Items;
using VoxelEngine.Particles;
using VoxelEngine.Player;

namespace VoxelEngine.Entities;

public sealed class ThrownProjectile
{
    public const float HalfExtent = 0.15f;
    /// <summary>Downward acceleration of every thrown projectile: the shared loose-body gravity
    /// (<see cref="BodyMotion.Gravity"/>), which the goblin aim solver uses too.</summary>
    public const float Gravity = BodyMotion.Gravity;

    public ushort ItemId { get; }
    public Vector3 Position { get; private set; }
    /// <summary>Where the projectile was launched from: a mob it hits is shoved away from here.</summary>
    public Vector3 Origin { get; }
    public float BaseDamage { get; }
    public float Charge01 { get; }
    private Vector3 _velocity;
    private readonly float _gravityScale;
    private readonly float _lifeSeconds;
    private float _age;

    /// <summary>The projectile's velocity in blocks per second.</summary>
    public Vector3 Velocity => _velocity;

    /// <summary>Whether it flies point first along its velocity (a javelin) instead of tumbling.</summary>
    public bool Aligned => ItemRegistry.TryGet(ItemId)?.ThrowAligned == true;

    /// <summary>
    /// The rotation that points a sprite's tip, on its (1, 1) diagonal as every blade's is, along
    /// <paramref name="velocity"/>, rolled a quarter-right so it reads from the side and from behind.
    /// Identity-like (a tip toward +X+Y) for a velocity too small to give a heading.
    /// </summary>
    public static Matrix4x4 AlignedRotation(Vector3 velocity)
    {
        if (velocity.LengthSquared() < 1e-8f) return Matrix4x4.Identity;
        var forward = Vector3.Normalize(velocity);
        var side = Vector3.Cross(Vector3.UnitY, forward);
        side = side.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(side);
        var up = Vector3.Cross(forward, side);
        // Rolled 45 degrees about the heading
        const float r = MathF.PI / 4f;
        var rolled = MathF.Cos(r) * up - MathF.Sin(r) * side;
        var normal = Vector3.Cross(forward, rolled);
        // Local x = (tip - perpendicular) / sqrt(2), local y = (tip + perpendicular) / sqrt(2), local z = the normal
        const float h = 0.70710678f;
        var x = (forward - rolled) * h;
        var y = (forward + rolled) * h;
        return new Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            normal.X, normal.Y, normal.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    /// <summary>The projectile's water-splash memory, stepped by the world from its center each frame
    /// (<see cref="WaterSplash.Track"/>).</summary>
    internal WaterSplashTracker Splash { get; } = new();

    /// <summary>The id the projectile is drawn as: the block cube its item names (<see cref="Item.ThrowBlockId"/>), else
    /// the item itself (a sprite) or the block it is.</summary>
    public ushort RenderId => ItemRegistry.TryGet(ItemId) is { ThrowBlockId: not 0 } item ? item.ThrowBlockId : ItemId;

    /// <summary>How deep a stuck item's tip sits in what it hit, in blocks.</summary>
    public const float StuckDepth = 0.2f;

    /// <summary>How far the tip of <paramref name="renderId"/> reaches from its center as a thrown (or stuck) projectile:
    /// a 3D item's own tip, a sprite's corner on its diagonal.</summary>
    public static float TipReach(ushort renderId) =>
        (ItemRegistry.TryGet(renderId)?.HeldModel?.TipHeight ?? 0.70710678f) * ScaleOf(renderId);

    /// <summary>
    /// Where the center of a stuck <paramref name="itemId"/> rests for a flight that stopped at <paramref name="impact"/>
    /// (the projectile's last position, a <see cref="HalfExtent"/> short of what it hit) heading along the unit
    /// <paramref name="heading"/>: back along the shaft, so the tip ends <see cref="StuckDepth"/> inside.
    /// <see cref="StuckImpact"/> is its inverse.
    /// </summary>
    public static Vector3 StuckCenter(ushort itemId, Vector3 impact, Vector3 heading) =>
        impact - heading * StuckBack(itemId);

    /// <summary>The impact point a stuck item's <paramref name="center"/> was placed from (<see cref="StuckCenter"/>).</summary>
    public static Vector3 StuckImpact(ushort itemId, Vector3 center, Vector3 heading) =>
        center + heading * StuckBack(itemId);

    private static float StuckBack(ushort itemId) => MathF.Max(0f, TipReach(itemId) - StuckDepth - HalfExtent);

    /// <summary>Whether the flight ended against terrain (rather than on a mob, or by running out of life in the air).</summary>
    public bool Landed { get; private set; }

    /// <summary>The unit direction it is flying (or was, when it stopped); +X for one with no speed.</summary>
    public Vector3 Heading => _velocity.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(_velocity);

    public float Spin => _age * 9f;
    /// <summary>The drawn size: a 3D item (<see cref="Item.HeldModel"/>) keeps the size it had in the hand, so it
    /// neither shrinks nor grows as it leaves it; a sprite or a block cube flies at its drop's size.</summary>
    public float Scale => ScaleOf(RenderId);

    /// <summary>The size <paramref name="renderId"/> is drawn at as a thrown (or stuck) projectile; see <see cref="Scale"/>.</summary>
    public static float ScaleOf(ushort renderId) => ItemRegistry.TryGet(renderId) switch
    {
        { HeldModel: not null } item => HeldItemPose.HeldItemScale * item.DisplayScale,
        null => ItemDrop.Size,
        _ => ItemDrop.Size * 1.5f,
    };
    public bool IsExpired => _age >= _lifeSeconds;

    /// <summary>A projectile that falls under <paramref name="gravityScale"/> of <see cref="Gravity"/> (bone shrapnel
    /// spreads sideways) and is spent after <paramref name="lifeSeconds"/>, a throw's by default.</summary>
    public ThrownProjectile(ushort itemId, Vector3 position, Vector3 velocity, float baseDamage, float charge01,
        float gravityScale = 1f, float lifeSeconds = PlayerConfig.ThrowLifetimeSeconds)
    {
        ItemId = itemId;
        Position = position;
        Origin = position;
        _velocity = velocity;
        BaseDamage = baseDamage;
        Charge01 = MathUtils.Clamp01(charge01);
        _gravityScale = MathF.Max(0f, gravityScale);
        _lifeSeconds = lifeSeconds;
    }

    public static float ChargeFactor(float charge01) =>
        MathUtils.Lerp(PlayerConfig.ThrowMinCharge, 1f, MathUtils.Clamp01(charge01));

    public static int DamageFor(float baseDamage, float charge01) =>
        (int)MathF.Round(baseDamage * ChargeFactor(charge01), MidpointRounding.AwayFromZero);

    public int Damage => DamageFor(BaseDamage, Charge01);

    public static Vector3 LaunchVelocity(Vector3 direction, float charge01, float speedScale = 1f)
    {
        if (direction.LengthSquared() < 1e-8f) direction = Vector3.UnitX;
        direction = Vector3.Normalize(direction);
        return direction * (PlayerConfig.ThrowSpeed * speedScale * ChargeFactor(charge01));
    }

    public bool Update(float deltaTime, ChunkManager chunks, IReadOnlyList<Mob>? mobs, out Mob? hit,
        Mob? ignore = null)
    {
        hit = null;
        _age += Math.Max(0f, deltaTime);
        if (IsExpired) return false;

        _velocity.Y = BodyMotion.Fall(_velocity.Y, deltaTime * _gravityScale);
        var movement = _velocity * deltaTime;
        var half = new Vector3(HalfExtent);
        var next = BoxSweep.MoveOnAxis(Position, movement.X, 0, half, chunks, out var hitX);
        next = BoxSweep.MoveOnAxis(next, movement.Z, 2, half, chunks, out var hitZ);
        next = BoxSweep.MoveOnAxis(next, movement.Y, 1, half, chunks, out var hitY);
        Position = next;
        if (hitX || hitY || hitZ)
        {
            Landed = true;
            return false;
        }

        hit = FindMobHit(Position, HalfExtent, mobs, ignore);
        return hit == null;
    }

    /// <summary>
    /// The first living mob (other than <paramref name="ignore"/>) a box of <paramref name="halfExtent"/>
    /// centered at <paramref name="position"/> touches, or null. The test is a box overlap against the
    /// mob's own body, so a tall mob is hittable through its head and shins rather than only near its center.
    /// Shared by thrown items and spell bolts.
    /// </summary>
    public static Mob? FindMobHit(Vector3 position, float halfExtent, IReadOnlyList<Mob>? mobs, Mob? ignore = null)
    {
        if (mobs == null) return null;
        for (var i = 0; i < mobs.Count; i++)
        {
            var mob = mobs[i];
            if (mob.IsDead || mob == ignore) continue;
            if (mob.OverlapsBox(position, halfExtent))
                return mob;
        }
        return null;
    }
}
