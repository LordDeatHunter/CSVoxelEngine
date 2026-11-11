using System;
using System.Collections.Generic;
using System.Numerics;

namespace VoxelEngine.Particles;

/// <summary>
/// This frame's ripples as the water shader takes them, GL-free: two vec4 per ripple
/// (<c>uRippleShape</c> = center x, surface y, center z, leading edge radius; <c>uRippleWave</c> =
/// slope, wavelength, seed, phase), at most <see cref="ParticleConfig.RippleShaded"/> of them. With
/// more alive, the ones nearest the eye are kept, where a missing ring would show.
/// <see cref="Pack"/> runs once a frame before the world programs' frame uniforms are uploaded.
/// </summary>
public sealed class RippleField
{
    private readonly Vector4[] _shapes = new Vector4[ParticleConfig.RippleShaded];
    private readonly Vector4[] _waves = new Vector4[ParticleConfig.RippleShaded];
    private readonly float[] _distances = new float[ParticleConfig.RippleShaded];

    /// <summary>Ripples packed this frame.</summary>
    public int Count { get; private set; }

    public ReadOnlySpan<Vector4> Shapes => _shapes.AsSpan(0, Count);

    public ReadOnlySpan<Vector4> Waves => _waves.AsSpan(0, Count);

    /// <summary>Packs <paramref name="ripples"/> for a view from <paramref name="eye"/>, nearest first.</summary>
    public void Pack(IReadOnlyList<WaterRipple> ripples, Vector3 eye)
    {
        Count = 0;
        foreach (var ripple in ripples)
        {
            var distance = Vector3.DistanceSquared(ripple.Position, eye);
            var slot = SlotFor(distance);
            if (slot < 0) continue;

            if (Count < _shapes.Length) Count++;
            for (var i = Count - 1; i > slot; i--)
            {
                _shapes[i] = _shapes[i - 1];
                _waves[i] = _waves[i - 1];
                _distances[i] = _distances[i - 1];
            }

            _shapes[slot] = new Vector4(ripple.Position, ripple.Radius);
            _waves[slot] = new Vector4(ripple.Slope, ripple.Wavelength, ripple.Seed, ripple.Phase);
            _distances[slot] = distance;
        }
    }

    // Where a ripple this far from the eye goes in the nearest-first list; -1 when the list is full of
    // nearer ones.
    private int SlotFor(float distance)
    {
        var slot = Count;
        while (slot > 0 && _distances[slot - 1] > distance) slot--;
        return slot < _shapes.Length ? slot : -1;
    }
}
