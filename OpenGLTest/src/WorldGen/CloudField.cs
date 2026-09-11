using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace silkgltriangle;

/// <summary>Position + flat face normal; the cloud shader needs nothing else.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CloudVertex(Vector3 position, Vector3 normal)
{
    public Vector3 Position = position;
    public Vector3 Normal = normal;
    public static readonly uint Size = (uint)Marshal.SizeOf<CloudVertex>(); // 24
}

/// <summary>
/// The cloud layer as a 2D grid of cells, each holding 0..CloudMaxLevels stacked slabs, plus the
/// mesher that turns that grid into one stitched surface. Everything here is pure math over plain
/// arrays, so the headless tests cover the shape of the sky without a GL context.
///
/// Levels come from periodic value noise whose every octave period divides the field width, so the
/// field tiles with no seam; the mesher wraps its neighbour lookups for the same reason, which lets
/// a cloud straddling the field edge join its own copy one field over instead of growing a wall.
/// </summary>
public static class CloudField
{
    /// <summary>Cells per field side, e.g. 2048 / 8 = 256.</summary>
    public static int Cells => (int)(WorldGenerationConfig.CloudFieldSize / WorldGenerationConfig.CloudCellSize);

    /// <summary>Levels[x, z] in 0..CloudMaxLevels for the configured field.</summary>
    public static byte[,] BuildLevels() => BuildLevels(Cells, WorldGenerationConfig.CloudSeed,
        WorldGenerationConfig.CloudThreshold, WorldGenerationConfig.CloudLevelStep, WorldGenerationConfig.CloudMaxLevels);

    /// <summary>
    /// Thresholds the density field into stacked layers: below <paramref name="threshold"/> the sky
    /// is open, and every further <paramref name="levelStep"/> of density piles on one more layer.
    /// That keeps the thick cores in the middle of a mass and thins the outline down to one layer.
    /// </summary>
    public static byte[,] BuildLevels(int cells, int seed, float threshold, float levelStep, int maxLevels)
    {
        var levels = new byte[cells, cells];
        for (var x = 0; x < cells; x++)
        for (var z = 0; z < cells; z++)
        {
            var d = Density(x + 0.5f, z + 0.5f, cells, seed);
            levels[x, z] = d <= threshold
                ? (byte)0
                : (byte)Math.Min(maxLevels, 1 + (int)((d - threshold) / levelStep));
        }

        return levels;
    }

    /// <summary>Cloud density 0..1 at cell coordinates; periodic with period <paramref name="cells"/> in both axes.</summary>
    public static float Density(float cx, float cz, int cells, int seed)
    {
        // Wavelengths are fixed in blocks and converted to cell spacings, so coarsening
        // CloudCellSize keeps the same physical sky instead of silently doubling every cloud.
        // These are the values the two bands were tuned to at the original 8-block cell: a macro
        // mass roughly every 512 blocks and outlines down to ~64. Every octave period must still
        // divide the field, which the power-of-two cell sizes guarantee.
        var cellSize = WorldGenerationConfig.CloudFieldSize / cells;
        var macroSpacing = Math.Max(1, (int)MathF.Round(MacroWavelengthBlocks / cellSize));
        var detailSpacing = Math.Max(1, (int)MathF.Round(DetailWavelengthBlocks / cellSize));
        var macro = Fbm(cx, cz, cells, macroSpacing, 2, seed);
        var detail = Fbm(cx, cz, cells, detailSpacing, 3, seed + 104729);
        return 0.45f * macro + 0.55f * detail;
    }

    // Physical wavelengths of the two noise bands, in blocks.
    private const float MacroWavelengthBlocks = 512f;
    private const float DetailWavelengthBlocks = 64f;

