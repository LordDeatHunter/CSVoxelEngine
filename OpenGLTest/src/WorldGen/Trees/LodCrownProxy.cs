using System;

namespace silkgltriangle;

/// <summary>
/// The far-LOD crown silhouette of a species' <c>lod</c> block: the cheap stand-in the rings
/// beyond the first draw instead of a grown canopy (see <see cref="LodLevelSpec.ProxyCanopy"/>).
/// GL-free and pure.
/// </summary>
public static class LodCrownProxy
{
    /// <summary>
    /// The crown's top above the base cell at horizontal offset (<paramref name="dx"/>,
    /// <paramref name="dz"/>) from the trunk, or 0 where the column is outside the crown. Every
    /// profile is exactly <paramref name="crownHeight"/> at the trunk column (decided before any
    /// division, so radius 0 is the apex alone and never NaN), non-increasing with distance and
    /// never above <paramref name="crownHeight"/>, so the proxy never leaves the species' reach
    /// while <c>crownHeight &lt;= ReachOf.Up</c> and <c>crownRadius &lt;= ReachOf.Horizontal</c>.
    /// </summary>
    public static int TopOffset(LodCrownProfile profile, int crownHeight, int crownRadius, int dx, int dz)
    {
        var d2 = dx * dx + dz * dz;
        if (d2 == 0) return crownHeight;
        var r2 = crownRadius * crownRadius;
        if (d2 > r2) return 0;

        var value = profile switch
        {
            LodCrownProfile.Column => crownHeight,
            // A spherical cap: the rim sinks by how far the sphere's surface falls below its pole
            LodCrownProfile.Dome => crownHeight - (crownRadius - (int)Math.Floor(Math.Sqrt(r2 - d2))),
            // Apex over the trunk, half the height at the rim
            LodCrownProfile.Cone => crownHeight - (int)Math.Ceiling(Math.Sqrt(d2) * crownHeight / (2.0 * crownRadius)),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown LOD crown profile."),
        };
        return Math.Max(0, value);
    }
}
