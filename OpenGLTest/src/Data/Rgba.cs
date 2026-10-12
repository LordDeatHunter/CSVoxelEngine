using System.Numerics;

namespace VoxelEngine.Data;

/// <summary>
/// Straight (non-premultiplied) 8-bit RGBA color, matching the packing <see cref="Vertex"/>
/// expects. Alpha is carried like any other channel, so a translucent primitive only needs
/// blending enabled — no separate shader path.
/// </summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Rgba White = new(255, 255, 255);
    public static readonly Rgba Black = new(0, 0, 0);
    public static readonly Rgba Transparent = new(0, 0, 0, 0);

    /// <summary>Builds a color from 0..1 components, clamping each.</summary>
    public static Rgba FromFloat(float r, float g, float b, float a = 1f) =>
        new(ToByte(r), ToByte(g), ToByte(b), ToByte(a));

    /// <summary>Uniform grey, handy for UI panels and borders.</summary>
    public static Rgba Grey(float level, float alpha = 1f)
    {
        var v = ToByte(level);
        return new Rgba(v, v, v, ToByte(alpha));
    }

    public Rgba WithAlpha(byte alpha) => this with { A = alpha };

    public Rgba WithAlpha(float alpha) => this with { A = ToByte(alpha) };

    /// <summary>Pack to the uint <see cref="Vertex"/> stores (R in the low byte).</summary>
    public readonly uint Pack() => (uint)(R | (G << 8) | (B << 16) | (A << 24));

    /// <summary>Unpack a <see cref="Pack"/>ed value.</summary>
    public static Rgba FromPacked(uint packed) =>
        new((byte)packed, (byte)(packed >> 8), (byte)(packed >> 16), (byte)(packed >> 24));

    /// <summary>0..1 components, for shader uniforms.</summary>
    public Vector4 ToVector4() => new(R / 255f, G / 255f, B / 255f, A / 255f);

    private static byte ToByte(float value) => (byte)(MathUtils.Clamp01(value) * 255f + 0.5f);
}