    /// <summary>
    /// Meshes a level grid of any size; cell size and heights come from WorldGenerationConfig.
    /// Neighbour lookups wrap, so no faces land on the field seam.
    ///
    /// Every convex edge of the blocky level field is replaced by a fillet of radius
    /// <see cref="WorldGenerationConfig.CloudCornerRadius"/>, which is why the old run merging is
    /// gone: a cell's surface now depends on all eight of its neighbours, so neighbouring cells no
    /// longer share a face that could be merged into a run. The pieces (cap tiles, quarter-cylinder
    /// fillets, sphere octants, horn-torus junctions, walls, vertical corner cylinders, ledges and
    /// riser patches) are laid out so that they meet on exactly the same boundary curves, which is
    /// what keeps the result a closed surface; CloudTests.RoundedMeshIsWatertight checks that.
    /// </summary>
    public static (CloudVertex[] Vertices, uint[] Indices) BuildMesh(byte[,] levels)
    {
        var sizeX = levels.GetLength(0);
        var sizeZ = levels.GetLength(1);
        var s = WorldGenerationConfig.CloudCellSize;
        var builder = new MeshBuilder();
        var nb = new int[3, 3];

        for (var x = 0; x < sizeX; x++)
        for (var z = 0; z < sizeZ; z++)
        {
            int level = levels[x, z];
            if (level == 0) continue;

            for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
                nb[dx + 1, dz + 1] = levels[((x + dx) % sizeX + sizeX) % sizeX, ((z + dz) % sizeZ + sizeZ) % sizeZ];

            var cell = new Cell(nb, level, x * s, (x + 1) * s, z * s, (z + 1) * s);
            EmitCap(builder, cell, WorldGenerationConfig.CloudHeight + level * WorldGenerationConfig.CloudLayerThickness, 1f, true);
            EmitCap(builder, cell, WorldGenerationConfig.CloudHeight, -1f, false);
            EmitWalls(builder, cell);
            EmitCornerColumns(builder, cell);
            EmitRiserPatches(builder, cell);
        }

        return (builder.Vertices.ToArray(), builder.Indices.ToArray());
    }

    /// <summary>One cell's footprint plus the 3x3 block of levels around it, already wrapped.</summary>
    private readonly struct Cell(int[,] nb, int level, float x0, float x1, float z0, float z1)
    {
        private readonly int[,] _nb = nb;
        public readonly int Level = level;
        public readonly float X0 = x0, X1 = x1, Z0 = z0, Z1 = z1;

        /// <summary>Level of the neighbour <paramref name="dx"/>, <paramref name="dz"/> cells away.</summary>
        public int At(int dx, int dz) => _nb[dx + 1, dz + 1];

        public float CornerX(int sx) => sx > 0 ? X1 : X0;
        public float CornerZ(int sz) => sz > 0 ? Z1 : Z0;

        /// <summary>
        /// A vertical corner is convex in band <paramref name="k"/> only when both neighbours that
        /// meet there are air in that band; otherwise their material fills the corner and there is
        /// nothing to round off. Monotone in k, which is what lets <see cref="EmitCornerColumns"/>
        /// place a single ledge at the height where the corner starts being rounded.
        /// </summary>
        public bool IsConvex(int sx, int sz, int k) => At(sx, 0) < k && At(0, sz) < k;
    }

    /// <summary>
    /// The fillet profile at angle <paramref name="a"/>, which rolls the surface from the cap plane
    /// (a = 0) round to the wall plane (a = pi/2). Returned as (across, up) in the plane
    /// perpendicular to the sweep so the X-facing and Z-facing fillets can share one formula:
    /// <paramref name="edge"/> is the cell edge the fillet ends on, <paramref name="sEdge"/> its
    /// outward sign, <paramref name="yb"/> the cap plane and <paramref name="sy"/> +1 for the top.
    /// </summary>
    private static (float Across, float Up, float NAcross, float NUp) Profile(
        float edge, int sEdge, float yb, float sy, float a)
    {
        var r = WorldGenerationConfig.CloudCornerRadius;
        var sin = MathF.Sin(a);
        var cos = MathF.Cos(a);
        return (edge - sEdge * r * (1f - sin), yb - sy * r + sy * r * cos, sEdge * sin, sy * cos);
    }

