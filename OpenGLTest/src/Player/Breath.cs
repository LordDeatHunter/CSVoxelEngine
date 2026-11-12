using System;

namespace VoxelEngine.Player;

/// <summary>
/// The player's air while the eye is under water, GL-free: it drains over
/// <see cref="PlayerConfig.AirSeconds"/>, refills quickly once the head is out, and once empty it
/// ticks a drowning blow every <see cref="PlayerConfig.DrownTickSeconds"/>. The owner bills the
/// ticks to its pool (<see cref="PlayerHealth.UpdateBreath"/>). Creative and flight never drain.
/// The lung holds <see cref="PlayerConfig.AirSeconds"/> by default; worn gear can widen it
/// (<see cref="SetMaxAir"/> from <see cref="PlayerHealth.ApplyGear"/>, the extra_air ability). The wire step stays a
/// share of the receiver's own lung.
/// </summary>
public sealed class Breath
{
    /// <summary>Whole steps a share of air is quantized to on the wire (and drawn at).</summary>
    public const int NetSteps = 40;

    private float _drownTimer;

    /// <summary>Seconds of air a full lung holds: <see cref="PlayerConfig.AirSeconds"/>, or more while worn gear widens it.</summary>
    public float MaxAir { get; private set; } = PlayerConfig.AirSeconds;

    /// <summary>Seconds of air left, 0..<see cref="MaxAir"/>.</summary>
    public float Air { get; private set; } = PlayerConfig.AirSeconds;

    /// <summary>The air as a share of a full lung, 0..1.</summary>
    public float Fraction => Air / MaxAir;

    /// <summary>
    /// Sets how many seconds a full lung holds, never below <see cref="PlayerConfig.AirSeconds"/>. A full lung stays
    /// full, otherwise the seconds left are kept (clamped to a smaller lung), so putting the gear on under water never
    /// adds air. Cheap to call every frame: an unchanged capacity returns at once.
    /// </summary>
    public void SetMaxAir(float seconds)
    {
        var max = Math.Max(PlayerConfig.AirSeconds, seconds);
        if (max == MaxAir)
            return;

        var wasFull = Air >= MaxAir;
        Air = wasFull ? max : Math.Min(Air, max);
        MaxAir = max;
    }

    /// <summary>
    /// Advances the air by <paramref name="deltaTime"/> and returns how many drowning ticks fell
    /// due. Air drains only while <paramref name="eyeUnderWater"/> and not <paramref name="immune"/>;
    /// otherwise it refills and the partial tick is forgotten, so surfacing never leaves a hit pending.
    /// </summary>
    public int Step(float deltaTime, bool eyeUnderWater, bool immune)
    {
        var dt = Math.Max(0f, deltaTime);
        if (!eyeUnderWater || immune)
        {
            Air = Math.Min(MaxAir, Air + dt * PlayerConfig.AirRefillPerSecond);
            _drownTimer = 0f;
            return 0;
        }

        Air = Math.Max(0f, Air - dt);
        if (Air > 0f)
        {
            _drownTimer = 0f;
            return 0;
        }

        _drownTimer += dt;
        var ticks = 0;
        while (_drownTimer >= PlayerConfig.DrownTickSeconds)
        {
            _drownTimer -= PlayerConfig.DrownTickSeconds;
            ticks++;
        }
        return ticks;
    }

    /// <summary>The air in whole <see cref="NetSteps"/>, rounded up so a sliver of air still reads as some.</summary>
    public byte NetStep => (byte)Math.Clamp((int)MathF.Ceiling(Fraction * NetSteps - 1e-4f), 0, NetSteps);

    /// <summary>Sets the air from a wire step (a client mirroring its server) and forgets any partial tick.</summary>
    public void SetNetStep(int step)
    {
        Air = Math.Clamp(step, 0, NetSteps) / (float)NetSteps * MaxAir;
        _drownTimer = 0f;
    }

    /// <summary>A full lung again (a respawn, a restore).</summary>
    public void Refill()
    {
        Air = MaxAir;
        _drownTimer = 0f;
    }
}
