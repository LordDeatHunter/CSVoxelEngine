using System;
using System.Numerics;

namespace VoxelEngine.Player;

/// <summary>
/// The damage view shake, split out of <see cref="PlayerPhysics"/>, which owns it and ticks it once a frame inside
/// <see cref="PlayerPhysics.Update"/>. <see cref="Camera"/>'s view matrix and its view-projection cache key read
/// <see cref="Offset"/> and <see cref="Roll"/>.
/// </summary>
internal sealed class ViewShake
{
    // Damage shake: trauma 0..1, fed by Add and decayed in Update. The view offset
    // and roll ride trauma-squared, so small hits tremble and big hits lurch.
    private float _trauma;
    private float _shakeTime;
    public Vector3 Offset { get; private set; } = Vector3.Zero;
    public float Roll { get; private set; }
    private const float ShakeDecaySeconds = 0.7f;
    private const float ShakeMaxOffset = 0.3f;
    private const float ShakeMaxRoll = 0.04f;

    /// <summary>Test seam: sets the damage-shake view offset directly.</summary>
    public void SetOffset(Vector3 offset) => Offset = offset;
    /// <summary>Test seam: sets the damage-shake roll directly.</summary>
    public void SetRoll(float roll) => Roll = roll;

    /// <summary>
    /// Kicks the damage shake: <paramref name="amount"/> adds to trauma (0..1, clamped),
    /// which <see cref="Update"/> decays back to still over <c>ShakeDecaySeconds</c>.
    /// Callers scale it by the hit — World bills fall and lava ticks through here.
    /// </summary>
    public void Add(float amount) => _trauma = Math.Min(1f, _trauma + amount);

    /// <summary>Advances the shake clock and decays trauma by <paramref name="deltaTime"/>, refreshing Offset and Roll.</summary>
    internal void Update(float deltaTime)
    {
        _shakeTime += Math.Max(0f, deltaTime);
        if (_trauma <= 0f)
        {
            Offset = Vector3.Zero;
            Roll = 0f;
            return;
        }

        _trauma = Math.Max(0f, _trauma - Math.Max(0f, deltaTime) / ShakeDecaySeconds);
        // Squared falloff plus mismatched sines per axis, so the view shudders instead of
        // swaying on one clean frequency.
        var t = _trauma * _trauma;
        var time = _shakeTime;
        Offset = new Vector3(
            MathF.Sin(time * 39.7f) * ShakeMaxOffset * t,
            MathF.Sin(time * 44.3f + 1.3f) * ShakeMaxOffset * 0.6f * t,
            MathF.Sin(time * 31.9f + 2.1f) * ShakeMaxOffset * t);
        Roll = MathF.Sin(time * 47.7f + 0.7f) * ShakeMaxRoll * t;
    }
}
