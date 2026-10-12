using System;
using VoxelEngine.Data;

namespace VoxelEngine.Lod;

/// <summary>
/// Coarse per-vertex sky exposure for the far LOD shells, baked into <see cref="Vertex.Sun"/>
/// by <see cref="LodRegion.BuildMeshData"/> on the LodGen worker from the region's own
/// column-top grids (no GL, no shadow state, no allocation). It darkens what the far shadow
/// cascade cannot see: a vertex in a notch whose surrounding columns rise above it, and the
/// lower part of a canopy bump. Open ground, open walls and water stay at 1, matching the full
/// skylight near chunks give open sky, so the LOD ring does not read darker than the chunks it
/// fades into.
/// <para>Heights are in LOD cell units: a column whose top cell is <c>t</c> is solid up to
/// <c>t + 1</c>, and a vertex Y is a cell boundary.</para>
/// </summary>
internal static class LodSkyExposure
{
    /// <summary>
    /// Horizon term: 1 minus <see cref="WorldGenerationConfig.LodValleyShade"/> times the mean
    /// of how far each of the three columns rises above the vertex, in units of
    /// <see cref="WorldGenerationConfig.LodSkyRiseCells"/> (saturating at 1). Each column sits one
    /// cell step away, so the rise is a slope and reads the same at every LOD level.
    /// </summary>
    public static float Horizon(int vertexY, int a, int b, int c)
    {
        var blocked = (Blocked(vertexY, a) + Blocked(vertexY, b) + Blocked(vertexY, c)) / 3f;
        return 1f - WorldGenerationConfig.LodValleyShade * blocked;
    }

    private static float Blocked(int vertexY, int top) =>
        Math.Clamp((top + 1 - vertexY) / WorldGenerationConfig.LodSkyRiseCells, 0f, 1f);

    /// <summary>
    /// Understory term for canopy airspace: 1 at a bump's top face, falling by
    /// <see cref="WorldGenerationConfig.LodCanopyShade"/> toward its base at the ground line.
    /// A column with no bump (<paramref name="top"/> at or below <paramref name="ground"/>) is 1.
    /// </summary>
    public static float Understory(int vertexY, int ground, int top)
    {
        if (top <= ground)
            return 1f;
        var depth = Math.Clamp((top + 1 - vertexY) / (float)(top - ground), 0f, 1f);
        return 1f - WorldGenerationConfig.LodCanopyShade * depth;
    }

    /// <summary>Multiplies the two terms, floored at
    /// <see cref="WorldGenerationConfig.LodSkyExposureFloor"/> so no far face goes black.</summary>
    public static float Combine(float horizon, float understory) =>
        Math.Clamp(horizon * understory, WorldGenerationConfig.LodSkyExposureFloor, 1f);

    /// <summary>
    /// Exposure of one top-face corner: the horizon over the same three ring columns the
    /// top-face AO reads (the two sides and the diagonal toward this corner).
    /// </summary>
    public static float TopFace(int ground, int top, int side1, int side2, int diagonal) =>
        Combine(Horizon(top + 1, side1, side2, diagonal), Understory(top + 1, ground, top));

    /// <summary>
    /// Exposure of one side-face corner at <paramref name="vertexY"/> on the face of cell
    /// <paramref name="cy"/>. Only columns in front of the face count — the two front diagonals
    /// and the column straight ahead; the coplanar lateral column is part of the same wall and
    /// blocks no sky, so a straight cliff under open sky keeps full sun. The understory applies
    /// only in canopy airspace (<c>cy &gt; ground</c>); a terrain wall ignores the bump.
    /// </summary>
    public static float SideFace(int vertexY, int cy, int ground, int top, int frontDiagA, int frontDiagB, int front) =>
        Combine(Horizon(vertexY, frontDiagA, frontDiagB, front),
            cy > ground ? Understory(vertexY, ground, top) : 1f);
}
