using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Particles;

/// <summary>
/// The live water ripple rings (<see cref="WaterRipple"/>): <see cref="WaterSplash.Track"/> spawns one
/// where a body crosses a water surface, <see cref="Update"/> ages them out, and
/// <see cref="RippleRenderer"/> draws them. At the cap the oldest ring makes room. Cosmetic: never
/// on a headless server. Main thread only.
/// </summary>
public sealed class RippleSystem
{
    private readonly ParticleRing<WaterRipple> _ripples = new(ParticleConfig.RippleMax);

    public IReadOnlyList<WaterRipple> Ripples => _ripples;

    /// <summary>Starts a ring at <paramref name="waterTop"/> (the column's surface point) for a crossing
    /// of <paramref name="strength"/> 0..1; <paramref name="sizeScale"/> widens it for a big body. With
    /// <paramref name="chunks"/> the ring is confined to the water cells round the crossing
    /// (<see cref="RippleSurface"/>); without, it spreads unclipped.</summary>
    public void Spawn(Vector3 waterTop, float strength, float sizeScale = 1f, ChunkManager? chunks = null)
    {
        if (_ripples.Count >= ParticleConfig.RippleMax)
            _ripples.RemoveAt(0);
        var position = waterTop + new Vector3(0f, ParticleConfig.RippleLift, 0f);
        var ripple = new WaterRipple(position, MathUtils.Clamp01(strength), sizeScale,
            Random.Shared.NextSingle());
        if (chunks != null)
            ripple.ConfineTo(RippleSurface.Gather(chunks, waterTop, waterTop.Y, ripple.Half));
        _ripples.Add(ripple);
    }

    public void Update(float deltaTime)
    {
        for (var i = _ripples.Count - 1; i >= 0; i--)
            if (!_ripples[i].Update(deltaTime))
                _ripples.RemoveAt(i);
    }
}
