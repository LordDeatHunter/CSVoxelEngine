using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace silkgltriangle.Tests;

public sealed class CloudTests(ITestOutputHelper output)
{
    private const float S = WorldGenerationConfig.CloudCellSize;
    private const float T = WorldGenerationConfig.CloudLayerThickness;
    private const float Y0 = WorldGenerationConfig.CloudHeight;
    private const float R = WorldGenerationConfig.CloudCornerRadius;

    /// <summary>
    /// Walks the mesh triangle by triangle, handing back the geometric normal. The rounded mesher
    /// emits fans and collapsed quads as well as quads, so nothing may assume a fixed stride.
    /// </summary>
    private static IEnumerable<(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal)> Triangles(
        CloudVertex[] vertices, uint[] indices)
    {
        for (var i = 0; i < indices.Length; i += 3)
        {
            var a = vertices[indices[i]].Position;
            var b = vertices[indices[i + 1]].Position;
            var c = vertices[indices[i + 2]].Position;
            yield return (a, b, c, Vector3.Normalize(Vector3.Cross(b - a, c - a)));
        }
    }

    [Fact]
    public void LevelsAreDeterministic()
    {
        var first = CloudField.BuildLevels();
        var second = CloudField.BuildLevels();

        Assert.Equal(first.GetLength(0), second.GetLength(0));
        Assert.Equal(first.GetLength(1), second.GetLength(1));
        for (var x = 0; x < first.GetLength(0); x++)
        for (var z = 0; z < first.GetLength(1); z++)
            Assert.Equal(first[x, z], second[x, z]);
    }

    [Fact]
    public void DensityIsPeriodic()
    {
        // The field is drawn as 3x3 copies, so the noise has to repeat exactly at the field width
        // or the copies would show a seam.
        const int cells = 256;
        var points = new[] { (3.3f, 17.9f), (200.25f, 0.5f), (255.9f, 255.1f) };
        foreach (var (x, z) in points)
        {
            var baseline = CloudField.Density(x, z, cells, WorldGenerationConfig.CloudSeed);
            Assert.Equal(baseline, CloudField.Density(x + cells, z, cells, WorldGenerationConfig.CloudSeed), precision: 5);
            Assert.Equal(baseline, CloudField.Density(x, z + cells, cells, WorldGenerationConfig.CloudSeed), precision: 5);
        }
    }

    [Fact]
    public void CoverageInBand()
    {
        var levels = CloudField.BuildLevels();
        var cells = levels.GetLength(0);
        var filled = 0;
        var counts = new int[WorldGenerationConfig.CloudMaxLevels + 1];
        for (var x = 0; x < cells; x++)
        for (var z = 0; z < cells; z++)
        {
            var level = levels[x, z];
            Assert.InRange((int)level, 0, WorldGenerationConfig.CloudMaxLevels);
            counts[level]++;
            if (level > 0) filled++;
        }

        var coverage = filled / (float)(cells * cells);
        output.WriteLine($"coverage {coverage:P2}; levels 0..3 = {string.Join(", ", counts)}");
        Assert.True(coverage is >= 0.30f and <= 0.45f, $"Coverage {coverage:P2} is outside 30-45 %");
        Assert.True(counts[2] > 0, "No two-layer cells: the field is a flat sheet");
        Assert.True(counts[3] > 0, "No three-layer cells: nothing reaches CloudMaxLevels");
    }

