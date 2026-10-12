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
    /// <summary>Downward acceleration of every thrown projectile; the goblin aim solver uses
    /// the same value.</summary>
    public const float Gravity = 28f;
    private const float TerminalVelocity = -30f;
    private const float CollisionStepSize = 0.05f;

    public byte ItemId { get; }
    public Vector3 Position { get; private set; }
    /// <summary>Where the projectile was launched from: a mob it hits is shoved away from here.</summary>
    public Vector3 Origin { get; }
    public float BaseDamage { get; }
    public float Charge01 { get; }
    private Vector3 _velocity;
    private float _age;

    /// <summary>The projectile's water-splash memory, stepped by the world from its centre each frame
    /// (<see cref="WaterSplash.Track"/>).</summary>
    internal WaterSplashTracker Splash { get; } = new();

    public float Spin => _age * 9f;
    public float Scale => ItemDrop.Size * (ItemRegistry.IsItem(ItemId) ? 1.5f : 1f);
    public bool IsExpired => _age >= WorldGenerationConfig.ThrowLifetimeSeconds;

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
        MathUtils.Lerp(WorldGenerationConfig.ThrowMinCharge, 1f, MathUtils.Clamp01(charge01));

    public static int DamageFor(float baseDamage, float charge01) =>
        (int)MathF.Round(baseDamage * ChargeFactor(charge01), MidpointRounding.AwayFromZero);

    public int Damage => DamageFor(BaseDamage, Charge01);

    public static Vector3 LaunchVelocity(Vector3 direction, float charge01)
    {
        if (direction.LengthSquared() < 1e-8f) direction = Vector3.UnitX;
        direction = Vector3.Normalize(direction);
        return direction * (WorldGenerationConfig.ThrowSpeed * ChargeFactor(charge01));
    }

    public bool Update(float deltaTime, ChunkManager chunks, IReadOnlyList<Mob>? mobs, out Mob? hit,
        Mob? ignore = null)
    {
        hit = null;
        _age += Math.Max(0f, deltaTime);
        if (IsExpired) return false;

        _velocity.Y = MathF.Max(_velocity.Y - Gravity * deltaTime, TerminalVelocity);
        var movement = _velocity * deltaTime;
        var next = MoveOnAxis(Position, movement.X, 0, chunks, out var hitX);
        next = MoveOnAxis(next, movement.Z, 2, chunks, out var hitZ);
        next = MoveOnAxis(next, movement.Y, 1, chunks, out var hitY);
        Position = next;
        if (hitX || hitY || hitZ) return false;

        if (mobs != null)
        {
            // Box overlap against the mob's own body, so a tall mob is hittable through its
            // head and shins rather than only near its centre.
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

    private static Vector3 MoveOnAxis(Vector3 start, float delta, int axis, ChunkManager chunks, out bool collided)
    {
        collided = false;
        if (MathF.Abs(delta) < float.Epsilon) return start;
        var position = start;
        var remaining = delta;
        while (MathF.Abs(remaining) > float.Epsilon)
        {
            var step = MathUtils.ClampStep(remaining, CollisionStepSize);
            var candidate = OffsetAxis(position, axis, step);
            if (IsColliding(candidate, chunks))
            {
                collided = true;
                break;
            }
            position = candidate;
            remaining -= step;
        }
        return position;
    }

    private static Vector3 OffsetAxis(Vector3 value, int axis, float delta) => axis switch
    {
        0 => new Vector3(value.X + delta, value.Y, value.Z),
        1 => new Vector3(value.X, value.Y + delta, value.Z),
        _ => new Vector3(value.X, value.Y, value.Z + delta),
    };

    private static bool IsColliding(Vector3 center, ChunkManager chunks)
    {
        VoxelCoords.CellRange(center, HalfExtent, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            if (BlockType.IsSolid(chunks.GetBlockGlobal(x, y, z))) return true;
        }
        return false;
    }
}