    /// <summary>Angle of fillet step <paramref name="i"/> out of <see cref="WorldGenerationConfig.CloudFilletSegments"/>.</summary>
    private static float Angle(int i) => i * MathF.PI / (2f * WorldGenerationConfig.CloudFilletSegments);

    /// <summary>
    /// One horizontal cap (top when <paramref name="sy"/> is +1, bottom when -1) as a 3x3 autotile:
    /// a flat centre, four edge tiles that stay flat or bend into a quarter-cylinder fillet, and
    /// four corner tiles that resolve to a flat quad, a straight continuation of one neighbouring
    /// fillet, a sphere octant where two fillets meet convexly, or a horn torus where they meet
    /// concavely. Splitting the cap this way is what makes the rounding a pure per-cell decision:
    /// two cells that share an edge always pick the same profile for it.
    /// </summary>
    private static void EmitCap(MeshBuilder b, in Cell c, float yb, float sy, bool top)
    {
        var r = WorldGenerationConfig.CloudCornerRadius;
        // The top of a cell is buried wherever the neighbour stacks at least as high; the bottom is
        // buried wherever the neighbour has any layer at all, since every cell starts at CloudHeight.
        var need = top ? c.Level : 1;

        if (c.At(1, 0) >= need && c.At(-1, 0) >= need && c.At(0, 1) >= need && c.At(0, -1) >= need &&
            c.At(1, 1) >= need && c.At(-1, 1) >= need && c.At(1, -1) >= need && c.At(-1, -1) >= need)
        {
            AddFlat(b, c.X0, c.X1, c.Z0, c.Z1, yb, sy);
            return;
        }

        AddFlat(b, c.X0 + r, c.X1 - r, c.Z0 + r, c.Z1 - r, yb, sy);

        for (var sx = -1; sx <= 1; sx += 2)
        {
            var cx = c.CornerX(sx);
            if (c.At(sx, 0) >= need) AddFlat(b, cx, cx - sx * r, c.Z0 + r, c.Z1 - r, yb, sy);
            else AddFilletX(b, cx, sx, yb, sy, c.Z0 + r, c.Z1 - r);
        }

        for (var sz = -1; sz <= 1; sz += 2)
        {
            var cz = c.CornerZ(sz);
            if (c.At(0, sz) >= need) AddFlat(b, c.X0 + r, c.X1 - r, cz, cz - sz * r, yb, sy);
            else AddFilletZ(b, cz, sz, yb, sy, c.X0 + r, c.X1 - r);
        }

        for (var sx = -1; sx <= 1; sx += 2)
        for (var sz = -1; sz <= 1; sz += 2)
        {
            float cx = c.CornerX(sx), cz = c.CornerZ(sz);
            float ix = cx - sx * r, iz = cz - sz * r;
            var openX = c.At(sx, 0) < need;
            var openZ = c.At(0, sz) < need;

            if (openX && openZ) AddOctant(b, ix, yb - sy * r, iz, sx, sy, sz);
            else if (openX) AddFilletX(b, cx, sx, yb, sy, iz, cz);
            else if (openZ) AddFilletZ(b, cz, sz, yb, sy, ix, cx);
            else if (c.At(sx, sz) >= need) AddFlat(b, ix, cx, iz, cz, yb, sy);
            // Both sides are buried but the diagonal is not, so the two neighbours' fillets turn the
            // corner into an inside curve. That only happens when they sit at exactly this cap: a
            // taller neighbour's wall runs through here instead and the tile stays flat.
            else if (!top || (c.At(sx, 0) == c.Level && c.At(0, sz) == c.Level))
                AddTorus(b, cx, cz, sx, sy, sz, yb);
            else AddFlat(b, ix, cx, iz, cz, yb, sy);
        }
    }

    /// <summary>Flat cap tile spanning the two x and two z coordinates, facing <paramref name="sy"/>.</summary>
    private static void AddFlat(MeshBuilder b, float xa, float xb, float za, float zb, float y, float sy)
    {
        b.FlatQuad(new Vector3(xa, y, za), new Vector3(xb, y, za),
            new Vector3(xb, y, zb), new Vector3(xa, y, zb), new Vector3(0f, sy, 0f));
    }

