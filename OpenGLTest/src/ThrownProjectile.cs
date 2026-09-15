using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

public sealed class ThrownProjectile
{
    public const float HalfExtent = 0.15f;
    private const float Gravity = 28f;
    private const float TerminalVelocity = -30f;
    private const float CollisionStepSize = 0.05f;

    public byte ItemId { get; }
    public Vector3 Position { get; private set; }
    public float BaseDamage { get; }
    public float Charge01 { get; }
    private Vector3 _velocity;
    private float _age;

    public float Spin => _age * 9f;
    public float Scale => ItemDrop.Size * (ItemRegistry.IsItem(ItemId) ? 1.5f : 1f);
    public bool IsExpired => _age >= WorldGenerationConfig.ThrowLifetimeSeconds;

    public ThrownProjectile(byte itemId, Vector3 position, Vector3 velocity, float baseDamage, float charge01)
    {
        ItemId = itemId;
        Position = position;
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

    public bool Update(float deltaTime, ChunkManager chunks, IReadOnlyList<Slime> slimes, out Slime? hit)
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

        if (slimes != null)
        {
            for (var i = 0; i < slimes.Count; i++)
            {
                var slime = slimes[i];
                if (slime.IsDead) continue;
                var radius = HalfExtent + Slime.HalfExtent + 0.1f;
                if (Vector3.DistanceSquared(Position, slime.Position) <= radius * radius)
                {
                    hit = slime;
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