    [Fact]
    public void ClustersVaryInSize()
    {
        // Big masses plus scattered wisps, not one uniform texture. Flood fill wraps, matching the
        // way the mesher stitches across the field seam.
        var levels = CloudField.BuildLevels();
        var cells = levels.GetLength(0);
        var seen = new bool[cells, cells];
        var sizes = new List<int>();
        var stack = new Stack<(int X, int Z)>();

        for (var sx = 0; sx < cells; sx++)
        for (var sz = 0; sz < cells; sz++)
        {
            if (levels[sx, sz] == 0 || seen[sx, sz]) continue;

            var size = 0;
            seen[sx, sz] = true;
            stack.Push((sx, sz));
            while (stack.Count > 0)
            {
                var (x, z) = stack.Pop();
                size++;
                foreach (var (nx, nz) in new[]
                         {
                             ((x + 1) % cells, z), ((x - 1 + cells) % cells, z),
                             (x, (z + 1) % cells), (x, (z - 1 + cells) % cells)
                         })
                {
                    if (levels[nx, nz] == 0 || seen[nx, nz]) continue;
                    seen[nx, nz] = true;
                    stack.Push((nx, nz));
                }
            }

            sizes.Add(size);
        }

        var largest = 0;
        var tiny = 0;
        foreach (var size in sizes)
        {
            if (size > largest) largest = size;
            if (size <= 4) tiny++;
        }

        output.WriteLine($"components {sizes.Count}; largest {largest}; tiny (<=4) {tiny}");
        Assert.True(largest >= 150, $"Largest component is only {largest} cells");
        Assert.True(sizes.Count >= 40, $"Only {sizes.Count} components: the field is one blob");
        Assert.True(tiny >= 10, $"Only {tiny} components of 4 cells or fewer: no wisps");
    }

    [Fact]
    public void TwoCellsShareNoInteriorWall()
    {
        // The grid wraps, so the shape is padded away from the edges: two adjacent cells in the
        // middle of a 4x3 field mesh as one body with no wall on the boundary they share.
        var levels = new byte[4, 3];
        levels[1, 1] = 1;
        levels[2, 1] = 1;

        var (vertices, indices) = CloudField.BuildMesh(levels);

        foreach (var (a, b, c, n) in Triangles(vertices, indices))
        {
            if (MathF.Abs(n.X) <= 0.5f) continue;
            var shared = MathF.Abs(a.X - 2f * S) < 1e-3f && MathF.Abs(b.X - 2f * S) < 1e-3f &&
                         MathF.Abs(c.X - 2f * S) < 1e-3f;
            Assert.False(shared, "A wall was emitted between two cells of the same height");
        }
    }

    [Fact]
    public void StepEmitsRiser()
    {
        var levels = new byte[4, 3];
        levels[1, 1] = 2;
        levels[2, 1] = 1;

        var (vertices, indices) = CloudField.BuildMesh(levels);

        var risers = 0;
        foreach (var (a, b, c, n) in Triangles(vertices, indices))
        {
            var atBoundary = MathF.Abs(a.X - 2f * S) < 1e-3f && MathF.Abs(b.X - 2f * S) < 1e-3f &&
                             MathF.Abs(c.X - 2f * S) < 1e-3f;
            if (!atBoundary) continue;

            Assert.True(n.X > -0.5f, "The taller cell faces its lower neighbour with an inward wall");
            if (n.X < 0.5f) continue;

            var low = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
            var high = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
            if (low >= Y0 + T - 1e-3f && high <= Y0 + 2f * T - R + 1e-3f) risers++;
        }

        Assert.True(risers > 0, "The step between a 2-layer and a 1-layer cell emitted no riser");
    }

    [Fact]
    public void MeshWrapsAtSeam()
    {
        // Cells on opposite edges are neighbours, so the shared boundary carries no wall at all.
        var levels = new byte[4, 3];
        levels[0, 1] = 1;
        levels[3, 1] = 1;

        var (vertices, indices) = CloudField.BuildMesh(levels);

        foreach (var (a, b, c, n) in Triangles(vertices, indices))
        {
            if (MathF.Abs(n.X) <= 0.5f) continue;
            foreach (var p in new[] { a, b, c })
            {
                Assert.True(MathF.Abs(p.X) > 1e-3f, "A wall was emitted on the x = 0 seam");
                Assert.True(MathF.Abs(p.X - 4f * S) > 1e-3f, "A wall was emitted on the far seam");
            }
        }
    }

