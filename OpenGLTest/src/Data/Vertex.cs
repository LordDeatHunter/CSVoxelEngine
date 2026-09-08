using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace silkgltriangle;

/// <summary>
/// The one vertex layout every textured mesh shares (chunks, LOD, drops, icons, UI, sun).
/// Textures are <c>TEXTURE_2D_ARRAY</c>s: <see cref="Layer"/> picks the slice and the UV is in
/// tile units on it, so a quad spanning W×H cells tiles its texture with UVs running 0..W and
/// 0..H and the sampler's REPEAT does the wrapping. Single-slice textures (item sprites, the
/// font, the crosshair, the sun) use layer 0 and UVs in 0..1 exactly as a plain 2D texture.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Vertex
{
    public Vector3 Position;
    public uint PackedColor;

    /// <summary>UV in texture-tile units: 0..1 across one tile, 0..W across a merged quad.
    /// Kept as floats so both integer extents up to a chunk and the font atlas's sub-texel
    /// glyph rectangles are exact.</summary>
    public Vector2 Uv;

    /// <summary>Skylight level 0..1, flood-filled from sky-exposed cells at mesh time.</summary>
    public byte Sun;

    /// <summary>Lamp light level 0..1, flood-filled from emissive blocks at mesh time.</summary>
    public byte Block;

    /// <summary>Bits 0-2: face index in <see cref="Chunk.CubeFaces"/> order, reconstructed in
    /// the vertex shader. Sloped fluid tops and spun pebbles snap to their dominant axis; both
    /// sit within a few degrees of one, so the sun diffuse barely moves. Bit 3: the face looks
    /// into water, which is what the fragment shader paints the caustic net onto.</summary>
    public byte Normal;

    /// <summary>Wind weight for the leaf-wave shader (1 on canopy leaves, 0 elsewhere).
    /// Chunk meshes set it; standalone cubes (drops, icons, mining pops) stay planted at 0.</summary>
    public byte Sway;

    /// <summary>Array-texture slice this vertex samples: the atlas tile for block geometry,
    /// 0 for every single-slice texture.</summary>
    public ushort Layer;

    /// <summary>Unused; keeps the struct at 32 bytes so every float attribute stays 4-byte
    /// aligned.</summary>
    public ushort Reserved;

    /// <summary>Atlas geometry: a UV in tile units on <paramref name="layer"/>.</summary>
    public Vertex(Vector3 position, byte r, byte g, byte b, byte a, Vector2 uv, ushort layer,
        float sun = 1f, float block = 0f, Vector3? normal = null, float sway = 0f, bool submerged = false)
    {
        Position = position;
        PackedColor = (uint)(r | (g << 8) | (b << 16) | (a << 24));
        Uv = uv;
        Layer = layer;
        Reserved = 0;
        Sun = EncodeLight(sun);
        Block = EncodeLight(block);
        Normal = (byte)(EncodeNormal(normal ?? Vector3.UnitY) | (submerged ? 8 : 0));
        Sway = sway >= 0.5f ? (byte)255 : (byte)0;
    }

    /// <summary>Single-slice texture: a plain 0..1 UV on layer 0 (sprites, font, sun, UI).</summary>
    public Vertex(Vector3 position, byte r, byte g, byte b, byte a, Vector2 uv,
        float sun = 1f, float block = 0f, Vector3? normal = null, float sway = 0f, bool submerged = false)
        : this(position, r, g, b, a, uv, 0, sun, block, normal, sway, submerged)
    {
    }

    /// <summary>The UV as stored (tests, debugging).</summary>
    public readonly Vector2 UV => Uv;

    public static readonly uint Size = (uint)Marshal.SizeOf<Vertex>();

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
