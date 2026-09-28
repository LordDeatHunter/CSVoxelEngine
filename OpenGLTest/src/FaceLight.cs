using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The smooth corner light of one full cell face (see <see cref="Chunk.SmoothCornerLight"/>),
/// bilinearly interpolated for geometry that sits inside the cell: tapered wood, pebbles, branch
/// piles, snow layers and fluids light each vertex from where it lies on the cell face it takes
/// its light from, so a partial block blends into the full cubes beside it instead of stepping.
/// Each corner is a 12-bit code, <c>sunSum | blockSum &lt;&lt; 6</c> with both sums over four
/// samples (0..60), in <c>Chunk.CubeFaces[Face].face</c> corner order.
/// </summary>
internal readonly struct FaceLight
{
    /// <summary>The face whose corners the codes belong to; it picks the two in-plane axes.</summary>
    public readonly int Face;
    public readonly int C0, C1, C2, C3;

    // Per face and corner: bit 0 = the corner sits at 1 on the first in-plane axis, bit 1 = on
    // the second. Faces 0/1 span (X, Y), 2/3 (Z, Y), 4/5 (X, Z).
    private static readonly byte[] CornerBits = BuildCornerBits();

    private FaceLight(int face, int c0, int c1, int c2, int c3)
    {
        Face = face;
        C0 = c0;
        C1 = c1;
        C2 = c2;
        C3 = c3;
    }

    public static FaceLight FromCorners(int face, int c0, int c1, int c2, int c3) => new(face, c0, c1, c2, c3);

    /// <summary>Uniform light of one cell: every corner holds four samples of the same level, so
    /// <see cref="Sample"/> returns exactly <c>level / 15</c>.</summary>
    public static FaceLight Flat(byte sun, byte block)
    {
        var code = 4 * sun | 4 * block << 6;
        return new FaceLight(4, code, code, code, code);
    }

    /// <summary>Light at a cell-local position, interpolated between the face's four corners
    /// over its two in-plane axes (clamped to the cell).</summary>
    public void Sample(Vector3 local, out float sun, out float block)
    {
        // A uniform face is exact, with no float drift in the weights
        if (C0 == C1 && C0 == C2 && C0 == C3)
        {
            sun = (C0 & 63) / 60f;
            block = (C0 >> 6) / 60f;
            return;
        }

        float u, v;
        switch (Face)
        {
            case 0 or 1: u = local.X; v = local.Y; break;
            case 2 or 3: u = local.Z; v = local.Y; break;
            default: u = local.X; v = local.Z; break;
        }
        u = Math.Clamp(u, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);

        var bits = Face * 4;
        float s = 0f, b = 0f;
        for (var j = 0; j < 4; j++)
        {
            var cb = CornerBits[bits + j];
            var w = ((cb & 1) != 0 ? u : 1f - u) * ((cb & 2) != 0 ? v : 1f - v);
            var c = j switch { 0 => C0, 1 => C1, 2 => C2, _ => C3 };
            s += w * (c & 63);
            b += w * (c >> 6);
        }
        sun = s / 60f;
        block = b / 60f;
    }

    private static byte[] BuildCornerBits()
    {
        var bits = new byte[6 * 4];
        for (var f = 0; f < 6; f++)
        for (var j = 0; j < 4; j++)
        {
            var c = Chunk.CubeFaces[f].face[j];
            var (a, b) = f switch
            {
                0 or 1 => (c.X, c.Y),
                2 or 3 => (c.Z, c.Y),
                _ => (c.X, c.Z),
            };
            bits[f * 4 + j] = (byte)((a > 0.5f ? 1 : 0) | (b > 0.5f ? 2 : 0));
        }
        return bits;
    }
}
