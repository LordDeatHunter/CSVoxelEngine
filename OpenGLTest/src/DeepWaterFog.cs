using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// How much the underwater fog darkens with the eye's depth below the water surface: the deeper the
/// eye, the darker the fog and the more of its red is lost, red first. A pure function of depth that
/// the renderer evaluates once a frame (<c>uDeepWaterDim</c>); the world fragment multiplies the wet
/// fog colour by <c>1 - Dim</c>, so the water body reads darker and bluer the deeper the swimmer goes.
/// </summary>
public static class DeepWaterFog
{
    /// <summary>
    /// The per-channel dim (r, g, b), each 0..<see cref="WorldGenerationConfig.DeepWaterFogMaxDim"/>, at
    /// <paramref name="depthBlocks"/> below the surface: <c>MaxDim * (1 - exp(-loss * depth / scale))</c>
    /// with the channel's <see cref="WorldGenerationConfig.DeepWaterFogLoss"/>. Zero at or above the
    /// surface, never decreasing with depth, red never less dimmed than green nor green than blue, and
    /// never pitch black.
    /// </summary>
    public static Vector3 Dim(float depthBlocks)
    {
        if (!(depthBlocks > 0f)) return Vector3.Zero;
        var loss = WorldGenerationConfig.DeepWaterFogLoss;
        var d = depthBlocks / WorldGenerationConfig.DeepWaterFogScaleBlocks;
        var max = WorldGenerationConfig.DeepWaterFogMaxDim;
        return new Vector3(
            max * (1f - MathF.Exp(-loss.X * d)),
            max * (1f - MathF.Exp(-loss.Y * d)),
            max * (1f - MathF.Exp(-loss.Z * d)));
    }

    /// <summary>The colour the wet fog is multiplied by at <paramref name="depthBlocks"/>: <c>1 - Dim</c>.</summary>
    public static Vector3 Tint(float depthBlocks) => Vector3.One - Dim(depthBlocks);
}