    [Fact]
    public void FieldMeshWithinBounds()
    {
        var (vertices, indices) = CloudField.BuildMesh(CloudField.BuildLevels());
        output.WriteLine($"full mesh: {vertices.Length} vertices, {indices.Length} indices");

        Assert.True(vertices.Length > 1000, $"Empty sky: {vertices.Length} verts");
        Assert.True(vertices.Length < 1_000_000, $"Runaway field: {vertices.Length} verts");
        Assert.Equal(0, indices.Length % 3);

        var top = Y0 + WorldGenerationConfig.CloudMaxLevels * T;
        foreach (var vertex in vertices)
        {
            Assert.InRange(vertex.Position.X, 0f, WorldGenerationConfig.CloudFieldSize);
            Assert.InRange(vertex.Position.Z, 0f, WorldGenerationConfig.CloudFieldSize);
            Assert.InRange(vertex.Position.Y, Y0, top);
        }
    }

    [Fact]
    public void WindingIsOutward()
    {
        // Counter-clockwise seen from the normal side, so back-face culling drops the far faces.
        var (vertices, indices) = CloudField.BuildMesh(CloudField.BuildLevels());
        for (var i = 0; i < indices.Length; i += 3)
        {
            var a = vertices[indices[i]];
            var b = vertices[indices[i + 1]].Position;
            var c = vertices[indices[i + 2]].Position;
            var facing = Vector3.Dot(Vector3.Cross(b - a.Position, c - a.Position), a.Normal);
            Assert.True(facing > 0f, $"Triangle {i / 3} winds inward (dot {facing})");
        }
    }

    [Fact]
    public void CurvedNormalsAreUnit()
    {
        // The fillets, octants and tori carry per-vertex normals, so the shader normalises nothing:
        // a normal that drifted off unit length would show up as a bright or dark band.
        var (vertices, _) = CloudField.BuildMesh(CloudField.BuildLevels());
        foreach (var vertex in vertices)
            Assert.Equal(1f, vertex.Normal.Length(), tolerance: 1e-3f);
    }