    /// <summary>Quarter cylinder rolling the cap edge at x = <paramref name="cx"/> onto the x wall.</summary>
    private static void AddFilletX(MeshBuilder b, float cx, int sx, float yb, float sy, float za, float zb)
    {
        var seg = WorldGenerationConfig.CloudFilletSegments;
        Span<uint> near = stackalloc uint[seg + 1];
        Span<uint> far = stackalloc uint[seg + 1];
        for (var i = 0; i <= seg; i++)
        {
            var p = Profile(cx, sx, yb, sy, Angle(i));
            var n = new Vector3(p.NAcross, p.NUp, 0f);
            near[i] = b.Add(new Vector3(p.Across, p.Up, za), n);
            far[i] = b.Add(new Vector3(p.Across, p.Up, zb), n);
        }

        for (var i = 0; i < seg; i++) b.Quad(near[i], far[i], far[i + 1], near[i + 1]);
    }

    /// <summary>Quarter cylinder rolling the cap edge at z = <paramref name="cz"/> onto the z wall.</summary>
    private static void AddFilletZ(MeshBuilder b, float cz, int sz, float yb, float sy, float xa, float xb)
    {
        var seg = WorldGenerationConfig.CloudFilletSegments;
        Span<uint> near = stackalloc uint[seg + 1];
        Span<uint> far = stackalloc uint[seg + 1];
        for (var i = 0; i <= seg; i++)
        {
            var p = Profile(cz, sz, yb, sy, Angle(i));
            var n = new Vector3(0f, p.NUp, p.NAcross);
            near[i] = b.Add(new Vector3(xa, p.Up, p.Across), n);
            far[i] = b.Add(new Vector3(xb, p.Up, p.Across), n);
        }

        for (var i = 0; i < seg; i++) b.Quad(near[i], far[i], far[i + 1], near[i + 1]);
    }

    /// <summary>
    /// Sphere octant closing the outside corner where two fillets and a vertical corner cylinder
    /// meet: its th = pi/2 equator is the cylinder's cross-section and its ph = 0 and ph = pi/2
    /// meridians are the two fillet profiles, so the three pieces share their boundary curves exactly.
    /// </summary>
    private static void AddOctant(MeshBuilder b, float cx, float cy, float cz, int sx, float sy, int sz)
    {
        var r = WorldGenerationConfig.CloudCornerRadius;
        var seg = WorldGenerationConfig.CloudFilletSegments;
        var centre = new Vector3(cx, cy, cz);
        var stride = seg + 1;
        Span<uint> grid = stackalloc uint[stride * stride];

        // The th = 0 row is the pole, one point with one normal however far ph has turned, so the
        // whole row indexes a single vertex and the quads that touch it collapse to triangles.
        var pole = Sphere(sx, sy, sz, 0f, 0f);
        grid[0] = b.Add(centre + r * pole, pole);
        for (var j = 1; j <= seg; j++) grid[j] = grid[0];

        for (var i = 1; i <= seg; i++)
        for (var j = 0; j <= seg; j++)
        {
            var d = Sphere(sx, sy, sz, Angle(i), Angle(j));
            grid[i * stride + j] = b.Add(centre + r * d, d);
        }

        for (var i = 0; i < seg; i++)
        for (var j = 0; j < seg; j++)
            b.Quad(grid[i * stride + j], grid[i * stride + j + 1],
                grid[(i + 1) * stride + j + 1], grid[(i + 1) * stride + j]);
    }

    /// <summary>Unit direction on the rounded corner: th = 0 points along the cap, th = pi/2 sideways.</summary>
    private static Vector3 Sphere(int sx, float sy, int sz, float th, float ph) =>
        new(sx * MathF.Sin(th) * MathF.Cos(ph), sy * MathF.Cos(th), sz * MathF.Sin(th) * MathF.Sin(ph));

