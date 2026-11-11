using System.Numerics;
using VoxelEngine.Particles;

namespace VoxelEngine.Rendering;

/// <summary>
/// Uploads the splash ripples to a world program (test-fragment.glsl's <c>uRipple*</c>): the wave
/// packet's shape once, and each frame the ripples <see cref="RippleField"/> packed. The program must
/// be bound. Render thread only.
/// </summary>
internal static class RippleUniforms
{
    /// <summary>The one-time part: the packet (<see cref="RippleWave"/>) and the level band a ripple stays in.</summary>
    internal static void Init(Shader program)
    {
        program.SetUniform("uRippleTrain", new Vector4(ParticleConfig.RippleTrainWaves, ParticleConfig.RippleLeadWaves,
            ParticleConfig.RippleRiseWaves, ParticleConfig.RippleChirp));
        program.SetUniform("uRippleLevel", ParticleConfig.RippleLevelTolerance);
    }

    /// <summary>The per-frame part: how many ripples the water shades and where each one stands.</summary>
    internal static void Apply(Shader program, RippleField field)
    {
        program.SetUniform("uRippleCount", field.Count);
        program.SetUniformArray4("uRippleShape", ParticleConfig.RippleShaded, field.Shapes);
        program.SetUniformArray4("uRippleWave", ParticleConfig.RippleShaded, field.Waves);
    }
}
