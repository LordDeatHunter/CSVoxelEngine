using System;
using System.Collections.Generic;
using System.Numerics;

namespace VoxelEngine.Particles;

/// <summary>
/// The live water ripples (<see cref="WaterRipple"/>): <see cref="WaterSplash.Track"/> spawns one
/// where a body crosses a water surface, <see cref="Update"/> ages them out, and
/// <see cref="RippleField"/> hands them to the water shader. At the cap the oldest makes room.
/// Cosmetic: never on a headless server. Main thread only.
/// </summary>
public sealed class RippleSystem
{
    private readonly ParticleRing<WaterRipple> _ripples = new(ParticleConfig.RippleMax);

    public IReadOnlyList<WaterRipple> Ripples => _ripples;

    /// <summary>Starts a ripple at <paramref name="waterTop"/> (the column's surface point) for a crossing
    /// of <paramref name="strength"/> 0..1; <paramref name="sizeScale"/> widens it for a big body.</summary>
    public void Spawn(Vector3 waterTop, float strength, float sizeScale = 1f)
    {
        if (_ripples.Count >= ParticleConfig.RippleMax)
            _ripples.RemoveAt(0);
        _ripples.Add(new WaterRipple(waterTop, MathUtils.Clamp01(strength), sizeScale, Random.Shared.NextSingle()));
    }

    public void Update(float deltaTime)
    {
        for (var i = _ripples.Count - 1; i >= 0; i--)
            if (!_ripples[i].Update(deltaTime))
                _ripples.RemoveAt(i);
    }
}
