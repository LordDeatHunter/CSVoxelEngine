using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace silkgltriangle;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Vertex(Vector3 position, byte r, byte g, byte b, byte a, Vector2 uv, float sun = 1f, float block = 0f, Vector3? normal = null, float sway = 0f)
{
    public Vector3 Position = position;
    public uint PackedColor = (uint)(r | (g << 8) | (b << 16) | (a << 24));
    // Atlas UVs as normalised ushorts; every emitter stays inside 0..1 (see CropUv and the
    // baked model UVs), so the 1/65535 step is a twentieth of a texel and never flips a texel.
    public ushort UvX = EncodeUv(uv.X);
    public ushort UvY = EncodeUv(uv.Y);

    /// <summary>Skylight level 0..1, flood-filled from sky-exposed cells at mesh time.</summary>
    public byte Sun = EncodeLight(sun);

    /// <summary>Lamp light level 0..1, flood-filled from emissive blocks at mesh time.</summary>
    public byte Block = EncodeLight(block);

    /// <summary>Face index in <see cref="Chunk.CubeFaces"/> order, reconstructed in the vertex
    /// shader. Sloped fluid tops and spun pebbles snap to their dominant axis; both sit within
    /// a few degrees of one, so the sun diffuse barely moves.</summary>
    public byte Normal = EncodeNormal(normal ?? Vector3.UnitY);

    /// <summary>Wind weight for the leaf-wave shader (1 on canopy leaves, 0 elsewhere).
    /// Chunk meshes set it; standalone cubes (drops, icons, mining pops) stay planted at 0.</summary>
    public byte Sway = sway >= 0.5f ? (byte)255 : (byte)0;

    /// <summary>Decoded atlas UV (tests, debugging); the GPU reads the packed ushorts.</summary>
    public readonly Vector2 UV => new(UvX / 65535f, UvY / 65535f);

    public static readonly uint Size = (uint)Marshal.SizeOf<Vertex>();

    private static ushort EncodeUv(float v) =>
        (ushort)(Math.Clamp(v, 0f, 1f) * 65535f + 0.5f);

    // Light arrives as one of 16 flood-fill levels (level/15), so a byte holds it exactly.
    private static byte EncodeLight(float v) =>
        (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);

    internal static byte EncodeNormal(Vector3 n)
    {
        var ax = MathF.Abs(n.X);
        var ay = MathF.Abs(n.Y);
        var az = MathF.Abs(n.Z);
        if (ay >= ax && ay >= az) return n.Y >= 0f ? (byte)4 : (byte)5;
        if (ax >= az) return n.X >= 0f ? (byte)3 : (byte)2;
        return n.Z >= 0f ? (byte)1 : (byte)0;
    }
}
