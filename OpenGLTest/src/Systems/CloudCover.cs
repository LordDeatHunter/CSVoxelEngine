using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Whether the cloud layer, as <see cref="CloudRenderer"/> draws it, stands between an eye and the
/// sky along a ray, read from the same baked coverage grid the cloud shadow samples
/// (<see cref="CloudField.BuildShadowCoverage"/>, one byte per cell, row-major <c>z * cells + x</c>).
/// The grid is read cell by cell, not filtered: a cell holding any layer is cloud across its whole
/// square, exactly the footprint the mesh is built on, so the cover's edge is the drawn silhouette.
/// The night dome's star occlusion (<c>CloudCoverAlongRay</c> in sky-fragment.glsl) is its GL
/// twin; keep the two in step. The sun's glare reads it on the CPU (<see cref="AcrossDisc"/>).
/// GL-free, so the headless tests cover it.
/// </summary>
internal static class CloudCover
{
    /// <summary>
    /// How many layers the field cell under world column (<paramref name="x"/>, <paramref name="z"/>)
    /// stacks this frame (0..<see cref="WorldGenerationConfig.CloudMaxLevels"/>),
    /// <paramref name="drift"/> being the layer's wrapped drift (<see cref="CloudCopies.Drift"/>).
    /// The field repeats every field on both axes.
    /// </summary>
    public static int LevelAt(ReadOnlySpan<byte> coverage, int cells, float x, float z, Vector2 drift)
    {
        var uv = CloudCopies.FieldUv(x, z, drift, WorldGenerationConfig.CloudFieldSize);
        var i = MathUtils.Mod(MathUtils.FloorToInt(uv.X * cells), cells);
        var j = MathUtils.Mod(MathUtils.FloorToInt(uv.Y * cells), cells);
        // The bake stores round(255 * level / maxLevels), which this inverts exactly
        return (int)MathF.Round(coverage[j * cells + i] * WorldGenerationConfig.CloudMaxLevels / 255f);
    }

    /// <summary>
    /// The drawn cloud cover, 0..1, along the ray from <paramref name="eye"/> in unit direction
    /// <paramref name="dir"/>. The ray is tested on every layer boundary it climbs through: at the
    /// base any cloud covers it, and at the top of layer k a cell at least k layers thick does, so a
    /// ray that slips in through a cloud's side wall is caught too. Covered means
    /// <paramref name="blocking"/> (<see cref="Blocking"/>) times the same cubic fog fade that thins
    /// the drawn clouds with distance. 0 when the ray does not climb, when the eye is at or above
    /// the base and with the clouds off.
    /// </summary>
    public static float AlongRay(ReadOnlySpan<byte> coverage, int cells, Vector3 eye, Vector3 dir,
        Vector2 drift, float blocking)
    {
        if (blocking <= 0f || dir.Y <= 1e-3f || eye.Y >= WorldGenerationConfig.CloudHeight) return 0f;

        var tBase = (WorldGenerationConfig.CloudHeight - eye.Y) / dir.Y;
        var covered = false;
        for (var k = 0; k <= WorldGenerationConfig.CloudMaxLevels && !covered; k++)
        {
            var t = tBase + k * WorldGenerationConfig.CloudLayerThickness / dir.Y;
            covered = LevelAt(coverage, cells, eye.X + dir.X * t, eye.Z + dir.Z * t, drift) >= Math.Max(k, 1);
        }

        if (!covered) return 0f;
        var f = MathUtils.InverseLerp(WorldGenerationConfig.FogNearBlocks, WorldGenerationConfig.FogFarBlocks, tBase);
        return blocking * (1f - f * f * f);
    }

    /// <summary>
    /// The drawn cloud cover averaged over a disc of angular radius <paramref name="radius"/>
    /// (radians) around unit direction <paramref name="dir"/>: <see cref="AlongRay"/> at the centre
    /// and on two rings of eight, so the cover of the sun's disc rises in small steps as a cloud
    /// edge drifts across it instead of flipping at once. Allocation-free, for once a frame.
    /// </summary>
    public static float AcrossDisc(ReadOnlySpan<byte> coverage, int cells, Vector3 eye, Vector3 dir, float radius,
        Vector2 drift, float blocking)
    {
        if (blocking <= 0f) return 0f;

        // Any axis well clear of dir builds the disc's basis
        var helper = MathF.Abs(dir.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        var right = Vector3.Normalize(Vector3.Cross(dir, helper));
        var up = Vector3.Cross(right, dir);
        var total = AlongRay(coverage, cells, eye, dir, drift, blocking);
        for (var ring = 1; ring <= 2; ring++)
        {
            var spread = MathF.Tan(radius * ring * 0.5f);
            for (var i = 0; i < 8; i++)
            {
                // The outer ring sits half a step round, so the sixteen spokes interleave
                var a = (i + 0.5f * (ring - 1)) * (MathF.Tau / 8f);
                var sample = Vector3.Normalize(dir + (right * MathF.Cos(a) + up * MathF.Sin(a)) * spread);
                total += AlongRay(coverage, cells, eye, sample, drift, blocking);
            }
        }

        return total / 17f;
    }

    /// <summary>
    /// How completely a cloud blocks a point of light behind it (a star, the sun's glare) at the
    /// player's cloud <paramref name="opacity"/>: fully at the default opacity and above, less as
    /// the clouds are turned down, nothing with them off. The drawn layer itself only covers what
    /// is behind it at its opacity, which would leave a bright star glinting through the middle of
    /// a cloud, so these point lights are dimmed by this first.
    /// </summary>
    public static float Blocking(float opacity) => WorldGenerationConfig.CloudOpacity <= 0f
        ? 0f
        : MathUtils.Clamp01(opacity / WorldGenerationConfig.CloudOpacity);
}