    /// <summary>
    /// Horn-torus patch plus its flat remainder for an inside corner: both neighbours reach this cap
    /// but the diagonal does not, so their fillets have to be joined around the corner point. Sweeping
    /// the fillet profile along the quarter circle of radius r about that point reproduces each
    /// neighbour's profile exactly at ph = 0 and ph = pi/2, which is what makes the seam invisible.
    /// </summary>
    private static void AddTorus(MeshBuilder b, float cx, float cz, int sx, float sy, int sz, float yb)
    {
        var r = WorldGenerationConfig.CloudCornerRadius;
        var seg = WorldGenerationConfig.CloudFilletSegments;
        var stride = seg + 1;
        Span<uint> grid = stackalloc uint[stride * stride];
        Span<Vector3> rim = stackalloc Vector3[stride];

        for (var i = 0; i <= seg; i++)
        for (var j = 0; j <= seg; j++)
        {
            var (p, d) = Torus(cx, cz, sx, sy, sz, yb, Angle(i), Angle(j));
            grid[i * stride + j] = b.Add(p, d);
        }

        for (var i = 0; i < seg; i++)
        for (var j = 0; j < seg; j++)
            b.Quad(grid[i * stride + j], grid[(i + 1) * stride + j],
                grid[(i + 1) * stride + j + 1], grid[i * stride + j + 1]);

        // The rest of the corner tile is still flat: everything outside the quarter disc the torus
        // curls into. A fan from the tile's inner corner to the arc covers exactly that.
        for (var i = 0; i <= seg; i++)
        {
            var a = Angle(i);
            rim[i] = new Vector3(cx - sx * r * MathF.Cos(a), yb, cz - sz * r * MathF.Sin(a));
        }

        b.FlatFan(new Vector3(cx - sx * r, yb, cz - sz * r), new Vector3(0f, sy, 0f), rim);
    }

    /// <summary>Point and unit normal on the horn torus wrapped around the corner (cx, yb, cz).</summary>
    private static (Vector3 P, Vector3 N) Torus(float cx, float cz, int sx, float sy, int sz, float yb, float ph, float th)
    {
        var r = WorldGenerationConfig.CloudCornerRadius;
        var ring = new Vector3(cx - sx * r * MathF.Cos(ph), yb - sy * r, cz - sz * r * MathF.Sin(ph));
        var d = Sphere(sx, sy, sz, th, ph);
        return (ring + r * d, d);
    }

    /// <summary>
    /// The flat side faces, one per exposed side and layer band. Each band is trimmed by the corner
    /// radius at an end whose vertical corner is rounded in that band, leaving room for the corner
    /// cylinder; the top band stops r below the cap and a band starting on the floor starts r above
    /// it, leaving room for the cap fillets.
    /// </summary>
    private static void EmitWalls(MeshBuilder b, in Cell c)
    {
        var t = WorldGenerationConfig.CloudLayerThickness;
        var y0 = WorldGenerationConfig.CloudHeight;
        var r = WorldGenerationConfig.CloudCornerRadius;
        var yt = y0 + c.Level * t;

        foreach (var (dx, dz) in Dirs)
        {
            var nd = c.At(dx, dz);
            if (nd >= c.Level) continue;

            var n = new Vector3(dx, 0f, dz);
            for (var k = nd + 1; k <= c.Level; k++)
            {
                var low = k == 1 ? y0 + r : y0 + (k - 1) * t;
                var high = k == c.Level ? yt - r : y0 + k * t;
                if (dz == 0)
                {
                    var cx = c.CornerX(dx);
                    var za = c.IsConvex(dx, -1, k) ? c.Z0 + r : c.Z0;
                    var zb = c.IsConvex(dx, 1, k) ? c.Z1 - r : c.Z1;
                    b.FlatQuad(new Vector3(cx, low, za), new Vector3(cx, low, zb),
                        new Vector3(cx, high, zb), new Vector3(cx, high, za), n);
                }
                else
                {
                    var cz = c.CornerZ(dz);
                    var xa = c.IsConvex(-1, dz, k) ? c.X0 + r : c.X0;
                    var xb = c.IsConvex(1, dz, k) ? c.X1 - r : c.X1;
                    b.FlatQuad(new Vector3(xa, low, cz), new Vector3(xb, low, cz),
                        new Vector3(xb, high, cz), new Vector3(xa, high, cz), n);
                }
            }
        }
    }