    [Fact]
    public void RoundedMeshIsWatertight()
    {
        // The real acceptance test for the rounded mesher: fire rays at random level grids and check
        // that nothing ever reaches the inside of the blocky solid without first crossing a
        // front-facing triangle. A missing fillet, octant, torus, ledge or riser patch leaves a hole
        // that some ray will thread. Points within one corner radius of the air are skipped, since
        // that is exactly the shell the rounding carves away.
        const int rays = 1500;
        const float radius = 3f * S + 4f * T;
        const float step = 0.25f;
        const float skin = R + 0.05f;
        var centre = new Vector3(2.5f * S, Y0 + 1.5f * T, 2.5f * S);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        for (var seed = 0; seed < 30; seed++)
        {
            var rng = new Random(seed);
            var levels = new byte[5, 5];
            for (var x = 1; x <= 3; x++)
            for (var z = 1; z <= 3; z++)
                levels[x, z] = (byte)rng.Next(0, 4);

            var (vertices, indices) = CloudField.BuildMesh(levels);
            var count = indices.Length / 3;
            var v0 = new Vector3[count];
            var e1 = new Vector3[count];
            var e2 = new Vector3[count];
            var face = new Vector3[count];
            var lo = new Vector3[count];
            var hi = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                var a = vertices[indices[3 * i]].Position;
                var b = vertices[indices[3 * i + 1]].Position;
                var c = vertices[indices[3 * i + 2]].Position;
                v0[i] = a;
                e1[i] = b - a;
                e2[i] = c - a;
                face[i] = Vector3.Cross(e1[i], e2[i]);
                lo[i] = Vector3.Min(a, Vector3.Min(b, c));
                hi[i] = Vector3.Max(a, Vector3.Max(b, c));
            }

            // Nothing solid lives outside this box, so a sample beyond it can never be deep inside
            // the material and the scan can start where the ray enters instead of at the origin.
            var fieldLo = new Vector3(0f, Y0, 0f);
            var fieldHi = new Vector3(5f * S, Y0 + WorldGenerationConfig.CloudMaxLevels * T, 5f * S);

            for (var ray = 0; ray < rays; ray++)
            {
                var u = (float)(rng.NextDouble() * 2.0 - 1.0);
                var phi = (float)(rng.NextDouble() * 2.0 * Math.PI);
                var ring = MathF.Sqrt(MathF.Max(0f, 1f - u * u));
                var origin = centre + radius * new Vector3(ring * MathF.Cos(phi), u, ring * MathF.Sin(phi));
                var target = new Vector3(
                    (float)rng.NextDouble() * 5f * S,
                    Y0 + (float)rng.NextDouble() * 3f * T,
                    (float)rng.NextDouble() * 5f * S);
                var dir = Vector3.Normalize(target - origin);
                var inverse = new Vector3(1f / dir.X, 1f / dir.Y, 1f / dir.Z);

                var nearest = float.PositiveInfinity;
                for (var i = 0; i < count; i++)
                {
                    if (Vector3.Dot(dir, face[i]) >= 0f) continue;
                    if (!Slab(origin, inverse, lo[i], hi[i], out _, out _)) continue;
                    if (!Intersect(origin, dir, v0[i], e1[i], e2[i], out var hit)) continue;
                    if (hit < nearest) nearest = hit;

                    // Every front face has to sit on the blocky solid; a stray patch floating in air
                    // would let the ray "enter" somewhere the material never was.
                    var point = origin + hit * dir;
                    if (!OnSolid(levels, point))
                        Assert.Fail($"seed {seed} ray {ray}: front face at {point} is off the solid\n{Describe(levels)}");
                }

                Slab(origin, inverse, fieldLo, fieldHi, out var enter, out var exit);
                var limit = MathF.Min(MathF.Min(nearest, 2f * radius), exit);
                for (var t = MathF.Ceiling(MathF.Max(0f, enter) / step) * step; t < limit; t += step)
                {
                    var point = origin + t * dir;
                    var depth = DistToComplement(levels, point, skin);
                    if (depth > skin)
                        Assert.Fail($"seed {seed} ray {ray}: reached {point} (depth {depth:F3}) without crossing " +
                                    $"a face; origin {origin} dir {dir}\n{Describe(levels)}");
                }
            }
        }

