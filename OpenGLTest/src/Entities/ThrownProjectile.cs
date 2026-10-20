using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Items;
using VoxelEngine.Particles;

namespace VoxelEngine.Entities;

public sealed class ThrownProjectile
{
    public const float HalfExtent = 0.15f;
    /// <summary>Downward acceleration of every thrown projectile: the shared loose-body gravity
    /// (<see cref="BodyMotion.Gravity"/>), which the goblin aim solver uses too.</summary>
    public const float Gravity = BodyMotion.Gravity;

    public byte ItemId { get; }
    public Vector3 Position { get; private set; }
    /// <summary>Where the projectile was launched from: a mob it hits is shoved away from here.</summary>
    public Vector3 Origin { get; }
    public float BaseDamage { get; }
    public float Charge01 { get; }
    private Vector3 _velocity;
    private float _age;

    /// <summary>The projectile's water-splash memory, stepped by the world from its center each frame
    /// (<see cref="WaterSplash.Track"/>).</summary>
    internal WaterSplashTracker Splash { get; } = new();

    public float Spin => _age * 9f;
    public float Scale => ItemDrop.Size * (ItemRegistry.IsItem(ItemId) ? 1.5f : 1f);
    public bool IsExpired => _age >= PlayerConfig.ThrowLifetimeSeconds;

    public ThrownProjectile(byte itemId, Vector3 position, Vector3 velocity, float baseDamage, float charge01)
    {
        ItemId = itemId;
        Position = position;
        Origin = position;
        _velocity = velocity;
        BaseDamage = baseDamage;
        Charge01 = MathUtils.Clamp01(charge01);
    }

    public static float ChargeFactor(float charge01) =>
        MathUtils.Lerp(PlayerConfig.ThrowMinCharge, 1f, MathUtils.Clamp01(charge01));

    public static int DamageFor(float baseDamage, float charge01) =>
        (int)MathF.Round(baseDamage * ChargeFactor(charge01), MidpointRounding.AwayFromZero);

    public int Damage => DamageFor(BaseDamage, Charge01);

    public static Vector3 LaunchVelocity(Vector3 direction, float charge01)
    {
        if (direction.LengthSquared() < 1e-8f) direction = Vector3.UnitX;
        direction = Vector3.Normalize(direction);
        return direction * (PlayerConfig.ThrowSpeed * ChargeFactor(charge01));
    }

    public bool Update(float deltaTime, ChunkManager chunks, IReadOnlyList<Mob>? mobs, out Mob? hit,
        Mob? ignore = null)
    {
        hit = null;
        _age += Math.Max(0f, deltaTime);
        if (IsExpired) return false;

        _velocity.Y = BodyMotion.Fall(_velocity.Y, deltaTime);
        var movement = _velocity * deltaTime;
        var half = new Vector3(HalfExtent);
        var next = BoxSweep.MoveOnAxis(Position, movement.X, 0, half, chunks, out var hitX);
        next = BoxSweep.MoveOnAxis(next, movement.Z, 2, half, chunks, out var hitZ);
        next = BoxSweep.MoveOnAxis(next, movement.Y, 1, half, chunks, out var hitY);
        Position = next;
        if (hitX || hitY || hitZ) return false;

        if (mobs != null)
        {
            // Box overlap against the mob's own body, so a tall mob is hittable through its
            // head and shins rather than only near its center.
            for (var i = 0; i < mobs.Count; i++)
            {
                var mob = mobs[i];
                if (mob.IsDead || mob == ignore) continue;
                var d = Position - mob.Position;
                var reachXz = HalfExtent + mob.BodyHalfExtent + 0.05f;
                var reachY = HalfExtent + mob.BodyHalfHeight + 0.05f;
                if (MathF.Abs(d.X) <= reachXz && MathF.Abs(d.Z) <= reachXz && MathF.Abs(d.Y) <= reachY)
                {
                    hit = mob;
                    return false;
                }
            }
        }
        return true;
    }
}