    /// <summary>
    /// The vertical quarter cylinder that rounds each convex corner, band by band, plus the ledge
    /// that closes the step where a corner starts being rounded partway up: below it the walls run
    /// out to the sharp corner, above it they are trimmed back, and the difference is an up-facing
    /// square minus the cylinder's quarter disc. Consecutive rounded bands hand their shared ring of
    /// vertices on, so a three-layer corner costs four rings rather than six.
    /// </summary>
    private static void EmitCornerColumns(MeshBuilder b, in Cell c)
    {
        var t = WorldGenerationConfig.CloudLayerThickness;
        var y0 = WorldGenerationConfig.CloudHeight;
        var r = WorldGenerationConfig.CloudCornerRadius;
        var seg = WorldGenerationConfig.CloudFilletSegments;
        var yt = y0 + c.Level * t;
        Span<uint> below = stackalloc uint[seg + 1];
        Span<uint> above = stackalloc uint[seg + 1];
        Span<Vector3> rim = stackalloc Vector3[seg + 1];

        for (var sx = -1; sx <= 1; sx += 2)
        for (var sz = -1; sz <= 1; sz += 2)
        {
            float cx = c.CornerX(sx), cz = c.CornerZ(sz);
            float ax = cx - sx * r, az = cz - sz * r;
            var first = 0;
            var carried = float.NaN;

            for (var k = 1; k <= c.Level; k++)
            {
                if (!c.IsConvex(sx, sz, k)) continue;
                if (first == 0) first = k;

                var low = k == 1 ? y0 + r : y0 + (k - 1) * t;
                var high = k == c.Level ? yt - r : y0 + k * t;
                if (carried != low) AddRing(b, below, ax, az, sx, sz, low);
                AddRing(b, above, ax, az, sx, sz, high);
                for (var i = 0; i < seg; i++) b.Quad(below[i], below[i + 1], above[i + 1], above[i]);

                above.CopyTo(below);
                carried = high;
            }

            if (first <= 1) continue;

            var y = y0 + (first - 1) * t;
            for (var i = 0; i <= seg; i++)
            {
                var d = Ring(sx, sz, Angle(i));
                rim[i] = new Vector3(ax + r * d.X, y, az + r * d.Z);
            }

            b.FlatFan(new Vector3(cx, y, cz), Vector3.UnitY, rim);
        }
    }

    /// <summary>One horizontal ring of the corner cylinder at height <paramref name="y"/>.</summary>
    private static void AddRing(MeshBuilder b, Span<uint> ring, float ax, float az, int sx, int sz, float y)
    {
        var r = WorldGenerationConfig.CloudCornerRadius;
        for (var i = 0; i < ring.Length; i++)
        {
            var d = Ring(sx, sz, Angle(i));
            ring[i] = b.Add(new Vector3(ax + r * d.X, y, az + r * d.Z), d);
        }
    }

    /// <summary>Unit horizontal direction on a vertical corner cylinder; a = 0 faces x, a = pi/2 faces z.</summary>
    private static Vector3 Ring(int sx, int sz, float a) => new(sx * MathF.Cos(a), 0f, sz * MathF.Sin(a));

