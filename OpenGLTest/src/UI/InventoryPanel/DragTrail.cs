using System;
using System.Numerics;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// The sparkle trail behind an item dragged across the [E] panel (<see cref="RowDrag"/>). GL-free: each frame
/// <see cref="Advance"/> gets the pointer while a drag is lifted (or null), spawns particles along the path the
/// pointer traveled since the last frame (more for a faster drag, none for a still pointer) and ages the rest, so
/// the trail fades out on its own after the drop. A fixed ring: when full, a new particle replaces the oldest.
/// Main thread only.
/// </summary>
public sealed class DragTrail
{
    /// <summary>One sparkle: canvas position, drift per second, age and lifetime in seconds.</summary>
    public struct Particle
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public float Age;
        public float Life;
    }

    private readonly Particle[] _particles = new Particle[HudConfig.DragTrailMax];
    private Vector2? _lastPointer;
    private float _time = float.NaN;
    private float _spawnDebt;
    private uint _seed = 0x9E3779B9u;

    /// <summary>How many particles are alive.</summary>
    public int Count { get; private set; }

    /// <summary>The <paramref name="index"/>th live particle.</summary>
    public Particle At(int index) => _particles[index];

    /// <summary>Fraction of its life a particle has left, 1 when spawned and 0 when it dies.</summary>
    public static float Remaining(Particle particle) => 1f - MathUtils.Clamp01(particle.Age / particle.Life);

    /// <summary>
    /// Steps by the time since the last call, emitting along the pointer's path when <paramref name="pointer"/> is
    /// not null (a drag is lifted).
    /// </summary>
    public void Advance(float timeSeconds, Vector2? pointer)
    {
        var dt = float.IsNaN(_time) ? 0f : Math.Clamp(timeSeconds - _time, 0f, 0.1f);
        _time = timeSeconds;
        Age(dt);
        if (pointer is not { } now)
        {
            _lastPointer = null;
            _spawnDebt = 0f;
            return;
        }
        var from = _lastPointer ?? now;
        _lastPointer = now;
        _spawnDebt += Vector2.Distance(from, now) * HudConfig.DragTrailSpawnPerPixel;
        var spawn = (int)_spawnDebt;
        _spawnDebt -= spawn;
        for (var i = 0; i < spawn; i++)
            Spawn(Vector2.Lerp(from, now, (i + 1f) / spawn));
    }

    /// <summary>Empties the trail (the panel closed).</summary>
    public void Clear()
    {
        Count = 0;
        _lastPointer = null;
        _spawnDebt = 0f;
    }

    private void Age(float dt)
    {
        for (var i = Count - 1; i >= 0; i--)
        {
            ref var p = ref _particles[i];
            p.Age += dt;
            p.Position += p.Velocity * dt;
            if (p.Age < p.Life) continue;
            _particles[i] = _particles[--Count];
        }
    }

    private void Spawn(Vector2 at)
    {
        var index = Count;
        if (Count == _particles.Length)
        {
            index = 0;
            for (var i = 1; i < Count; i++)
                if (_particles[i].Age > _particles[index].Age) index = i;
        }
        else
            Count++;
        var spread = HudConfig.DragTrailSpreadPixels;
        _particles[index] = new Particle
        {
            Position = at + new Vector2(Next() - 0.5f, Next() - 0.5f) * spread,
            Velocity = new Vector2((Next() - 0.5f) * HudConfig.DragTrailDriftPixelsPerSecond,
                HudConfig.DragTrailFallPixelsPerSecond * (0.5f + Next())),
            Life = HudConfig.DragTrailLifeSeconds * (0.6f + 0.4f * Next()),
        };
    }

    /// <summary>A cheap xorshift in [0, 1): the look needs no better, and it keeps the trail deterministic.</summary>
    private float Next()
    {
        _seed ^= _seed << 13;
        _seed ^= _seed >> 17;
        _seed ^= _seed << 5;
        return (_seed & 0xFFFFFF) / (float)0x1000000;
    }
}
