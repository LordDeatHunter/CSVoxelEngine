using System;
using System.Numerics;
using VoxelEngine.Rendering;

namespace VoxelEngine.Entities;

/// <summary>
/// The floating damage numbers over struck mobs, in one fixed array: a hit claims a free slot, or
/// replaces the oldest number when every slot is taken, so nothing is allocated per hit and the
/// count never exceeds <see cref="Capacity"/>. Each number rises, drifts a little sideways and
/// fades out over <see cref="WorldGenerationConfig.DamageNumberLifetimeSeconds"/> (the curves are
/// <see cref="MathUtils"/>'s). GL-free; <see cref="World"/> owns one, feeds it the hits it sees and
/// <see cref="DamageNumberRenderer"/> draws it. Main thread only.
/// </summary>
public sealed class DamageNumberPool
{
    // Successive numbers drift a golden angle apart, so a quick combo fans out instead of stacking
    private const float GoldenAngle = 2.39996323f;

    private readonly DamageNumber[] _items;
    private int _serial;

    public DamageNumberPool(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "A damage number pool needs at least one slot.");
        _items = new DamageNumber[capacity];
    }

    public int Capacity => _items.Length;

    /// <summary>How many numbers are showing.</summary>
    public int Count { get; private set; }

    /// <summary>Every slot, active or not; skip the inactive ones.</summary>
    public ReadOnlySpan<DamageNumber> Items => _items;

    /// <summary>
    /// Shows <paramref name="amount"/> rising from <paramref name="origin"/>. A non-positive amount
    /// shows nothing. A full pool replaces its oldest number.
    /// </summary>
    public void Spawn(Vector3 origin, int amount)
    {
        if (amount <= 0) return;

        var slot = -1;
        var oldest = 0;
        for (var i = 0; i < _items.Length; i++)
        {
            if (!_items[i].Active)
            {
                slot = i;
                break;
            }
            if (_items[i].Age > _items[oldest].Age)
                oldest = i;
        }
        if (slot < 0)
            slot = oldest;
        else
            Count++;

        var angle = _serial++ * GoldenAngle;
        _items[slot] = new DamageNumber
        {
            Origin = origin,
            Amount = amount,
            Age = 0f,
            Drift = new Vector2(MathF.Cos(angle), MathF.Sin(angle)),
            Active = true,
        };
    }

    /// <summary>Ages every number and frees the ones whose lifetime is over.</summary>
    public void Update(float deltaTime)
    {
        var step = MathF.Max(0f, deltaTime);
        for (var i = 0; i < _items.Length; i++)
        {
            ref var item = ref _items[i];
            if (!item.Active) continue;
            item.Age += step;
            if (item.Age < WorldGenerationConfig.DamageNumberLifetimeSeconds) continue;
            item.Active = false;
            Count--;
        }
    }

    /// <summary>Where a number is now: risen and drifted along eased curves from its origin.</summary>
    public static Vector3 PositionOf(in DamageNumber number)
    {
        var t = MathUtils.EaseOutCubic(number.Age / WorldGenerationConfig.DamageNumberLifetimeSeconds);
        var drift = WorldGenerationConfig.DamageNumberDriftBlocks * t;
        return number.Origin + new Vector3(number.Drift.X * drift,
            WorldGenerationConfig.DamageNumberRiseBlocks * t, number.Drift.Y * drift);
    }

    /// <summary>A number's opacity: fully on for the hold, then fading to 0 by the end of its life.</summary>
    public static float AlphaOf(in DamageNumber number) =>
        MathUtils.HoldThenFade(number.Age, WorldGenerationConfig.DamageNumberHoldSeconds,
            WorldGenerationConfig.DamageNumberLifetimeSeconds - WorldGenerationConfig.DamageNumberHoldSeconds);

    /// <summary>A number's size multiplier: it pops in larger and springs back to 1.</summary>
    public static float ScaleOf(in DamageNumber number) =>
        MathUtils.LerpUnclamped(WorldGenerationConfig.DamageNumberPopScale, 1f,
            MathUtils.EaseOutBack(number.Age / WorldGenerationConfig.DamageNumberPopSeconds));
}