    /// <summary>
    /// Closes the sliver a lower neighbour's cap fillet uncovers against our side face. Where that
    /// neighbour's own edge is exposed at its top, its fillet curls away from our wall plane just
    /// below its cap, and the square-minus-quarter-disc it leaves behind is part of our face. The
    /// same happens against an equally tall neighbour when the corner is squared off by a taller
    /// third cell, which is the only case there where our cap does not already meet that fillet.
    /// </summary>
    private static void EmitRiserPatches(MeshBuilder b, in Cell c)
    {
        var t = WorldGenerationConfig.CloudLayerThickness;
        var y0 = WorldGenerationConfig.CloudHeight;
        var seg = WorldGenerationConfig.CloudFilletSegments;
        Span<Vector3> rim = stackalloc Vector3[seg + 1];

        for (var sx = -1; sx <= 1; sx += 2)
        for (var sz = -1; sz <= 1; sz += 2)
        for (var axis = 0; axis < 2; axis++)
        {
            // axis 0: the wall faces x and the fillet runs along z; axis 1 swaps the two.
            var nd = axis == 0 ? c.At(sx, 0) : c.At(0, sz);
            var np = axis == 0 ? c.At(0, sz) : c.At(sx, 0);
            if (nd < 1 || nd > c.Level || c.At(sx, sz) >= nd) continue;
            if (nd == c.Level && np <= c.Level) continue;

            var yd = y0 + nd * t;
            var n = axis == 0 ? new Vector3(sx, 0f, 0f) : new Vector3(0f, 0f, sz);
            for (var i = 0; i <= seg; i++) rim[i] = PatchPoint(c, sx, sz, axis, yd, Angle(i));
            b.FlatFan(new Vector3(c.CornerX(sx), yd, c.CornerZ(sz)), n, rim);
        }
    }

    /// <summary>Point on the neighbour's fillet arc as it crosses our wall plane.</summary>
    private static Vector3 PatchPoint(in Cell c, int sx, int sz, int axis, float yd, float a)
    {
        var r = WorldGenerationConfig.CloudCornerRadius;
        var across = r * (1f - MathF.Sin(a));
        var y = yd - r + r * MathF.Cos(a);
        return axis == 0
            ? new Vector3(c.CornerX(sx), y, c.CornerZ(sz) - sz * across)
            : new Vector3(c.CornerX(sx) - sx * across, y, c.CornerZ(sz));
    }

    /// <summary>Unit steps to the four edge neighbours, in the order E, W, N, S.</summary>
    private static readonly (int X, int Z)[] Dirs = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>
    /// Collects an indexed triangle mesh. Each patch stores its vertex lattice once and then names
    /// those vertices from several triangles, which is what keeps the field mesh around a third of
    /// the size it would be with three fresh vertices per triangle.
    ///
    /// Winding is derived from the vertex normals rather than tracked by hand: with this many
    /// parametric patches, orienting each one from its sign parameters is easy to get wrong, while
    /// every patch knows its outward normal exactly. Only the index order is flipped, so a shared
    /// vertex can serve triangles on either side of a seam. Zero-area triangles — the row of a sphere
    /// octant or torus that collapses to a point — are dropped, since they carry no surface.
    /// </summary>
    private sealed class MeshBuilder
    {
        public readonly List<CloudVertex> Vertices = new(1 << 16);
        public readonly List<uint> Indices = new(1 << 16);
        private readonly Dictionary<(Vector3 Position, Vector3 Normal), uint> _shared = new(1 << 16);

        /// <summary>
        /// Stores one vertex and hands back the index the triangles refer to it by. Identical
        /// position-and-normal pairs collapse onto one entry, which is what lets a fillet strip
        /// reuse the octant's meridian, a wall reuse the fillet's foot, and a flat plateau of cap
        /// tiles keep one vertex per grid point instead of four per tile. The match is exact: a
        /// vertex that merely lands nearby keeps its own normal and stays separate.
        /// </summary>
        public uint Add(Vector3 position, Vector3 normal)
        {
            var key = (position, Vector3.Normalize(normal));
            if (_shared.TryGetValue(key, out var existing)) return existing;

            var index = (uint)Vertices.Count;
            Vertices.Add(new CloudVertex(key.Item1, key.Item2));
            _shared.Add(key, index);
            return index;
        }

