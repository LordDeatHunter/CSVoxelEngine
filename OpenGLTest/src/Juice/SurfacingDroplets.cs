using System;
using VoxelEngine.Player;
using VoxelEngine.Rendering;

namespace VoxelEngine.Juice;

/// <summary>
/// GL-free timing of the surfacing droplets: when the eye comes up out of water, droplets streak
/// down the screen and fade out over <see cref="WorldGenerationConfig.SurfacingDropletSeconds"/>.
/// <see cref="Camera"/> owns one, reports the eye's fluid state once a frame
/// (<see cref="Observe"/>) and ages it (<see cref="Update"/>); <see cref="DamageVignetteRenderer"/>
/// draws the drops as a hashed-disc term of the vignette's one full-screen pass from
/// <see cref="Intensity"/>, <see cref="Seconds"/> and <see cref="Seed"/>, the drop positions, sizes
/// and speeds being hashed in the shader. Surfacing from lava shows none, and the eye going back
/// under clears them at once.
/// </summary>
public sealed class SurfacingDroplets
{
    private bool _wasUnderWater;
    // Seconds since the eye surfaced; infinite while no droplets show
    private float _age = float.PositiveInfinity;

    /// <summary>
    /// Changes on every surfacing (wrapping well inside a float's exact integers), so each one
    /// scatters a fresh pattern of drops rather than the same streaks again.
    /// </summary>
    public int Seed { get; private set; }

    /// <summary>
    /// Reports where the eye is this frame. The frame it leaves water (not lava) for the air the
    /// droplets start at full strength; the frame it is back under any fluid they are gone.
    /// </summary>
    public void Observe(bool eyeInFluid, bool eyeInLava)
    {
        if (eyeInFluid)
            _age = float.PositiveInfinity;
        else if (_wasUnderWater)
        {
            _age = 0f;
            Seed = (Seed + 1) % 4096;
        }
        _wasUnderWater = eyeInFluid && !eyeInLava;
    }

    /// <summary>Ages the droplets by <paramref name="deltaTime"/> seconds.</summary>
    public void Update(float deltaTime)
    {
        if (float.IsFinite(_age))
            _age += MathF.Max(0f, deltaTime);
    }

    /// <summary>
    /// How strongly the droplets show, 0..1: full for
    /// <see cref="WorldGenerationConfig.SurfacingDropletHoldSeconds"/> after surfacing, then a smooth
    /// fade that ends <see cref="WorldGenerationConfig.SurfacingDropletSeconds"/> after it. Only ever
    /// falls between surfacings.
    /// </summary>
    public float Intensity
    {
        get
        {
            if (!float.IsFinite(_age))
                return 0f;
            const float hold = WorldGenerationConfig.SurfacingDropletHoldSeconds;
            return MathUtils.HoldThenFade(_age, hold, WorldGenerationConfig.SurfacingDropletSeconds - hold);
        }
    }

    /// <summary>Seconds since the eye surfaced, which slides the drops down; 0 while none show.</summary>
    public float Seconds => float.IsFinite(_age) ? _age : 0f;
}
