using System;

namespace VoxelEngine.Core;

/// <summary>
/// A damped spring that carries its own velocity, for jiggle and overshoot (slime squash, hit
/// kicks, camera dips, viewmodel sway). Integrated in closed form per sub-step, so it is stable
/// at any frame time and two half-steps agree with one full step to within the sub-step error.
/// <see cref="Frequency"/> is the undamped rate in Hz and <see cref="DampingRatio"/> is 1 for
/// critical damping (no overshoot), below 1 for ringing.
/// </summary>
public struct Spring
{
    private const float MaxSubStep = 1f / 120f;

    public float Value;
    public float Velocity;
    public float Frequency;
    public float DampingRatio;

    public Spring(float frequency, float dampingRatio, float value = 0f)
    {
        Frequency = frequency;
        DampingRatio = dampingRatio;
        Value = value;
        Velocity = 0f;
    }

    /// <summary>Kicks the spring: adds an instantaneous velocity (an impact).</summary>
    public void Impulse(float velocity) => Velocity += velocity;

    /// <summary>Jumps to a value and rest.</summary>
    public void Reset(float value = 0f)
    {
        Value = value;
        Velocity = 0f;
    }

    /// <summary>Advances toward <paramref name="target"/> and returns the new value.</summary>
    public float Step(float target, float deltaTime)
    {
        var remaining = MathF.Max(0f, deltaTime);
        var omega = MathF.Tau * Frequency;
        while (remaining > 0f)
        {
            var h = MathF.Min(remaining, MaxSubStep);
            remaining -= h;
            // Semi-implicit Euler on x'' = -w^2 (x - target) - 2 z w x'.
            var accel = -omega * omega * (Value - target) - 2f * DampingRatio * omega * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
        }
        return Value;
    }

    /// <summary>True once value and velocity are both within <paramref name="epsilon"/> of rest at the target.</summary>
    public readonly bool IsSettled(float target, float epsilon = 1e-3f) =>
        MathF.Abs(Value - target) < epsilon && MathF.Abs(Velocity) < epsilon;
}
