using System;
using System.Numerics;
using System.Runtime.InteropServices;
using VoxelEngine.Chunks;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Simulation;

namespace VoxelEngine.Data;

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

    /// <summary>Low byte of the little-endian ushort the shaders read at attribute 5 (bytes 26-27,
    /// with <see cref="LightTint"/> the high byte). Bits 0-2: face index in
    /// <see cref="Chunk.CubeFaces"/> order, reconstructed in the vertex shader. Sloped fluid tops
    /// and spun pebbles snap to their dominant axis; both sit within a few degrees of one, so the
    /// sun diffuse barely moves. Bit 3: the face looks into water, which is what the fragment
    /// shader paints the caustic net onto. Bit 4: biome tint (<see cref="IsBiomeTinted"/>): the
    /// rgb carries shade * <see cref="BiomeTint"/> (chunks, LOD, the felled tree) or shade alone
    /// with the tint in <c>uBiomeTint</c> (mining pop, debris), and the fragment shader multiplies
    /// the texel by <see cref="BiomeTint.Gain"/>. Bit 5: leaf-wind sway (<see cref="IsSwaying"/>; chunk
    /// meshes set it on canopy leaves and rooted plants, standalone cubes such as drops, icons and
    /// mining pops stay planted), with the alpha byte then holding the sway weight
    /// (<see cref="SwayWeight"/>). Bits 6-7: the low two bits of the 10-bit block-light tint code
    /// (<see cref="BlockTint"/>).</summary>
    public byte Normal;

    /// <summary>High byte of the attribute-5 ushort: bits 2-9 of the 10-bit block-light tint code
    /// (<see cref="BlockTint"/>, <see cref="VoxelEngine.Effects.Lighting.LightTint"/> layout: slot A, slot B and
    /// B's weight), which the world vertex shader decodes against the light palette.</summary>
    public byte LightTint;

    /// <summary>Array-texture slice this vertex samples: the atlas tile for block geometry,
    /// 0 for every single-slice texture.</summary>
    public ushort Layer;

    /// <summary>For a submerged face (<see cref="Normal"/> bit 3): the world Y of the water
    /// surface above it, in quarter blocks offset by <see cref="WaterSurfaceOffset"/>
    /// (<see cref="EncodeWaterSurface"/>); 0 on every other non-fluid face. The caustic net fades with depth
    /// below this, so a pond above sea level measures from its own surface. Also keeps the
    /// struct at 32 bytes.
    /// <para>On a fluid quad (alpha byte 254) the same ushort instead carries the flow code
    /// (<see cref="EncodeWaterFlow"/>), one per vertex, i.e. per top corner of the cell
    /// (<see cref="FluidFlow.Corner"/>); 0 means still. That is safe because the shader reads the
    /// surface meaning only on submerged faces that are not water themselves.</para></summary>
    public ushort WaterSurface;

    /// <summary>Blocks added to a world Y before <see cref="WaterSurface"/> quantises it, so
    /// every surface down to -4096 encodes as a positive ushort. The world vertex shader
    /// mirrors this constant.</summary>
    public const int WaterSurfaceOffset = 4096;

    /// <summary>Quantisation steps per block of <see cref="WaterSurface"/> (quarter blocks).
    /// The world vertex shader mirrors this constant.</summary>
    public const int WaterSurfaceStepsPerBlock = 4;

    /// <summary>Packs a world-space water surface Y into <see cref="WaterSurface"/>. Never
    /// returns 0, which is reserved for "not submerged".</summary>
    public static ushort EncodeWaterSurface(float worldY) =>
        (ushort)Math.Clamp((int)MathF.Round((worldY + WaterSurfaceOffset) * WaterSurfaceStepsPerBlock), 1, ushort.MaxValue);

    /// <summary>Inverse of <see cref="EncodeWaterSurface"/>.</summary>
    public static float DecodeWaterSurface(ushort v) => v / (float)WaterSurfaceStepsPerBlock - WaterSurfaceOffset;

    /// <summary>Quantisation steps per unit of each flow component in
    /// <see cref="EncodeWaterFlow"/>. The world vertex shader mirrors this constant.</summary>
    public const int WaterFlowSteps = 63;

    private const int WaterFlowFallingBit = 1 << 14;

    /// <summary>
    /// Packs a fluid vertex's horizontal flow (length clamped to 1: direction times strength)
    /// and the falling flag into <see cref="WaterSurface"/>: X in bits 0-6 and Z in bits 7-13 as
    /// 7-bit two's complement of <c>round(c * 63)</c>, falling in bit 14. Only (zero, not
    /// falling) packs to 0, so a still, non-falling vertex is bit-identical to every other emitter.
    /// </summary>
    public static ushort EncodeWaterFlow(Vector2 flow, bool falling)
    {
        var length = flow.Length();
        if (length > 1f)
            flow /= length;

        var qx = (int)MathF.Round(flow.X * WaterFlowSteps);
        var qz = (int)MathF.Round(flow.Y * WaterFlowSteps);
        if (qx == 0 && qz == 0 && length > 0f)
        {
            // A trickle too weak to quantise still has to read as moving, not still
            if (MathF.Abs(flow.X) >= MathF.Abs(flow.Y))
                qx = flow.X >= 0f ? 1 : -1;
            else
                qz = flow.Y >= 0f ? 1 : -1;
        }

        qx = Math.Clamp(qx, -WaterFlowSteps, WaterFlowSteps);
        qz = Math.Clamp(qz, -WaterFlowSteps, WaterFlowSteps);
        return (ushort)((qx & 0x7F) | ((qz & 0x7F) << 7) | (falling ? WaterFlowFallingBit : 0));
    }

    /// <summary>Inverse of <see cref="EncodeWaterFlow"/>.</summary>
    public static (Vector2 Flow, bool Falling) DecodeWaterFlow(ushort code)
    {
        static int Signed7(int v) => v >= 64 ? v - 128 : v;
        var qx = Signed7(code & 0x7F);
        var qz = Signed7((code >> 7) & 0x7F);
        return (new Vector2(qx, qz) / WaterFlowSteps, (code & WaterFlowFallingBit) != 0);
    }

    /// <summary>Atlas geometry: a UV in tile units on <paramref name="layer"/>.</summary>
    public Vertex(Vector3 position, byte r, byte g, byte b, byte a, Vector2 uv, ushort layer,
        float sun = 1f, float block = 0f, Vector3? normal = null, float sway = 0f, bool submerged = false,
        ushort waterSurface = 0, ushort tint = 0, bool biomeTint = false)
    {
        System.Diagnostics.Debug.Assert(tint <= TintMask, "block-light tint code exceeds 10 bits");
        tint &= TintMask;
        Position = position;
        PackedColor = (uint)(r | (g << 8) | (b << 16) | (a << 24));
        Uv = uv;
        Layer = layer;
        WaterSurface = waterSurface;
        Sun = EncodeLight(sun);
        Block = EncodeLight(block);
        Normal = (byte)(EncodeNormal(normal ?? Vector3.UnitY) | (submerged ? SubmergedBit : 0)
                        | (biomeTint ? BiomeTintBit : 0)
                        | (sway >= 0.5f ? SwayBit : 0) | ((tint & 3) << 6));
        LightTint = (byte)(tint >> 2);
    }

    /// <summary>Single-slice texture: a plain 0..1 UV on layer 0 (sprites, font, sun, UI).</summary>
    public Vertex(Vector3 position, byte r, byte g, byte b, byte a, Vector2 uv,
        float sun = 1f, float block = 0f, Vector3? normal = null, float sway = 0f, bool submerged = false)
        : this(position, r, g, b, a, uv, 0, sun, block, normal, sway, submerged)
    {
    }

    /// <summary>Atlas geometry with a packed <see cref="Rgba"/> color.</summary>
    public Vertex(Vector3 position, Rgba color, Vector2 uv, ushort layer,
        float sun = 1f, float block = 0f, Vector3? normal = null, float sway = 0f, bool submerged = false)
        : this(position, color.R, color.G, color.B, color.A, uv, layer, sun, block, normal, sway, submerged)
    {
    }

    /// <summary>Single-slice texture with a packed <see cref="Rgba"/> color.</summary>
    public Vertex(Vector3 position, Rgba color, Vector2 uv,
        float sun = 1f, float block = 0f, Vector3? normal = null, float sway = 0f, bool submerged = false)
        : this(position, color.R, color.G, color.B, color.A, uv, 0, sun, block, normal, sway, submerged)
    {
    }

    private const int SubmergedBit = 1 << 3;
    private const int BiomeTintBit = 1 << 4;
    private const int SwayBit = 1 << 5;
    private const ushort TintMask = 0x3FF;

    /// <summary><see cref="Normal"/> bit 3: the face looks into a fluid.</summary>
    public readonly bool IsSubmerged => (Normal & SubmergedBit) != 0;

    /// <summary><see cref="Normal"/> bit 4: a biome-tinted face; the world fragment shader
    /// multiplies its texel by <see cref="BiomeTint.Gain"/>.</summary>
    public readonly bool IsBiomeTinted => (Normal & BiomeTintBit) != 0;

    /// <summary>This vertex recoloured to <paramref name="r"/>/<paramref name="g"/>/
    /// <paramref name="b"/> with the biome-tint flag set, alpha unchanged: the mesher's
    /// <c>modelTint</c> path turns a sub-cell model quad's shade into shade * the palette field.</summary>
    internal Vertex WithBiomeTint(byte r, byte g, byte b)
    {
        var v = this;
        v.PackedColor = (uint)(r | (g << 8) | (b << 16)) | (PackedColor & 0xFF000000u);
        v.Normal |= BiomeTintBit;
        return v;
    }

    /// <summary><see cref="Normal"/> bit 5: the leaf-wave shader sways this vertex.</summary>
    public readonly bool IsSwaying => (Normal & SwayBit) != 0;

    /// <summary>
    /// How far the wind moves this vertex, 0..1: on a swaying vertex (<see cref="IsSwaying"/>) the
    /// alpha byte is its sway weight (<see cref="EncodeSwayWeight"/>), 0 everywhere else. Canopy
    /// leaves carry 255 and sway uniformly; a rooted plant's vertices ease from 0 at the root to
    /// 1 at the tip. The world and shadow vertex shaders read it the same way and then hand the
    /// fragment stage an alpha of 1, so the weight never meets the water/LOD-foliage flags there.
    /// </summary>
    public readonly float SwayWeight => IsSwaying ? (PackedColor >> 24) / 255f : 0f;

    /// <summary>
    /// The alpha byte that carries a sway weight of <paramref name="weight"/> (clamped to 0..1).
    /// A weight that would round to 253 or 254 snaps to a full 255 (a hair at the very tip), so a
    /// swaying vertex never carries the fluid (254) or far-LOD foliage (253) alpha that CPU-side
    /// readers check without looking at the sway bit.
    /// </summary>
    public static byte EncodeSwayWeight(float weight)
    {
        var a = (int)MathF.Round(MathUtils.Clamp01(weight) * 255f);
        return a >= 253 ? (byte)255 : (byte)a;
    }

    /// <summary>The 10-bit block-light tint code (<see cref="VoxelEngine.Effects.Lighting.LightTint"/>), split
    /// across <see cref="Normal"/> bits 6-7 and <see cref="LightTint"/>.</summary>
    public readonly int BlockTint => (Normal >> 6) | (LightTint << 2);

    /// <summary>The UV as stored (tests, debugging).</summary>
    public readonly Vector2 UV => Uv;

    public static readonly uint Size = (uint)Marshal.SizeOf<Vertex>();

    // Light arrives as one of 16 flood-fill levels (level/15), so a byte holds it exactly.
    private static byte EncodeLight(float v) =>
        (byte)(MathUtils.Clamp01(v) * 255f + 0.5f);

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
