using System.Numerics;

namespace VoxelEngine.Effects.Post;

/// <summary>
/// One circle of the lens flare (<see cref="LensFlare"/>): a reflection of the sun inside the lens,
/// lying on the line from the sun through the screen center.
/// </summary>
/// <param name="Offset">Where it lies on that line: 1 on the sun, 0 at the screen center, negative across the center from the sun.</param>
/// <param name="Radius">Its radius, as a share of the screen height.</param>
/// <param name="Hollow">0 an evenly lit disc, 1 a ring with a dark middle.</param>
/// <param name="Softness">Share of its radius over which its edge fades out.</param>
/// <param name="Tint">Its color (sRGB), which the sun's own color then multiplies.</param>
/// <param name="Brightness">How strong it is: scales the light it adds (<see cref="PostProcessConfig.LensFlareLight"/>)
/// and how much of the picture behind it it covers (<see cref="PostProcessConfig.LensFlareCover"/>).</param>
public readonly record struct LensFlareGhost(float Offset, float Radius, float Hollow, float Softness, Vector3 Tint, float Brightness);