        watch.Stop();
        output.WriteLine($"watertight: 30 grids x {rays} rays in {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Ray-versus-box slab test. Used twice: to throw out the triangles a ray comes nowhere near
    /// before paying for Moller-Trumbore, and to clip the depth scan to the field's own box.
    /// </summary>
    private static bool Slab(Vector3 origin, Vector3 inverse, Vector3 lo, Vector3 hi, out float enter, out float exit)
    {
        var a = (lo - origin) * inverse;
        var b = (hi - origin) * inverse;
        var near = Vector3.Min(a, b);
        var far = Vector3.Max(a, b);
        enter = MathF.Max(near.X, MathF.Max(near.Y, near.Z));
        exit = MathF.Min(far.X, MathF.Min(far.Y, far.Z));
        return exit >= MathF.Max(enter, 0f);
    }

    /// <summary>Moller-Trumbore; returns the positive ray parameter of the intersection.</summary>
    private static bool Intersect(Vector3 origin, Vector3 dir, Vector3 v0, Vector3 e1, Vector3 e2, out float t)
    {
        t = 0f;
        var pv = Vector3.Cross(dir, e2);
        var det = Vector3.Dot(e1, pv);
        if (MathF.Abs(det) < 1e-9f) return false;

        var inv = 1f / det;
        var tv = origin - v0;
        var u = Vector3.Dot(tv, pv) * inv;
        if (u is < 0f or > 1f) return false;

        var qv = Vector3.Cross(tv, e1);
        var v = Vector3.Dot(dir, qv) * inv;
        if (v < 0f || u + v > 1f) return false;

        t = Vector3.Dot(e2, qv) * inv;
        return t > 1e-4f;
    }

    /// <summary>
    /// Distance from <paramref name="p"/> to the nearest air: below the layer, above some cell's
    /// stack, or outside the grid. Bails out as soon as it drops to <paramref name="cutoff"/>,
    /// which is all the caller needs to know, and starts with the cell the point stands in because
    /// that is almost always the one that settles it.
    /// </summary>
    private static float DistToComplement(byte[,] levels, Vector3 p, float cutoff)
    {
        var best = p.Y - Y0;
        if (best <= cutoff) return best;

        var cells = levels.GetLength(0);
        var here = CellDistance(levels, p, Index(p.X, cells), Index(p.Z, cells));
        if (here < best)
        {
            best = here;
            if (best <= cutoff) return best;
        }

        for (var cx = 0; cx < cells; cx++)
        for (var cz = 0; cz < cells; cz++)
        {
            var d = CellDistance(levels, p, cx, cz);
            if (d >= best) continue;
            best = d;
            if (best <= cutoff) return best;
        }

        var span = cells * S;
        best = MathF.Min(best, MathF.Max(p.X, 0f));
        best = MathF.Min(best, MathF.Max(span - p.X, 0f));
        best = MathF.Min(best, MathF.Max(p.Z, 0f));
        best = MathF.Min(best, MathF.Max(span - p.Z, 0f));
        return best;
    }

    /// <summary>Distance to the open air above one cell's stack, treated as a half-infinite box.</summary>
    private static float CellDistance(byte[,] levels, Vector3 p, int cx, int cz)
    {
        var dx = MathF.Max(MathF.Max(cx * S - p.X, p.X - (cx + 1) * S), 0f);
        var dz = MathF.Max(MathF.Max(cz * S - p.Z, p.Z - (cz + 1) * S), 0f);
        var dy = MathF.Max(Y0 + levels[cx, cz] * T - p.Y, 0f);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>True when the point lies on the blocky solid the rounded mesh is carved out of.</summary>
    private static bool OnSolid(byte[,] levels, Vector3 p)
    {
        const float slack = 0.05f;
        var cells = levels.GetLength(0);
        var span = cells * S;
        if (p.X < -slack || p.X > span + slack || p.Z < -slack || p.Z > span + slack) return false;
        if (p.Y < Y0 - slack) return false;

        // A face on a cell boundary belongs to whichever side is taller, so widen the lookup by the
        // same slack instead of trusting one cell index.
        var top = Y0;
        for (var cx = Index(p.X - slack, cells); cx <= Index(p.X + slack, cells); cx++)
        for (var cz = Index(p.Z - slack, cells); cz <= Index(p.Z + slack, cells); cz++)
            top = MathF.Max(top, Y0 + levels[cx, cz] * T);

        return p.Y <= top + slack;
    }

    private static int Index(float world, int cells) =>
        Math.Clamp((int)MathF.Floor(world / S), 0, cells - 1);

    /// <summary>The inner 3x3 levels, so a watertightness failure prints the grid that produced it.</summary>
    private static string Describe(byte[,] levels)
    {
        var text = new System.Text.StringBuilder("levels (x across, z down):");
        for (var z = levels.GetLength(1) - 1; z >= 0; z--)
        {
            text.Append("\n  ");
            for (var x = 0; x < levels.GetLength(0); x++) text.Append(levels[x, z]).Append(' ');
        }

        return text.ToString();
    }

    [Fact]
    public void CloudShadersExist()
    {
        var root = TestContent.FindAssetsRoot();
        Assert.True(File.Exists(Path.Combine(root, "shaders", "cloud-vertex.glsl")));
        Assert.True(File.Exists(Path.Combine(root, "shaders", "cloud-fragment.glsl")));
    }
}
