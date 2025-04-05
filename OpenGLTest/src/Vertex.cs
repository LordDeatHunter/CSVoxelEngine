using System.Numerics;
using System.Runtime.InteropServices;

namespace silkgltriangle;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Vertex(Vector3 position, byte r, byte g, byte b, byte a, Vector2 uv)
{
    public Vector3 Position = position;
    public uint PackedColor = (uint)(r | (g << 8) | (b << 16) | (a << 24));
    public Vector2 UV = uv;

    public static readonly uint Size = (uint)Marshal.SizeOf<Vertex>();
}