        public void Tri(uint i0, uint i1, uint i2)
        {
            var a = Vertices[(int)i0];
            var b = Vertices[(int)i1];
            var c = Vertices[(int)i2];
            var geometric = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            if (geometric.LengthSquared() < 1e-8f) return;
            if (Vector3.Dot(geometric, a.Normal + b.Normal + c.Normal) < 0f) (i1, i2) = (i2, i1);

            Indices.Add(i0);
            Indices.Add(i1);
            Indices.Add(i2);
        }

        public void Quad(uint i0, uint i1, uint i2, uint i3)
        {
            Tri(i0, i1, i2);
            Tri(i0, i2, i3);
        }

        /// <summary>Four corners sharing one normal: the flat cap tiles, walls and ledgeless quads.</summary>
        public void FlatQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            Quad(Add(a, normal), Add(b, normal), Add(c, normal), Add(d, normal));
        }

        /// <summary>Triangle fan from an apex around a rim, every point stored exactly once.</summary>
        public void FlatFan(Vector3 apex, Vector3 normal, ReadOnlySpan<Vector3> rim)
        {
            var centre = Add(apex, normal);
            var previous = Add(rim[0], normal);
            for (var i = 1; i < rim.Length; i++)
            {
                var next = Add(rim[i], normal);
                Tri(centre, previous, next);
                previous = next;
            }
        }
    }

    /// <summary>
    /// Hashed lattice value in [0,1). Coordinates wrap into [0, period) first, which is the whole
    /// trick behind the seamless tiling: opposite edges of the field read the same corners.
    /// </summary>
    private static float Lattice(int ix, int iz, int period, int seed)
    {
        ix = ((ix % period) + period) % period;
        iz = ((iz % period) + period) % period;
        return (SplitMix64.Hash(ix, iz, seed) >> 40) * (1f / (1 << 24));
    }

    /// <summary>Quintic fade: zero slope and curvature at both ends, so the octaves never band.</summary>
    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    /// <summary>Bilinear value noise over the wrapped lattice, result 0..1.</summary>
    private static float ValueNoise(float x, float z, int period, int seed)
    {
        var ix = (int)MathF.Floor(x);
        var iz = (int)MathF.Floor(z);
        var u = Fade(x - ix);
        var v = Fade(z - iz);

        var n00 = Lattice(ix, iz, period, seed);
        var n10 = Lattice(ix + 1, iz, period, seed);
        var n01 = Lattice(ix, iz + 1, period, seed);
        var n11 = Lattice(ix + 1, iz + 1, period, seed);

        var a = n00 + (n10 - n00) * u;
        var b = n01 + (n11 - n01) * u;
        return a + (b - a) * v;
    }

    /// <summary>
    /// Octave sum normalised back to 0..1. Every octave period divides <paramref name="cells"/>, so
    /// the sum is exactly periodic with period <paramref name="cells"/> and the field tiles.
    /// </summary>
    private static float Fbm(float cx, float cz, int cells, int baseSpacingCells, int octaves, int seed)
    {
        if (cells % baseSpacingCells != 0)
            throw new ArgumentException(
                $"A field of {cells} cells is not a multiple of the {baseSpacingCells}-cell octave spacing, so the noise would not tile.",
                nameof(baseSpacingCells));

        var sum = 0f;
        var total = 0f;
        var amplitude = 1f;
        for (var i = 0; i < octaves; i++)
        {
            var spacing = baseSpacingCells >> i;
            if (spacing < 1) break;
            // Spacings are computed dynamically from the cell size now (see Density), so validate
            // every octave, not just the base: a halved spacing that does not divide the field
            // would silently break the tiling below via the truncated cells / spacing period.
            if (cells % spacing != 0)
                throw new ArgumentException(
                    $"A field of {cells} cells is not a multiple of the {spacing}-cell octave spacing, so the noise would not tile.",
                    nameof(baseSpacingCells));

            sum += amplitude * ValueNoise(cx / spacing, cz / spacing, cells / spacing, seed + 7919 * i);
            total += amplitude;
            amplitude *= 0.5f;
        }

        return sum / total;
    }
}
